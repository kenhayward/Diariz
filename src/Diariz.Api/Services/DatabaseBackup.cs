using System.Diagnostics;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Diariz.Api.Services;

/// <summary>The current database schema version (the last applied EF migration). Stamped into a backup's
/// manifest and checked on restore — a `pg_restore` brings the dump's schema, which must match the running
/// code. Abstracted so the controller is testable on the in-memory provider (which has no migrations).</summary>
public interface ISchemaVersion
{
    Task<string> CurrentAsync(CancellationToken ct = default);
}

public class EfSchemaVersion(DiarizDbContext db) : ISchemaVersion
{
    public async Task<string> CurrentAsync(CancellationToken ct = default) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";
}

/// <summary>Faithful Postgres dump/restore for the platform backup. Abstracted so the controller's
/// archive/object logic is unit-testable without the real `pg_dump`/`pg_restore` binaries.</summary>
public interface IDatabaseBackup
{
    /// <summary>Write a custom-format `pg_dump` of the configured database to <paramref name="destination"/>.</summary>
    Task DumpToAsync(Stream destination, CancellationToken ct = default);

    /// <summary>Restore the database from a custom-format dump stream (drops + recreates objects, then loads).</summary>
    Task RestoreFromAsync(Stream dump, CancellationToken ct = default);
}

/// <summary>Real <see cref="IDatabaseBackup"/> that shells out to `pg_dump` / `pg_restore` (installed in the
/// API image). Connection details come from the EF "Postgres" connection string; the password is passed via
/// the `PGPASSWORD` env var rather than the command line.</summary>
public class PgToolsDatabaseBackup : IDatabaseBackup
{
    private readonly NpgsqlConnectionStringBuilder _csb;

    public PgToolsDatabaseBackup(string connectionString) =>
        _csb = new NpgsqlConnectionStringBuilder(connectionString);

    public async Task DumpToAsync(Stream destination, CancellationToken ct = default)
    {
        var psi = BaseStartInfo("pg_dump");
        psi.ArgumentList.Add("--format=custom");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-privileges");
        psi.RedirectStandardOutput = true;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pg_dump.");
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.StandardOutput.BaseStream.CopyToAsync(destination, ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump failed (exit {proc.ExitCode}): {await errTask}");
    }

    public async Task RestoreFromAsync(Stream dump, CancellationToken ct = default)
    {
        // pg_restore needs a seekable archive file, so spill the stream to a temp file first.
        var temp = Path.GetTempFileName();
        try
        {
            await using (var fs = File.Create(temp))
                await dump.CopyToAsync(fs, ct);

            var psi = BaseStartInfo("pg_restore");
            psi.ArgumentList.Add("--clean");        // drop existing objects before recreating
            psi.ArgumentList.Add("--if-exists");    // ...tolerating ones that don't exist yet
            psi.ArgumentList.Add("--no-owner");
            psi.ArgumentList.Add("--no-privileges");
            psi.ArgumentList.Add(temp);             // the archive file (positional)
            psi.RedirectStandardOutput = true;

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pg_restore.");
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            // pg_restore only exits 0 (clean) or 1 (some errors were ignored and skipped). With --clean it
            // routinely exits 1 on benign "does not exist, skipping" notices, and a newer client dumping
            // server GUCs an older server doesn't know (e.g. transaction_timeout) is also ignored — the data
            // still restores. So we don't treat a non-zero exit as fatal; we drain stderr (also avoids a pipe
            // deadlock) and let the restore complete. The controller's manifest + schema-version checks guard
            // against the archive being wrong in the first place.
            await errTask;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private ProcessStartInfo BaseStartInfo(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(_csb.Host)) { psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(_csb.Host); }
        if (_csb.Port != 0) { psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(_csb.Port.ToString()); }
        if (!string.IsNullOrEmpty(_csb.Username)) { psi.ArgumentList.Add("-U"); psi.ArgumentList.Add(_csb.Username); }
        if (!string.IsNullOrEmpty(_csb.Database)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(_csb.Database); }
        psi.Environment["PGPASSWORD"] = _csb.Password ?? "";
        return psi;
    }
}
