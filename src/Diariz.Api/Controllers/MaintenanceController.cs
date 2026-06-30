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
[Authorize(Roles = Roles.PlatformAdministrator)]
[Route("api/maintenance")]
public class MaintenanceController : ControllerBase
{
    private readonly IAudioStorage _storage;
    private readonly IDatabaseBackup _backup;
    private readonly ISchemaVersion _schema;

    private const string ManifestEntry = "manifest.json";
    private const string DumpEntry = "database.dump";
    private const string ObjectPrefix = "objects/";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public MaintenanceController(IAudioStorage storage, IDatabaseBackup backup, ISchemaVersion schema)
    {
        _storage = storage;
        _backup = backup;
        _schema = schema;
    }

    /// <summary>Stream the full platform backup as a <c>.zip</c>. Token via <c>access_token</c> query (an
    /// anchor-href download can't set an Authorization header), like the audio endpoint.</summary>
    [HttpGet("backup")]
    public async Task<IActionResult> Backup(CancellationToken ct = default)
    {
        var manifest = new BackupManifest(
            Format: 1,
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
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var ms = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(ms, manifest, JsonOpts, ct);

                var dumpEntry = zip.CreateEntry(DumpEntry, CompressionLevel.Optimal);
                await using (var ds = dumpEntry.Open())
                    await _backup.DumpToAsync(ds, ct);

                await foreach (var key in _storage.ListKeysAsync(ct))
                {
                    var blob = await _storage.OpenAsync(key, ct: ct);
                    if (blob is null) continue;
                    // Audio/attachments are already compressed — store, don't deflate again.
                    var entry = zip.CreateEntry(ObjectPrefix + key, CompressionLevel.NoCompression);
                    await using var src = blob.Content;
                    await using var dest = entry.Open();
                    await src.CopyToAsync(dest, ct);
                }
            }
        }
        catch
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
            throw;
        }

        var name = $"diariz-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
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

            // pg_restore brings the dump's schema, which must match the running code.
            var current = await _schema.CurrentAsync(ct);
            if (!string.IsNullOrEmpty(current) && manifest.MigrationId != current)
                return BadRequest(
                    $"This backup was taken on a different schema version ({manifest.MigrationId}); this " +
                    $"instance is at {current}. Restore is only supported on a matching version.");

            var dumpEntry = zip.GetEntry(DumpEntry);
            if (dumpEntry is null) return BadRequest("Invalid backup: database.dump is missing.");
            await using (var ds = dumpEntry.Open())
                await _backup.RestoreFromAsync(ds, ct);

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
            return Ok(new { restored = true });
        }
        finally { if (System.IO.File.Exists(archive)) System.IO.File.Delete(archive); }
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
