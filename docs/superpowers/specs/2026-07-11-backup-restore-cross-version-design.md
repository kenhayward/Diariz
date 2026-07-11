# Cross-Version Backup Restore - Design

**Status:** Draft (design only; not yet planned or implemented)
**Date:** 2026-07-11
**Author:** Ken Hayward (with Claude)
**Scope:** API only (`src/Diariz.Api`, `src/Diariz.Domain`). No web/desktop/worker changes.

## Goal

Allow the Platform Administrator to restore a backup that was taken on an **older but
forward-compatible** schema version, not only on an exactly-matching version. The restore should
bring the older data up to the running code's schema automatically, and must continue to hard-reject
backups it cannot safely apply (newer/divergent schemas, or releases explicitly flagged as breaking).

## Background: how backup/restore works today

Platform-Administrator only, in [`MaintenanceController`](../../../src/Diariz.Api/Controllers/MaintenanceController.cs)
(`[Authorize(Policy = "ManagePlatform")]`, route `api/maintenance`).

**Backup** (`GET api/maintenance/backup`, lines 38-92) writes a single `.zip`:

| Entry | Content |
|---|---|
| `manifest.json` | `BackupManifest(int Format, string App, string Version, string MigrationId, DateTimeOffset CreatedAt, bool IncludesKeyring)` (line 172-173) |
| `database.dump` | Native Postgres **custom-format `pg_dump`** (`--format=custom --no-owner --no-privileges`), captures the full schema + data + `__EFMigrationsHistory` |
| `objects/<key>` | One entry per object-store blob (audio, PDF attachments), stored uncompressed |

`MigrationId` is stamped from [`EfSchemaVersion.CurrentAsync`](../../../src/Diariz.Api/Services/DatabaseBackup.cs)
= `db.Database.GetAppliedMigrationsAsync().LastOrDefault()`. The DataProtection keyring is deliberately
excluded (`IncludesKeyring: false`), so encrypted per-user LLM keys do not survive a cross-instance restore.

**Restore** (`POST api/maintenance/restore`, lines 96-152):
1. Spill the request body to a temp zip; read `manifest.json` (400 if missing/unreadable).
2. **Compatibility gate** (lines 116-120): reject unless `manifest.MigrationId` **exactly equals** the
   running instance's last applied migration.
3. `pg_restore --clean --if-exists --no-owner --no-privileges` - **drops and recreates** every object
   from the dump, including `__EFMigrationsHistory`. A non-zero exit is treated as non-fatal by design.
4. Wipe the object store, then re-upload every `objects/<key>` entry.

**Migrations** run only at process startup via `Database.MigrateAsync()`
([`Program.cs:448`](../../../src/Diariz.Api/Program.cs)). Restore itself never migrates. Latest migration
at time of writing: `20260710185237_AddRecordingPlacementPreference`.

### Why exactly-equal is the blocker

The gate is `manifest.MigrationId != current` (string inequality). It refuses any non-identical schema -
older *or* newer - even when the older one is trivially migrate-able forward. `Format` (currently `1`) and
`Version` are written into the manifest but **never checked**; only `MigrationId` gates today.

## Key insight

EF migrations already are the compatibility contract. Because `pg_restore --clean` replaces
`__EFMigrationsHistory` with the backup's (older) history, calling `MigrateAsync()` immediately after the
restore applies exactly the migrations between the backup's version and the current code - each migration's
`Up` runs against the restored data - leaving the database at the current schema. This is the same machinery
that already runs at startup, so it is well understood and low-risk.

This reframes "compatible previous version" precisely:

> A backup is restorable iff its `MigrationId` is an **ancestor of (or equal to)** the current build's
> migration set - i.e. same-or-older and forward-migratable - **and** its `Format` matches the current
> `Format`. Newer or unknown migrations are rejected (no down-migrations are maintained).

## Design

Three changes: a richer compatibility check, a post-restore forward-migration step, and promoting `Format`
to a real breaking-change fence.

### 1. Compatibility policy (replace the equality gate)

Extend `ISchemaVersion` so the controller can reason about ordering, not just the current id:

```csharp
public interface ISchemaVersion
{
    Task<string> CurrentAsync(CancellationToken ct = default);          // unchanged
    // All migrations known to THIS build, in dependency order (db.Database.GetMigrations()).
    IReadOnlyList<string> KnownMigrations { get; }
    // Migrations applied to the live database right now (db.Database.GetAppliedMigrationsAsync()).
    Task<IReadOnlyList<string>> AppliedAsync(CancellationToken ct = default);
}
```

Restore decision, given `manifest.MigrationId = m` and current applied last = `c`:

| Condition | Result |
|---|---|
| `m == c` | **Accept** - identical schema (today's behaviour). No forward-migration needed. |
| `m` is in `KnownMigrations` at an index **<** index(`c`) | **Accept as older** - restore, then `MigrateAsync()` forward. |
| `m` is in `KnownMigrations` at an index **>** index(`c`) | **Reject** - backup is newer than this build; upgrade the app first. |
| `m` not in `KnownMigrations` (unknown id) | **Reject** - divergent/unrecognised build. |
| `current` is empty (in-memory provider / no migrations) | Skip the gate (unit-test path, unchanged). |

Rejections return `400 BadRequest` with a message naming both versions and the reason (older-ok vs
newer-blocked vs unknown), so the admin knows whether to upgrade the app or the backup is unusable.

### 2. Format fence (promote the unused field)

`Format` becomes the explicit, human-controlled kill switch for changes that **cannot** be forward-migrated
without data loss (e.g. a destructive column drop/rename, a pgvector dimension change, a semantic data
reshaping). Rule:

- Bump `Format` (2, 3, ...) in the same PR as any migration that breaks forward-restore compatibility.
- Restore hard-rejects `manifest.Format != CurrentFormat` **before** the migration-ancestor check, with a
  message saying the backup predates a breaking change and cannot be restored onto this version.
- Within a single `Format`, the ancestor check governs.

`CurrentFormat` is a single constant in the API. This gives a coarse "epoch" fence over the fine-grained
migration-name check, so we are never relying on migration-name math alone to catch a genuinely breaking
release.

### 3. Restore-then-migrate-forward

After `pg_restore` succeeds and before returning, when the backup was accepted as *older* (`m != c`), run
`db.Database.MigrateAsync(ct)` to roll the schema up to the current build. Sequence in `Restore(...)`:

1. Read manifest -> `Format` fence -> ancestor check (reject/accept/needs-forward).
2. `pg_restore` the dump (drops + reloads old schema + old `__EFMigrationsHistory`).
3. If accepted-as-older: `await db.Database.MigrateAsync(ct)` (applies migrations `m+1 .. c`).
4. Wipe + re-upload the object store (unchanged).
5. Return `{ restored = true, migratedFrom = m, migratedTo = c }` so the UI can report the roll-forward.

Because migrations run inside the restore request (which is already rebuilding the whole database), the
existing "the app tolerates its DB being replaced under it" assumption still holds. We surface a
**"restart recommended"** hint in the response/UI so pooled connections, the `SummarizationWorker`, and any
cached state rebuild cleanly. (A full maintenance-mode gate is out of scope for v1 - see Open Questions.)

## Rejection / acceptance matrix (summary)

| Backup vs running instance | Outcome |
|---|---|
| Same `Format`, same `MigrationId` | Restore, no migration |
| Same `Format`, older ancestor `MigrationId` | Restore, then migrate forward |
| Same `Format`, newer `MigrationId` | Reject: "upgrade the app to at least <version> first" |
| Same `Format`, unknown `MigrationId` | Reject: "unrecognised schema version" |
| Older `Format` | Reject: "backup predates a breaking change; cannot restore onto this version" |
| Newer `Format` | Reject: "backup is from a newer app; upgrade first" |

## Data-safety considerations (decide before building)

- **Forward migrations must be safe on populated tables.** Restore-then-migrate is only as safe as each
  `Up` migration when run against real data (non-null column adds need defaults/backfills; data-shape
  migrations must be idempotent). This becomes a release-time discipline - suggest a line in the
  versioning rules: *"a migration that is not forward-restore-safe must bump `Format`."*
- **The seeder runs every boot and after any restart.** Per the existing note that one-way data moves
  belong in migrations (the seeder re-applies itself), confirm the post-restore/seed path does not clobber
  restored rows. Existing concern, more visible here.
- **DataProtection keyring stays excluded.** Encrypted per-user API keys are lost on any cross-instance
  restore regardless of version. Independent of this work; the restore UI should keep stating it. (A
  future option: include the keyring behind an explicit "same-instance restore" opt-in - out of scope.)
- **Newer backups are unrecoverable by design.** We do not maintain EF `Down` migrations, so a backup from
  a schema ahead of the running code cannot be applied. The ancestor check must keep rejecting these.

## Testing plan

- **Integration** (`DatabaseBackupIntegrationTests`, real `pg_dump`/`pg_restore`): dump a database at
  migration `N-k` (apply migrations up to an earlier point, seed a row), restore onto a database migrated
  to current, run the forward-migration, and assert (a) the old row survives, (b) columns added by the
  intervening migrations exist with correct defaults, (c) `__EFMigrationsHistory` ends at current. Keep the
  existing PATH-based auto-skip.
- **Controller** (`MaintenanceControllerTests`, fakes): drive `FakeSchemaVersion` with a known migration
  list and assert the matrix - accept-equal, accept-older (asserts forward-migrate was invoked via a fake),
  reject-newer, reject-unknown, reject-older-Format, reject-newer-Format. Extend `FakeSchemaVersion` with
  `KnownMigrations`/`AppliedAsync` and a fake that records whether `MigrateAsync` ran.
- **Manifest**: assert `Format` is written and that a bumped `CurrentFormat` rejects an older-Format archive.

## Rollout, versioning, docs

- **Backward compatible for consumers.** Existing same-version backups keep restoring unchanged. Old
  backups made before this change already carry `Format:1` and a `MigrationId`, so they slot into the new
  policy with no migration of the backup format itself.
- **Deployment surface:** server redeploy only (API). No desktop release, no web change beyond optionally
  showing the "migrated forward / restart recommended" result text.
- **Version bump:** functional enhancement -> Minor +1, plus a release-notes entry and a
  `Data_Schema.md` / `Overall_Synopsis_of_Platform.md` note that restore now accepts forward-compatible
  older backups and that `Format` gates breaking changes.
- **Process note to add to CLAUDE.md versioning section:** any migration that is not forward-restore-safe
  must bump the backup `Format` constant in the same PR.

## Open questions / decisions for the user

1. **Maintenance window during restore.** v1 runs the forward-migration inline and *recommends* a restart.
   Do we want a stronger guarantee - e.g. put the API into a read-only/maintenance state for the duration,
   or automatically signal a restart - or is the "recommended restart" hint enough for a single-admin
   deployment?
2. **How far back to support.** Do we support forward-restore across the *entire* migration history, or cap
   it (e.g. only within the current `Format` epoch, which the fence already enforces)? The `Format` fence
   gives a natural, curated boundary; unbounded history means every old migration must stay forward-safe
   forever.
3. **Keyring for same-instance restores.** Out of scope here, but worth confirming we still want it excluded
   even when restoring onto the same instance (where the keyring would decrypt correctly).
4. **Result reporting.** Is returning `{ restored, migratedFrom, migratedTo, restartRecommended }` enough,
   or do we want the migration list surfaced in the UI?

## Non-goals

- Changing the backup archive format (still `pg_dump` custom + objects + manifest).
- Logical/EF-serialized (JSON-per-entity) export. Considered and rejected: large rewrite, must re-solve FK
  ordering and pgvector fidelity, and still breaks on renames/type changes. The native-dump path plus
  forward-migration covers the real need.
- Restoring newer backups onto older code (requires down-migrations we do not maintain).
- Including the DataProtection keyring.
