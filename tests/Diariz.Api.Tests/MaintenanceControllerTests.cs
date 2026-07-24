using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class MaintenanceControllerTests
{
    private const string Migration = "20260615111923_InitialCreate";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static MaintenanceController Build(
        FakeAudioStorage storage, FakeDatabaseBackup backup, string migration = Migration,
        BackupProgress? progress = null)
    {
        var ctx = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]);
        return new MaintenanceController(storage, backup, new FakeSchemaVersion(migration), progress ?? new BackupProgress())
        {
            ControllerContext = ctx,
        };
    }

    [Fact]
    public void Controller_IsGatedToPlatformAdministrator()
    {
        // Backup/restore requires ManagePlatform, which only the Platform Administrators group confers.
        // An Administrator (ManageRooms | ManageUsers) must never reach it.
        var attr = typeof(MaintenanceController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("ManagePlatform", attr!.Policy);
        Assert.Null(attr.Roles);
    }

    [Fact]
    public async Task Backup_WritesManifest_DatabaseDump_AndOneEntryPerObject()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["u1/rec.webm"] = Encoding.UTF8.GetBytes("AUDIO-BYTES");
        storage.Objects["u1/attachments/a.pdf"] = Encoding.UTF8.GetBytes("PDF-BYTES");
        var backup = new FakeDatabaseBackup { DumpBytes = Encoding.UTF8.GetBytes("PGDUMP-XYZ") };

        var result = await Build(storage, backup).Backup();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", file.ContentType);
        // The download name carries the app version as Vx_xx_x (e.g. diariz-backup-…-V0_47_0.zip).
        Assert.Matches(@"^diariz-backup-\d{8}-\d{6}-V\d+_\d+_\d+\.zip$", file.FileDownloadName);
        Assert.True(backup.DumpCalled);

        using var zip = new ZipArchive(file.FileStream, ZipArchiveMode.Read); // disposing closes + deletes the temp

        var manifest = JsonSerializer.Deserialize<BackupManifest>(ReadEntry(zip, "manifest.json"), JsonOpts)!;
        Assert.Equal(Migration, manifest.MigrationId);
        Assert.Equal(MaintenanceController.CurrentFormat, manifest.Format); // the compatibility epoch
        Assert.False(manifest.IncludesKeyring);
        Assert.Equal("diariz", manifest.App);

        Assert.Equal("PGDUMP-XYZ", Encoding.UTF8.GetString(ReadEntry(zip, "database.dump")));
        Assert.Equal("AUDIO-BYTES", Encoding.UTF8.GetString(ReadEntry(zip, "objects/u1/rec.webm")));
        Assert.Equal("PDF-BYTES", Encoding.UTF8.GetString(ReadEntry(zip, "objects/u1/attachments/a.pdf")));
    }

    [Fact]
    public async Task Backup_ReportsProgressWhileAssembling_ThenReturnsToIdle()
    {
        // The whole archive is built before the first response byte, so the browser shows nothing for what
        // can be minutes. The progress tracker is what the Maintenance panel polls to say "this is running".
        var storage = new FakeAudioStorage();
        storage.Objects["u1/a.webm"] = Encoding.UTF8.GetBytes("A");
        storage.Objects["u1/b.webm"] = Encoding.UTF8.GetBytes("B");
        var progress = new BackupProgress();
        var sampled = new List<BackupProgressSnapshot>();
        storage.OnKeyListed = _ => sampled.Add(progress.Current);

        var result = await Build(storage, new FakeDatabaseBackup(), progress: progress).Backup();
        Assert.IsType<FileStreamResult>(result).FileStream.Dispose(); // closes + deletes the temp archive

        Assert.Equal(2, sampled.Count);
        Assert.All(sampled, s => Assert.True(s.Running));
        Assert.All(sampled, s => Assert.Equal(BackupPhase.Objects, s.Phase));
        Assert.Equal(0, sampled[0].ObjectsArchived);  // sampled as the first key is handed over
        Assert.Equal(1, sampled[1].ObjectsArchived);  // the first object is archived before the second is listed
        Assert.False(progress.Current.Running);       // idle again once the archive is built
    }

    [Fact]
    public void BackupStatus_ReturnsTheCurrentSnapshot()
    {
        var progress = new BackupProgress();
        using var scope = progress.Begin();
        progress.SetPhase(BackupPhase.Objects);
        progress.ObjectArchived();

        var result = Build(new FakeAudioStorage(), new FakeDatabaseBackup(), progress: progress).BackupStatus();

        var snapshot = Assert.IsType<BackupProgressSnapshot>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(snapshot.Running);
        Assert.Equal(BackupPhase.Objects, snapshot.Phase);
        Assert.Equal(1, snapshot.ObjectsArchived);
    }

    [Fact]
    public async Task Restore_LoadsDump_AndReplacesObjectStore()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["stale/old.webm"] = Encoding.UTF8.GetBytes("STALE"); // must be wiped
        var backup = new FakeDatabaseBackup();

        var archive = BuildArchive(Migration, "NEW-DUMP", new()
        {
            ["u9/new.webm"] = "NEW-AUDIO",
            ["u9/attachments/doc.pdf"] = "NEW-PDF",
        });

        var result = await BuildForRestore(storage, backup, archive).Restore();

        Assert.IsType<OkObjectResult>(result);
        Assert.True(backup.RestoreCalled);
        Assert.Equal("NEW-DUMP", Encoding.UTF8.GetString(backup.RestoredDump!));
        // Object store replaced: the stale key is gone, the archive's objects are present with their bytes.
        Assert.False(storage.Objects.ContainsKey("stale/old.webm"));
        Assert.Equal("NEW-AUDIO", Encoding.UTF8.GetString(storage.Objects["u9/new.webm"]));
        Assert.Equal("NEW-PDF", Encoding.UTF8.GetString(storage.Objects["u9/attachments/doc.pdf"]));
    }

    [Fact]
    public async Task Restore_RejectsBackupFromADifferentSchemaVersion()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["keep/x.webm"] = Encoding.UTF8.GetBytes("KEEP");
        var backup = new FakeDatabaseBackup();
        var archive = BuildArchive("20991231_SomeOtherMigration", "DUMP", new());

        var result = await BuildForRestore(storage, backup, archive).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);                       // nothing restored
        Assert.True(storage.Objects.ContainsKey("keep/x.webm"));  // object store untouched
    }

    // ---- Cross-version compatibility (Format epoch + migration ancestor check) ----

    private static readonly string[] History = ["m1", "m2", "m3"];

    [Fact]
    public async Task Restore_SameVersion_RestoresWithoutMigratingForward()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m3", History);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, BuildArchive("m3", "D", new()), schema).Restore();

        Assert.IsType<OkObjectResult>(result);
        Assert.True(backup.RestoreCalled);
        Assert.False(schema.Migrated); // identical schema - no roll-forward
    }

    [Fact]
    public async Task Restore_OlderAncestor_RestoresThenMigratesForward()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m3", History);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, BuildArchive("m1", "D", new()), schema).Restore();

        Assert.IsType<OkObjectResult>(result);
        Assert.True(backup.RestoreCalled);
        Assert.True(schema.Migrated); // rolled m1 -> m3
    }

    [Fact]
    public async Task Restore_NewerMigration_IsRejected()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["keep/x"] = Encoding.UTF8.GetBytes("KEEP");
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m2", History); // this instance is behind the backup
        var result = await BuildForRestore(storage, backup, BuildArchive("m3", "D", new()), schema).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);
        Assert.False(schema.Migrated);
        Assert.True(storage.Objects.ContainsKey("keep/x"));
    }

    [Fact]
    public async Task Restore_UnknownMigration_IsRejected()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m3", History);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, BuildArchive("m9-divergent", "D", new()), schema).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);
    }

    [Fact]
    public async Task Restore_OlderFormat_IsRejected()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m1", History);
        var archive = BuildArchive("m1", "D", new(), format: MaintenanceController.CurrentFormat - 1);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, archive, schema).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);
    }

    [Fact]
    public async Task Restore_NewerFormat_IsRejected()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m1", History);
        var archive = BuildArchive("m1", "D", new(), format: MaintenanceController.CurrentFormat + 1);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, archive, schema).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);
    }

    [Fact]
    public async Task Restore_OlderAncestor_ReportsTheRollForward()
    {
        var backup = new FakeDatabaseBackup();
        var schema = Schema("m3", History);
        var result = await BuildForRestore(new FakeAudioStorage(), backup, BuildArchive("m1", "D", new()), schema).Restore();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"migratedFrom\":\"m1\"", json);
        Assert.Contains("\"migratedTo\":\"m3\"", json);
        Assert.Contains("\"restartRecommended\":true", json);
    }

    [Fact]
    public async Task Restore_RejectsArchiveMissingTheDump()
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            WriteEntry(zip, "manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(
                    new BackupManifest(1, "diariz", "0.0.0", Migration, DateTimeOffset.UtcNow, false), JsonOpts));
        archive.Position = 0;
        var backup = new FakeDatabaseBackup();

        var result = await BuildForRestore(new FakeAudioStorage(), backup, archive).Restore();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(backup.RestoreCalled);
    }

    // ---- helpers ----

    private static MaintenanceController BuildForRestore(
        FakeAudioStorage storage, FakeDatabaseBackup backup, Stream body, FakeSchemaVersion? schema = null)
    {
        var ctx = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]);
        ctx.HttpContext.Request.Body = body;
        return new MaintenanceController(storage, backup, schema ?? new FakeSchemaVersion(Migration), new BackupProgress())
        {
            ControllerContext = ctx,
        };
    }

    private static FakeSchemaVersion Schema(string current, params string[] known) =>
        new(current) { Known = known.Length == 0 ? new() { current } : known.ToList() };

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var s = entry!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    private static MemoryStream BuildArchive(
        string migration, string dump, Dictionary<string, string> objects, int format = MaintenanceController.CurrentFormat)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(
                    new BackupManifest(format, "diariz", "0.0.0", migration, DateTimeOffset.UtcNow, false), JsonOpts));
            WriteEntry(zip, "database.dump", Encoding.UTF8.GetBytes(dump));
            foreach (var (key, value) in objects)
                WriteEntry(zip, "objects/" + key, Encoding.UTF8.GetBytes(value));
        }
        ms.Position = 0;
        return ms;
    }
}
