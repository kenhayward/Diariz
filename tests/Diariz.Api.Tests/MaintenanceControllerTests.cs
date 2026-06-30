using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Diariz.Api.Controllers;
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
        FakeAudioStorage storage, FakeDatabaseBackup backup, string migration = Migration)
    {
        var ctx = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]);
        return new MaintenanceController(storage, backup, new FakeSchemaVersion(migration)) { ControllerContext = ctx };
    }

    [Fact]
    public void Controller_IsGatedToPlatformAdministrator()
    {
        var attr = typeof(MaintenanceController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(Roles.PlatformAdministrator, attr!.Roles);
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
        Assert.True(backup.DumpCalled);

        using var zip = new ZipArchive(file.FileStream, ZipArchiveMode.Read); // disposing closes + deletes the temp

        var manifest = JsonSerializer.Deserialize<BackupManifest>(ReadEntry(zip, "manifest.json"), JsonOpts)!;
        Assert.Equal(Migration, manifest.MigrationId);
        Assert.False(manifest.IncludesKeyring);
        Assert.Equal("diariz", manifest.App);

        Assert.Equal("PGDUMP-XYZ", Encoding.UTF8.GetString(ReadEntry(zip, "database.dump")));
        Assert.Equal("AUDIO-BYTES", Encoding.UTF8.GetString(ReadEntry(zip, "objects/u1/rec.webm")));
        Assert.Equal("PDF-BYTES", Encoding.UTF8.GetString(ReadEntry(zip, "objects/u1/attachments/a.pdf")));
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

    private static MaintenanceController BuildForRestore(FakeAudioStorage storage, FakeDatabaseBackup backup, Stream body)
    {
        var ctx = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]);
        ctx.HttpContext.Request.Body = body;
        return new MaintenanceController(storage, backup, new FakeSchemaVersion(Migration)) { ControllerContext = ctx };
    }

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

    private static MemoryStream BuildArchive(string migration, string dump, Dictionary<string, string> objects)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(
                    new BackupManifest(1, "diariz", "0.0.0", migration, DateTimeOffset.UtcNow, false), JsonOpts));
            WriteEntry(zip, "database.dump", Encoding.UTF8.GetBytes(dump));
            foreach (var (key, value) in objects)
                WriteEntry(zip, "objects/" + key, Encoding.UTF8.GetBytes(value));
        }
        ms.Position = 0;
        return ms;
    }
}
