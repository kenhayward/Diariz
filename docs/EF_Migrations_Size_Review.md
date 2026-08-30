# EF Core Migrations - Size, Cost, and When to Squash

A record of what `src/Diariz.Domain/Migrations` actually weighs, what that costs, and the one
condition under which squashing it becomes worth doing. Written so the question does not have to be
re-derived from scratch next time someone notices the folder is enormous.

Measured 30 August 2026 at version 0.262.0, commit `a769a28e`. See
[Re-measuring](#re-measuring) at the end - every figure here is reproducible in a few minutes.

---

## Summary

The folder is **225,137 lines**, and **95.6% of it is machine-written**. It costs about **1.5 seconds
of an 11.6 second clean solution build** and **nothing at runtime**. The only cost that genuinely bit
was **review** - a one-column migration opened as a 3,600-line diff - and that was fixed in PR #681 by
marking the generated files `linguist-generated` in `.gitattributes`.

**Do not squash on its own.** It would make every existing backup unrestorable. There is a specific
future PR in which squashing costs nothing; wait for it.

---

## 1. What is actually in the folder

EF Core writes a complete copy of the model into a `*.Designer.cs` beside every migration. That file,
not the schema changes, is what dominates.

| Content | Lines | Share |
|---|---:|---:|
| Model snapshots (`*.Designer.cs`) | 215,222 | 95.6% |
| Actual `Up()` / `Down()` logic | 6,318 | 2.8% |
| `DiarizDbContextModelSnapshot.cs` | 3,597 | 1.6% |
| **Total** | **225,137** | 8.6 MB on disk |

98 migrations, `InitialCreate` (15 Jun 2026) through `AddActionPinned` (28 Aug 2026). Sixteen of them
do real data work via `migrationBuilder.Sql`.

**Two things that look wrong and are not:**

- The folder holds **107 non-Designer `.cs` files but only 98 migrations**. The extra eight are
  hand-written backfill helpers (`PersonalRoomBackfill.cs`, `RoleToGroupBackfill.cs` and friends).
  They correctly carry no `[Migration]` attribute, so EF never discovers them as migrations.
- **The database agrees exactly:** `__EFMigrationsHistory` holds 98 rows, first `InitialCreate`, last
  `AddActionPinned`. Nothing has drifted.

## 2. Growth is superlinear

Each snapshot is a copy of the *current* model, so its size grows as the model grows. Total folder
size is therefore roughly `migrations x model size` - it bends upward rather than climbing steadily.

| Month | Migrations | Lines added | Per migration | Cumulative |
|---|---:|---:|---:|---:|
| Jun 2026 | 22 | 17,752 | 807 | 17,752 |
| Jul 2026 | 44 | 89,735 | 2,039 | 107,487 |
| Aug 2026 | 32 | 107,735 | 3,367 | 215,222 |

August added **more lines than July from fewer migrations**. A single migration cost 513 lines at
`InitialCreate` and costs about 3,600 today.

At the observed cadence of ~40 migrations/month, six more months adds roughly **1.8 million lines**.
That figure is an extrapolation, not a measurement - it is the reason to keep a plan, not a reason to
act this week.

## 3. What it costs today

Every figure below came from running the thing. The build comparison was made by temporarily adding
`<Compile Remove="Migrations/**" />` to `Diariz.Domain.csproj` and rebuilding.

| Where | Cost | Verdict |
|---|---|---|
| Domain assembly | 2,988 KB with migrations, **175 KB** without | 94% of the DLL |
| Clean build, Domain | 2.0-3.4 s with, ~1.0 s without | ~1.5 s |
| Clean build, whole solution | 11.6 s total | Migrations are ~10% |
| Deployed image | 2.85 MB inside a 488 MB `/app` | Noise |
| Runtime | `No migrations were applied` - one history query, reflection over 98 types | Not measurable |
| CodeQL | ~5 min end to end, four languages in parallel | Not dominated by this |

The cost that actually bites is none of these: it is **review**. That is what PR #681 addressed.

## 4. Why we are not squashing

Squashing is the obvious fix and it is blocked by something concrete. This is the part that is not
visible from the Migrations folder itself, which is why it is written down here.

`MaintenanceController.EvaluateCompatibility` (`src/Diariz.Api/Controllers/MaintenanceController.cs:206`)
decides whether a platform backup can be restored by looking its `MigrationId` up in the
**compile-time** migration list:

```csharp
// MaintenanceController.EvaluateCompatibility
int idxBackup = list.IndexOf(manifest.MigrationId);
if (idxBackup < 0)
    return (false, false, $"This backup's schema version ... is not recognised by this build.");
```

Squash and every historical id disappears from that list, so **every backup taken so far fails to
restore**. Per `CLAUDE.md` that forces a `MaintenanceController.CurrentFormat` bump, which then
rejects all pre-squash backups deliberately and permanently.

There is no clever way around it. Even keeping a static list of retired ids purely for ordering would
not help: a squashed assembly has no migrations left that can roll a two-month-old schema forward to
current, so the restore genuinely cannot complete.

So the trade on offer today is: give up the ability to restore any backup from the platform's first
two and a half months, in exchange for ~1.5 s of build time and 2.85 MB of a 488 MB image. Clear no.

### The trigger that changes the answer

The expensive half of a squash is the `CurrentFormat` bump, not the squash. So **squash inside
whatever future PR is bumping `CurrentFormat` anyway** - a destructive column drop or rename, a
pgvector dimension change, any semantic reshape an older dump cannot survive. At that point the
backups are already being invalidated for another reason and the marginal cost of squashing is zero.

That PR should also delete the corresponding entry from the deferred-work list.

## 5. Considered and rejected

- **A compiled model** (`dotnet ef dbcontext optimize`). It targets model *building* at startup, not
  migration bloat, and would add a second generated corpus to keep in sync for perhaps 200 ms once per
  process. Not worth it.
- **Dropping the Designer files from compilation.** They carry the `[Migration]` attribute, and EF
  needs each migration's target model to generate SQL when applying it. They are not optional - the
  `<Compile Remove>` experiment above is a measurement technique, not a shippable change.
- **`-diff` in `.gitattributes`** alongside `linguist-generated`. It would also suppress the local
  `git diff` you want on the rare occasion a snapshot change is the thing under review.

## 6. Re-measuring

Run these from the repository root when you want to know whether the picture has changed.

**Size the folder and the split:**

```bash
cd src/Diariz.Domain/Migrations && ls *.Designer.cs | wc -l && cat *.cs | wc -l && cat *.Designer.cs | wc -l
```

**Weigh the assembly.** Add `<Compile Remove="Migrations/**" />` to `Diariz.Domain.csproj`, rebuild,
compare the DLL size and build time, then revert the csproj. The build succeeds without them - nothing
in `DiarizDbContext` references a migration.

**Check the database agrees.** The count should match the file count; a mismatch means drift, which is
a different and more urgent problem than size.

```bash
docker exec diariz-postgres-1 psql -U diariz -d diariz -c 'select count(*) from "__EFMigrationsHistory";'
```

**Confirm the runtime cost is still nil.** Look for `No migrations were applied`. If migrations *are*
being applied on every boot, something is wrong with the history table, not with the folder.

```bash
docker logs diariz-api-1 2>&1 | grep -i migrat
```

---

Build timings are Debug configuration on the development machine and will differ on CI. The line
counts, assembly sizes and database figures are exact.
