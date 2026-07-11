# Cross-Version Backup Restore - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Platform Administrator restore a backup taken on an older but forward-compatible schema, rolling its data up to the running code's schema, while hard-rejecting anything that can't be applied safely.

**Architecture:** Keep the existing `pg_dump`/`pg_restore` archive. Replace the exact-match `MigrationId` gate in `MaintenanceController.Restore` with (1) a `Format` epoch fence and (2) an ancestor check over the build's migration list; when the backup is an older ancestor, run `Database.MigrateAsync()` after `pg_restore` to migrate forward. Report `{ migratedFrom, migratedTo, restartRecommended }`.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core (Npgsql), xUnit + Testcontainers, React 19 + vitest.

**Decisions locked (from the design's open questions):**
1. **Restart handling:** inline forward-migrate + a `restartRecommended` flag; the admin restarts manually. No auto-restart, no maintenance-mode gate.
2. **History depth:** bounded by the `Format` epoch - the `Format` fence is the boundary; only same-`Format` backups are forward-restored.
3. **Keyring:** stays excluded. Login never depends on it (password hashes live in the dump; JWTs are signed with `Jwt:Key` from config), so it's out of scope here.
4. **Result:** summary fields + a one-line result in the Maintenance UI.

**Reference:** design doc `docs/superpowers/specs/2026-07-11-backup-restore-cross-version-design.md`.

---

## File Structure

- `src/Diariz.Api/Services/DatabaseBackup.cs` - extend `ISchemaVersion` (add `KnownMigrations`, `MigrateToCurrentAsync`); implement in `EfSchemaVersion`.
- `src/Diariz.Api/Controllers/MaintenanceController.cs` - `CurrentFormat` constant; use it in the backup manifest; rewrite the restore compatibility gate + add forward-migrate + result payload; add a private `EvaluateCompatibility` helper.
- `tests/Diariz.Api.TestSupport/Fakes.cs` - extend `FakeSchemaVersion` (`Known`, `Migrated`, new members).
- `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs` - the acceptance/rejection matrix + forward-migrate + payload tests.
- `tests/Diariz.Api.IntegrationTests/DatabaseBackupIntegrationTests.cs` - a real EF forward-migrate test (migrate to an early target, then `MigrateToCurrentAsync`, assert).
- `apps/web/src/components/MaintenancePanel.tsx` (+ `MaintenancePanel.test.tsx`) - surface the restore result line.
- `apps/web/src/locales/{en,de,es,fr}/account.json` - result-message keys.
- Version + release notes: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`.
- `CLAUDE.md` - one line to the versioning section (breaking migration must bump `Format`).

---

## Task 1: Extend `ISchemaVersion` with ordering + a migrate primitive

**Files:**
- Modify: `src/Diariz.Api/Services/DatabaseBackup.cs:11-20`
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs:572-576`

- [ ] **Step 1: Extend the interface + real implementation**

Replace the `ISchemaVersion` interface and `EfSchemaVersion` (lines 11-20):

```csharp
/// <summary>The database's schema state, for stamping backups and gating restore. Abstracted so the
/// controller is testable on the in-memory provider (which has no migrations).</summary>
public interface ISchemaVersion
{
    /// <summary>The last applied EF migration (empty on the in-memory provider).</summary>
    Task<string> CurrentAsync(CancellationToken ct = default);

    /// <summary>Every migration this build knows, in dependency order (compile-time list, no DB hit).</summary>
    IReadOnlyList<string> KnownMigrations { get; }

    /// <summary>Apply any pending migrations, bringing the database up to this build's latest schema.</summary>
    Task MigrateToCurrentAsync(CancellationToken ct = default);
}

public class EfSchemaVersion(DiarizDbContext db) : ISchemaVersion
{
    public async Task<string> CurrentAsync(CancellationToken ct = default) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";

    public IReadOnlyList<string> KnownMigrations => db.Database.GetMigrations().ToList();

    public Task MigrateToCurrentAsync(CancellationToken ct = default) => db.Database.MigrateAsync(ct);
}
```

- [ ] **Step 2: Extend `FakeSchemaVersion`** (Fakes.cs:572-576)

```csharp
public sealed class FakeSchemaVersion(string current = "20260615111923_InitialCreate") : ISchemaVersion
{
    public string Current { get; set; } = current;
    /// The ordered migration list this "build" knows. Defaults to just the baseline.
    public List<string> Known { get; set; } = new() { current };
    /// Set true when MigrateToCurrentAsync is called - so tests can assert a forward-migrate ran.
    public bool Migrated { get; private set; }

    public Task<string> CurrentAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public IReadOnlyList<string> KnownMigrations => Known;
    public Task MigrateToCurrentAsync(CancellationToken ct = default) { Migrated = true; return Task.CompletedTask; }
}
```

- [ ] **Step 3: Verify it compiles + existing tests still pass**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded.
Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceController"`
Expected: PASS (behaviour unchanged - the new members aren't used yet).

- [ ] **Step 4: Commit**

```bash
git add src/Diariz.Api/Services/DatabaseBackup.cs tests/Diariz.Api.TestSupport/Fakes.cs
git commit -m "refactor: extend ISchemaVersion with KnownMigrations + MigrateToCurrentAsync"
```

---

## Task 2: `CurrentFormat` constant + use it in the backup manifest

**Files:**
- Modify: `src/Diariz.Api/Controllers/MaintenanceController.cs:24-26` (constants) and `:41` (manifest write)
- Test: `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs`

- [ ] **Step 1: Write the failing test** (add to `MaintenanceControllerTests`)

```csharp
[Fact]
public async Task Backup_StampsTheCurrentFormat()
{
    var (controller, _, _) = BuildBackup(); // reuse the existing backup test's arrange helper
    var result = (FileContentResult)await controller.Backup();
    using var zip = new ZipArchive(new MemoryStream(result.FileContents));
    var manifest = await ReadManifest(zip); // existing helper that deserialises manifest.json
    Assert.Equal(MaintenanceController.CurrentFormat, manifest.Format);
    Assert.True(manifest.Format >= 1);
}
```

(If the existing backup test already reads the manifest, assert `manifest.Format == MaintenanceController.CurrentFormat` there instead of a new test.)

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name~Backup_StampsTheCurrentFormat"`
Expected: FAIL - `MaintenanceController.CurrentFormat` does not exist.

- [ ] **Step 3: Add the constant + use it**

In `MaintenanceController` add near the other constants (line 24):

```csharp
/// <summary>Backup archive compatibility epoch. Bump ONLY when a migration is not forward-restore-safe
/// (a destructive drop/rename, a pgvector dimension change, a semantic data reshape); a mismatch is
/// hard-rejected on restore. Within one Format, the migration ancestor check governs.</summary>
public const int CurrentFormat = 1;
```

In `Backup`, change `Format: 1,` to `Format: CurrentFormat,`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceController"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/MaintenanceController.cs tests/Diariz.Api.Tests/MaintenanceControllerTests.cs
git commit -m "feat: stamp a CurrentFormat epoch into the backup manifest"
```

---

## Task 3: Restore compatibility policy + forward-migrate + result payload

This is the core change. The gate becomes: Format fence -> ancestor check -> (older) forward-migrate.

**Files:**
- Modify: `src/Diariz.Api/Controllers/MaintenanceController.cs:116-120` (the gate) and `:149` (the return), plus a new private helper.
- Test: `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs`

- [ ] **Step 1: Write the failing tests** (the full matrix + forward-migrate + payload)

Use `FakeSchemaVersion` with `Known = ["m1","m2","m3"]`. Assume a restore-arrange helper `BuildRestore(FakeSchemaVersion schema)` that returns `(controller, FakeAudioStorage storage, FakeDatabaseBackup backup)` and a helper `MakeArchive(BackupManifest manifest)` producing a zip stream with `manifest.json` + a `database.dump` entry + one `objects/<key>`. (Mirror the existing restore test's arrange.)

```csharp
private static FakeSchemaVersion Schema(string current, params string[] known) =>
    new(current) { Known = known.Length == 0 ? new() { current } : known.ToList() };

private static BackupManifest Manifest(string migrationId, int format = MaintenanceController.CurrentFormat) =>
    new(format, "diariz", "0.0.0", migrationId, DateTimeOffset.UtcNow, false);

[Fact] // same version -> restore, no forward-migrate
public async Task Restore_SameVersion_RestoresWithoutMigrating()
{
    var schema = Schema("m3", "m1", "m2", "m3");
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m3")));
    var ok = Assert.IsType<OkObjectResult>(await controller.Restore());
    Assert.True(backup.Restored);
    Assert.False(schema.Migrated);
}

[Fact] // older ancestor -> restore, THEN forward-migrate
public async Task Restore_OlderAncestor_RestoresThenMigratesForward()
{
    var schema = Schema("m3", "m1", "m2", "m3");
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m1")));
    Assert.IsType<OkObjectResult>(await controller.Restore());
    Assert.True(backup.Restored);
    Assert.True(schema.Migrated); // rolled m1 -> m3
}

[Fact] // newer than this build -> reject, nothing restored
public async Task Restore_NewerMigration_IsRejected()
{
    var schema = Schema("m2", "m1", "m2", "m3");
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m3")));
    Assert.IsType<BadRequestObjectResult>(await controller.Restore());
    Assert.False(backup.Restored);
    Assert.False(schema.Migrated);
}

[Fact] // unknown migration id -> reject
public async Task Restore_UnknownMigration_IsRejected()
{
    var schema = Schema("m3", "m1", "m2", "m3");
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m9-divergent")));
    Assert.IsType<BadRequestObjectResult>(await controller.Restore());
    Assert.False(backup.Restored);
}

[Fact] // older Format -> reject (predates a breaking change)
public async Task Restore_OlderFormat_IsRejected()
{
    var schema = Schema("m1", "m1"); // any
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m1", format: MaintenanceController.CurrentFormat - 1)));
    Assert.IsType<BadRequestObjectResult>(await controller.Restore());
    Assert.False(backup.Restored);
}

[Fact] // newer Format -> reject (from a newer app)
public async Task Restore_NewerFormat_IsRejected()
{
    var schema = Schema("m1", "m1");
    var (controller, _, backup) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m1", format: MaintenanceController.CurrentFormat + 1)));
    Assert.IsType<BadRequestObjectResult>(await controller.Restore());
    Assert.False(backup.Restored);
}

[Fact] // payload reports the roll-forward
public async Task Restore_OlderAncestor_ReportsMigratedFromTo()
{
    var schema = Schema("m3", "m1", "m2", "m3");
    var (controller, _, _) = BuildRestore(schema);
    SetBody(controller, MakeArchive(Manifest("m1")));
    var ok = Assert.IsType<OkObjectResult>(await controller.Restore());
    var json = JsonSerializer.Serialize(ok.Value);
    Assert.Contains("\"migratedFrom\":\"m1\"", json);
    Assert.Contains("\"migratedTo\":\"m3\"", json);
    Assert.Contains("\"restartRecommended\":true", json);
}
```

> Note: `FakeDatabaseBackup` must expose a `Restored` flag (set in `RestoreFromAsync`). If it doesn't yet, add `public bool Restored { get; private set; }` and set it - one line in `Fakes.cs`.

- [ ] **Step 2: Run to confirm they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests&FullyQualifiedName~Restore_"`
Expected: the new cases FAIL (old gate still exact-match; no migrate; no payload fields).

- [ ] **Step 3: Implement the helper + rewire `Restore`**

Add a private helper to `MaintenanceController`:

```csharp
// Decide whether this backup can be restored onto the running instance.
// Returns (accept, needsForwardMigrate, errorMessage).
private static (bool ok, bool migrate, string? error) EvaluateCompatibility(
    BackupManifest manifest, string current, IReadOnlyList<string> known)
{
    if (string.IsNullOrEmpty(current)) return (true, false, null); // in-memory/test: no migrations to compare

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
    return (true, true, null); // older ancestor -> restore, then migrate forward
}
```

Replace the gate at lines 116-120:

```csharp
var current = await _schema.CurrentAsync(ct);
var (ok, needMigrate, error) = EvaluateCompatibility(manifest, current, _schema.KnownMigrations);
if (!ok) return BadRequest(error);
```

After `await _backup.RestoreFromAsync(ds, ct);` (line 125), add:

```csharp
// The dump landed the backup's (older) schema + __EFMigrationsHistory; roll it up to this build.
if (needMigrate) await _schema.MigrateToCurrentAsync(ct);
```

Change the return (line 149) to:

```csharp
return Ok(new
{
    restored = true,
    migratedFrom = manifest.MigrationId,
    migratedTo = current,
    restartRecommended = needMigrate,
});
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests"`
Expected: PASS (all matrix cases + existing tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/MaintenanceController.cs tests/Diariz.Api.Tests/MaintenanceControllerTests.cs tests/Diariz.Api.TestSupport/Fakes.cs
git commit -m "feat: restore accepts older forward-compatible backups and migrates them forward"
```

---

## Task 4: Integration test - real EF forward-migrate

Prove `MigrateToCurrentAsync` on a real Postgres rolls a partially-migrated database up to latest with data intact. (The `pg_dump`/`pg_restore` round-trip is already covered by the existing `DumpThenRestore_RoundTripsData`; this covers the migrate-forward half against real migrations.)

**Files:**
- Modify: `tests/Diariz.Api.IntegrationTests/DatabaseBackupIntegrationTests.cs`

- [ ] **Step 1: Write the test**

```csharp
[Fact]
public async Task MigrateToCurrent_FromAnEarlierMigration_RollsForwardKeepingData()
{
    // Fresh probe database migrated only to the baseline.
    await using var probe = _fixture.CreateProbeContext(out var probeName); // provision a throwaway DB
    var migrator = probe.GetInfrastructure().GetService<IMigrator>()!;
    var all = probe.Database.GetMigrations().ToList();
    var baseline = all.First();
    Assert.True(all.Count > 1, "need at least two migrations to prove a forward roll");
    await migrator.MigrateAsync(baseline);

    // Seed a row that exists at the baseline schema (a user is safe - it's in InitialCreate).
    var userId = Guid.NewGuid();
    await probe.Database.ExecuteSqlRawAsync(
        "insert into \"AspNetUsers\" (\"Id\",\"UserName\",\"Email\",\"EmailConfirmed\",\"PhoneNumberConfirmed\"," +
        "\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\",\"Status\") " +
        $"values ('{userId}','probe@x.test','probe@x.test',true,false,false,false,0,1)");

    // Roll forward via the production primitive.
    await new EfSchemaVersion(probe).MigrateToCurrentAsync();

    Assert.Equal(all.Last(), (await probe.Database.GetAppliedMigrationsAsync()).Last());
    Assert.Equal(1, await probe.Database
        .SqlQueryRaw<int>($"select count(*)::int as \"Value\" from \"AspNetUsers\" where \"Id\" = '{userId}'")
        .SingleAsync());
}
```

> Adapt `CreateProbeContext`/`CreateProbeContext(out name)` to the fixture's existing probe-DB helper used by `DumpThenRestore_RoundTripsData`. If the column list for `AspNetUsers` at baseline differs, adjust the insert to the baseline's non-null columns (check `20260615111923_InitialCreate.cs`). Keep the existing PATH-based auto-skip pattern if the fixture needs the pg tools.

- [ ] **Step 2: Run**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "Name~MigrateToCurrent_FromAnEarlierMigration"`
Expected: PASS (needs Docker).

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/DatabaseBackupIntegrationTests.cs
git commit -m "test: forward-migrate a partially-migrated database keeps data (integration)"
```

---

## Task 5: Surface the restore result in the Maintenance UI

**Files:**
- Modify: `apps/web/src/components/MaintenancePanel.tsx` (+ `MaintenancePanel.test.tsx` if present)
- Modify: `apps/web/src/lib/api.ts` (restore return type) and `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`

- [ ] **Step 1: Type the restore response**

In `types.ts` add:

```ts
export interface RestoreResult {
  restored: boolean;
  migratedFrom: string;
  migratedTo: string;
  restartRecommended: boolean;
}
```

Update `api.restoreBackup` to `Promise<RestoreResult>` (return `data`).

- [ ] **Step 2: Add i18n keys (all four locales)**

`en/account.json`:
```json
"restoreDone": "Restore complete.",
"restoreMigrated": "Restored an older backup and upgraded its data ({{from}} -> {{to}}).",
"restoreRestartHint": "Restart the app to finish applying the upgrade.",
```
Add the matching translations to `de`, `es`, `fr` (keep the `{{from}}`/`{{to}}` placeholders; use a plain hyphen, no en/em dashes). Locale parity is enforced by `locales.test.ts`.

- [ ] **Step 3: Write the failing UI test** (if `MaintenancePanel.test.tsx` exists; otherwise add one)

```tsx
it("reports a forward-migrated restore and the restart hint", async () => {
  (api.restoreBackup as Mock).mockResolvedValue({
    restored: true, migratedFrom: "m1", migratedTo: "m3", restartRecommended: true,
  });
  // ...arrange: choose a file, tick the confirm, click Restore...
  expect(await screen.findByText(/upgraded its data/i)).toBeTruthy();
  expect(screen.getByText(/restart the app/i)).toBeTruthy();
});
```

- [ ] **Step 4: Implement** - in the restore success handler, store the `RestoreResult` and render: `restoreDone`; when `migratedFrom !== migratedTo`, the `restoreMigrated` line; when `restartRecommended`, the `restoreRestartHint` line.

- [ ] **Step 5: Run**

Run: `cd apps/web && npx vitest run src/components/MaintenancePanel.test.tsx && npm run build`
Expected: PASS + build.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src
git commit -m "feat(web): show the restore roll-forward result + restart hint"
```

---

## Task 6: Docs, version bump, release notes

**Files:** `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`, `CLAUDE.md`.

- [ ] **Step 1: Bump the version** - functional enhancement, so Minor +1, Build 0: `0.120.x` -> **`0.121.0`** in all four mirrors (keep them in lockstep).

- [ ] **Step 2: Add the release entry** to the top of `RELEASES` in `releases.ts` (`version:"0.121.0"`, the PR number, headline, prose `summary`, `added`/`changed`). `RELEASES[0].version` must equal `version.json` (asserted by `releases.test.ts`). No en/em dashes.

- [ ] **Step 3: Update the reference docs** - in `Overall_Synopsis_of_Platform.md` note the backup manifest carries a `Format` epoch and restore now accepts forward-compatible older backups (migrating them forward, `Format` gating breaking changes); mention the `Format` field in the backup/restore section of `Data_Schema.md` (manifest, not a table). Add one line to the **CLAUDE.md** versioning section: *a migration that is not forward-restore-safe must bump `MaintenanceController.CurrentFormat` in the same PR.*

- [ ] **Step 4: Run the version/locale guards**

Run: `cd apps/web && npx vitest run src/lib/releases.test.ts && npx vitest run`
Expected: PASS (releases + locale parity).

- [ ] **Step 5: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts docs CLAUDE.md
git commit -m "docs: version 0.121.0 + release notes for cross-version restore"
```

---

## Final verification (before opening the PR)

- [ ] `dotnet build Diariz.slnx` - clean.
- [ ] `dotnet test tests/Diariz.Api.Tests` - green.
- [ ] `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~DatabaseBackup"` - green (Docker).
- [ ] `cd apps/web && npm run build && npx vitest run` - green.
- [ ] Open the PR from a feature branch. **Deployment surface: server redeploy only** (no desktop shell change). Call out that the restore compatibility gate changed and that `Format` is now the breaking-change fence.

## Notes / gotchas for the implementer

- `db.Database.GetMigrations()` / `MigrateAsync()` are Npgsql-only; `EfSchemaVersion.KnownMigrations` is evaluated lazily and only reached under the real provider (unit tests use `FakeSchemaVersion`), so the in-memory provider never calls them. Keep the `string.IsNullOrEmpty(current)` short-circuit so the in-memory path skips the gate.
- Restore already runs `pg_restore --clean` under the live app and tolerates it; the added `MigrateAsync` runs in the same request. Do **not** add a maintenance-mode gate (decision 1) - just return `restartRecommended`.
- Do not touch the keyring exclusion (decision 3). Keep the restore UI's existing "encrypted API keys are not included" note.
- The `Format` fence is the epoch boundary (decision 2): you do not need to track which migration belongs to which Format - a same-`Format` backup is in-epoch by construction, and the ancestor check handles ordering within it.
