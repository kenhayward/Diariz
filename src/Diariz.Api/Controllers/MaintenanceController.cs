using System.IO.Compression;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Whole-platform backup &amp; restore (Postgres + all object-store blobs) as one transferable
/// <c>.zip</c>. Platform-Administrator only — restore wipes and replaces ALL data. The archive contains
/// <c>manifest.json</c>, a <c>pg_dump</c> custom-format <c>database.dump</c>, and one <c>objects/&lt;key&gt;</c>
/// entry per stored blob. The Data-Protection keyring is deliberately NOT included, so after restoring on a
/// different instance the encrypted per-user LLM API keys can't be decrypted (users re-enter them).</summary>
[ApiController]
[Authorize(Policy = "ManagePlatform")]
[Route("api/maintenance")]
public class MaintenanceController : ControllerBase
{
    private readonly IAudioStorage _storage;
    private readonly IDatabaseBackup _backup;
    private readonly ISchemaVersion _schema;
    private readonly IBackupProgress _progress;

    private const string ManifestEntry = "manifest.json";
    private const string DumpEntry = "database.dump";
    private const string ObjectPrefix = "objects/";

    /// <summary>Backup archive compatibility epoch. Bump ONLY when a migration is not forward-restore-safe
    /// (a destructive drop/rename, a pgvector dimension change, a semantic data reshape); a mismatch is
    /// hard-rejected on restore. Within one Format, the migration ancestor check governs which older
    /// backups can be rolled forward.</summary>
    public const int CurrentFormat = 1;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public MaintenanceController(
        IAudioStorage storage, IDatabaseBackup backup, ISchemaVersion schema, IBackupProgress progress)
    {
        _storage = storage;
        _backup = backup;
        _schema = schema;
        _progress = progress;
    }

    /// <summary>Whether an archive is being assembled right now, and how far in. The Maintenance panel polls
    /// this while <see cref="Backup"/> is in flight - that request sends no bytes until the whole zip is
    /// built, so this is the only signal the browser has that anything is happening.</summary>
    [HttpGet("backup/status")]
    public ActionResult<BackupProgressSnapshot> BackupStatus() => Ok(_progress.Current);

    /// <summary>Stream the full platform backup as a <c>.zip</c>. Token via <c>access_token</c> query (an
    /// anchor-href download can't set an Authorization header), like the audio endpoint.</summary>
    [HttpGet("backup")]
    public async Task<IActionResult> Backup(CancellationToken ct = default)
    {
        var manifest = new BackupManifest(
            Format: CurrentFormat,
            App: "diariz",
            Version: typeof(MaintenanceController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            MigrationId: await _schema.CurrentAsync(ct),
            CreatedAt: DateTimeOffset.UtcNow,
            IncludesKeyring: false);

        // Build to a temp file first, then stream it back. ZipArchive writes its headers/central-directory
        // SYNCHRONOUSLY, which Kestrel's response body forbids (AllowSynchronousIO is off) — so we can't write
        // the archive directly to Response.Body. Writing to a FileStream (sync IO is fine on a real file) and
        // returning File(...) (async copy) sidesteps that, and means a pg_dump failure surfaces as a clean 500
        // instead of a truncated download. The file is deleted when the response finishes (DeleteOnClose).
        var tempPath = Path.Combine(Path.GetTempPath(), $"diariz-backup-{Guid.NewGuid():N}.zip");
        // Assembling the archive is the long, invisible part of a download - report it so the admin UI can
        // show that a backup is running. The scope ends when the zip is built (the transfer that follows is
        // the browser's own visible download).
        using var tracked = _progress.Begin();
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var ms = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(ms, manifest, JsonOpts, ct);

                _progress.SetPhase(BackupPhase.Database);
                var dumpEntry = zip.CreateEntry(DumpEntry, CompressionLevel.Optimal);
                await using (var ds = dumpEntry.Open())
                    await _backup.DumpToAsync(ds, ct);

                _progress.SetPhase(BackupPhase.Objects);
                await foreach (var key in _storage.ListKeysAsync(ct))
                {
                    var blob = await _storage.OpenAsync(key, ct: ct);
                    if (blob is null) continue;
                    // Audio/attachments are already compressed — store, don't deflate again.
                    var entry = zip.CreateEntry(ObjectPrefix + key, CompressionLevel.NoCompression);
                    await using (var src = blob.Content)
                    await using (var dest = entry.Open())
                        await src.CopyToAsync(dest, ct);
                    _progress.ObjectArchived();
                }
            }
        }
        catch
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
            throw;
        }

        // Tag the filename with the app version (Vx_xx_x) so restores can be matched to a build at a glance.
        var ver = "V" + (typeof(MaintenanceController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Replace('.', '_');
        var name = $"diariz-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{ver}.zip";
        var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return File(stream, "application/zip", name);
    }

    /// <summary>Replace the platform with the uploaded backup (raw <c>application/zip</c> body). Destructive:
    /// drops &amp; reloads the DB and wipes &amp; reuploads the object store. The caller is signed out afterwards.</summary>
    [HttpPost("restore")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Restore(CancellationToken ct = default)
    {
        var archive = Path.Combine(Path.GetTempPath(), $"diariz-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fs = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
                await Request.Body.CopyToAsync(fs, ct);

            using var zip = ZipFile.OpenRead(archive);

            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry is null) return BadRequest("Invalid backup: manifest.json is missing.");
            BackupManifest? manifest;
            await using (var ms = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOpts, ct);
            if (manifest is null) return BadRequest("Invalid backup: manifest.json is unreadable.");

            // Decide whether this backup can be applied here: same Format epoch, and a same-or-older
            // (forward-migratable) schema. An older ancestor is rolled forward after the dump loads.
            var current = await _schema.CurrentAsync(ct);
            var (ok, needMigrate, error) = EvaluateCompatibility(manifest, current, _schema.KnownMigrations);
            if (!ok) return BadRequest(error);

            var dumpEntry = zip.GetEntry(DumpEntry);
            if (dumpEntry is null) return BadRequest("Invalid backup: database.dump is missing.");
            await using (var ds = dumpEntry.Open())
                await _backup.RestoreFromAsync(ds, ct);

            // The dump landed the backup's (older) schema + __EFMigrationsHistory; roll it up to this build.
            if (needMigrate) await _schema.MigrateToCurrentAsync(ct);

            // Replace the object store: wipe what's there, then upload the archive's objects.
            var existing = new List<string>();
            await foreach (var key in _storage.ListKeysAsync(ct)) existing.Add(key);
            foreach (var key in existing) await _storage.DeleteAsync(key, ct);

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(ObjectPrefix, StringComparison.Ordinal)) continue;
                var key = entry.FullName[ObjectPrefix.Length..];
                if (key.Length == 0) continue; // a directory placeholder
                // Spill each object to a temp file (seekable + known length for PutObject; no memory blowup).
                var objTemp = Path.GetTempFileName();
                try
                {
                    await using (var ofs = new FileStream(objTemp, FileMode.Create, FileAccess.Write, FileShare.None))
                    await using (var src = entry.Open())
                        await src.CopyToAsync(ofs, ct);
                    await using var read = new FileStream(objTemp, FileMode.Open, FileAccess.Read, FileShare.None);
                    await _storage.UploadAsync(key, read, ContentTypeForKey(key), ct);
                }
                finally { if (System.IO.File.Exists(objTemp)) System.IO.File.Delete(objTemp); }
            }
            return Ok(new
            {
                restored = true,
                migratedFrom = manifest.MigrationId,
                migratedTo = current,
                restartRecommended = needMigrate,
            });
        }
        finally { if (System.IO.File.Exists(archive)) System.IO.File.Delete(archive); }
    }

    /// <summary>Decide whether a backup can be restored onto the running instance. Returns whether to accept,
    /// whether a forward-migration is needed afterwards, and (on rejection) a message. Same <see cref="CurrentFormat"/>
    /// is required; within it, the backup's migration must be this build's current one or an earlier ancestor
    /// (newer/unknown schemas are refused - there are no down-migrations). Skipped when there is no migration
    /// history (the in-memory test provider).</summary>
    private static (bool Ok, bool Migrate, string? Error) EvaluateCompatibility(
        BackupManifest manifest, string current, IReadOnlyList<string> known)
    {
        if (string.IsNullOrEmpty(current)) return (true, false, null);

        if (manifest.Format != CurrentFormat)
            return (false, false, manifest.Format < CurrentFormat
                ? $"This backup (format {manifest.Format}) predates a breaking change; this instance is format " +
                  $"{CurrentFormat}. It can't be restored on this version."
                : $"This backup is from a newer app (format {manifest.Format}); upgrade this instance before restoring.");

        if (manifest.MigrationId == current) return (true, false, null); // identical schema

        var list = known as IList<string> ?? known.ToList();
        int idxBackup = list.IndexOf(manifest.MigrationId);
        int idxCurrent = list.IndexOf(current);
        if (idxBackup < 0)
            return (false, false, $"This backup's schema version ({manifest.MigrationId}) is not recognised by this build.");
        if (idxCurrent < 0 || idxBackup > idxCurrent)
            return (false, false,
                $"This backup ({manifest.MigrationId}) is newer than this instance ({current}); upgrade the app first.");
        return (true, true, null); // older ancestor - restore, then migrate forward
    }

    /// <summary>A reasonable content-type from the key's extension. Serving uses the DB's stored content-type
    /// (this is only the object-store fallback), so an approximate value is fine.</summary>
    private static string ContentTypeForKey(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".webm" => "audio/webm",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".flac" => "audio/flac",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}

/// <summary>Backup archive metadata (in <c>manifest.json</c>). <c>MigrationId</c> is the schema version the
/// backup was taken at; restore refuses a mismatch. <c>IncludesKeyring</c> is always false (by design).</summary>
public record BackupManifest(
    int Format, string App, string Version, string MigrationId, DateTimeOffset CreatedAt, bool IncludesKeyring);
