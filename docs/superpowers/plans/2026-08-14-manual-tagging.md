# Manual Tagging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tag exists because the user chose it - the LLM's extracted topics become suggestions offered on the recording hub, pickable or dismissable, and only adopted tags reach the tag cloud, drill-down and search.

**Architecture:** One table keeps holding every tag; `RecordingTag` gains a `Status` (`Suggested`/`Adopted`/`Dismissed`) whose migration default *is* the demotion of all existing tags. The extraction pass only ever writes `Suggested` rows and only ever replaces `Suggested` rows, so hand-applied tags survive a re-transcription. Three new per-recording endpoints adopt, remove and dismiss; a new Tags pill + popover on the hero summary card is the only UI.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; React 19 + TypeScript + Vite + Tailwind v4; xUnit (+ Testcontainers) and vitest + @testing-library/react.

**Spec:** `docs/superpowers/specs/2026-08-14-manual-tagging-design.md`
**Design handoff:** `docs/design_handoff_manual_tagging/README.md` (+ `screenshots/`) - high-fidelity, both themes, final colours and sizes.

## Global Constraints

- **TDD is mandatory.** Failing test first, watch it fail, minimal code to pass. No production code without a preceding failing test.
- **Test output stays pristine** - a passing run has no errors or warnings.
- **No em or en dashes in user-facing text** (UI strings, i18n catalogues, release notes). Plain hyphen `-` only. The handoff's `No tags yet — click to add` ships as `No tags yet - click to add`.
- **Enum ints are append-only.** `RecordingTagStatus` values are `Suggested = 0`, `Adopted = 1`, `Dismissed = 2`; never renumber.
- **Postgres-only model config goes behind `Database.IsNpgsql()`** so the in-memory test provider can still build the model.
- **Do not change `RecordingsController`'s constructor.** It already injects `DiarizDbContext _db` and `IRoomScope _rooms`, which is all this feature needs. A ctor change has a second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`.
- **Never `git add -A`** in this repo - it sweeps hundreds of untracked scratch files into the commit. Stage explicit paths only.
- **Branch:** work on `feat/manual-tagging` (already created, spec already committed there). Finish by pushing and opening a PR; never commit to `main`.
- **Version:** this PR ships `0.212.0` (functional enhancement: minor +1, build reset). Current is `0.211.3`.

---

### Task 1: Tag status on the domain model

**Files:**
- Create: `src/Diariz.Domain/Entities/RecordingTagStatus.cs`
- Modify: `src/Diariz.Domain/Entities/RecordingTag.cs` (whole file - add fields, rewrite the now-false doc comments)
- Modify: `src/Diariz.Domain/DiarizDbContext.cs:261-267` (the `RecordingTag` config block)
- Create: migration `src/Diariz.Domain/Migrations/<timestamp>_AddRecordingTagStatus.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/RecordingTagStatusIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `RecordingTagStatus { Suggested = 0, Adopted = 1, Dismissed = 2 }`; `RecordingTag.Status` (`RecordingTagStatus`, defaults to `Suggested`); `RecordingTag.AdoptedAt` (`DateTimeOffset?`).

The tests are integration tests because both behaviours (the column default, the case-insensitive unique index) are real-database behaviours the in-memory provider cannot show.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.IntegrationTests/RecordingTagStatusIntegrationTests.cs`:

```csharp
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class RecordingTagStatusIntegrationTests
{
    private readonly ContainersFixture _fx;
    public RecordingTagStatusIntegrationTests(ContainersFixture fx) => _fx = fx;

    private async Task<Recording> SeedRecordingAsync()
    {
        await using var db = _fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Email = $"{userId}@example.test", PasswordHash = "x" });
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task ALegacyRowWithNoStatusColumnValue_LandsAsSuggested_WhichIsTheDemotion()
    {
        var rec = await SeedRecordingAsync();

        // Insert the way a pre-migration row existed: without naming Status at all. The demotion of every
        // existing tag rests on the COLUMN DEFAULT, and only a raw insert proves it - going through EF would
        // send Status = 0 from the C# property initialiser and pass even if the migration forgot the default.
        await using (var db = _fx.CreateDbContext())
        {
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RecordingTags" ("Id", "RecordingId", "Tag", "Weight", "Ordinal", "CreatedAt")
                VALUES ({id}, {rec.Id}, 'legacy-tag', 0.7, 0, now())
                """);
        }

        await using (var db = _fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Suggested, saved.Status);
            Assert.Null(saved.AdoptedAt);
        }
    }

    [Fact]
    public async Task NewTag_FromTheApp_DefaultsToSuggested_WithNoAdoptedAt()
    {
        var rec = await SeedRecordingAsync();

        await using (var db = _fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Roadmap", Weight = 0.8, Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Suggested, saved.Status);
            Assert.Null(saved.AdoptedAt);
        }
    }

    [Fact]
    public async Task AdoptedAt_RoundTripsAsUtc()
    {
        var rec = await SeedRecordingAsync();
        var when = DateTimeOffset.UtcNow;

        await using (var db = _fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Adopted", Weight = 1.0, Ordinal = 0,
                Status = RecordingTagStatus.Adopted, AdoptedAt = when,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Adopted, saved.Status);
            Assert.Equal(when.ToUniversalTime(), saved.AdoptedAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task CaseVariantDuplicate_OnTheSameRecording_IsRejected()
    {
        var rec = await SeedRecordingAsync();

        await using (var db = _fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "metadata", Weight = 1.0, Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Metadata", Weight = 1.0, Ordinal = 1,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SameTag_OnDifferentRecordings_IsAllowed()
    {
        var a = await SeedRecordingAsync();
        var b = await SeedRecordingAsync();

        await using var db = _fx.CreateDbContext();
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = a.Id, Tag = "shared-word", Weight = 1.0, Ordinal = 0,
        });
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "shared-word", Weight = 1.0, Ordinal = 0,
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RecordingTags.CountAsync(t => t.Tag == "shared-word"));
    }
}
```

Before writing it, open `tests/Diariz.Api.IntegrationTests/TagsIntegrationTests.cs` and copy its fixture-injection and user-seeding idiom exactly - if `ContainersFixture` is injected or users are seeded differently there, follow that file, not this sketch.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RecordingTagStatusIntegrationTests"`
Expected: FAIL to compile - `RecordingTagStatus` and `RecordingTag.Status` do not exist.

(Docker must be running. `--filter "Name=X"` does not work in this repo; always use `FullyQualifiedName~X`.)

Once it compiles, `ALegacyRowWithNoStatusColumnValue_LandsAsSuggested_WhichIsTheDemotion` is the one to
watch: it must fail before the migration adds the column default and pass after. If it passes with the
default removed from the migration, the test is not testing what it claims.

- [ ] **Step 3: Add the enum**

Create `src/Diariz.Domain/Entities/RecordingTagStatus.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Where a <see cref="RecordingTag"/> stands with the user. Tags are extracted automatically but
/// only ever <em>offered</em>: the tag cloud, drill-down and search cover <see cref="Adopted"/> tags only.
/// Stored as ints in Postgres - APPEND ONLY, never renumber.</summary>
public enum RecordingTagStatus
{
    /// <summary>The LLM proposed it; nobody has acted on it. Replaced wholesale by the next extraction and
    /// invisible to every aggregate.</summary>
    Suggested = 0,
    /// <summary>The user picked it (typed it, or promoted a suggestion). Survives a re-transcription and is
    /// the only status the tag cloud counts.</summary>
    Adopted = 1,
    /// <summary>The user rejected it for this recording. Kept as a tombstone so a re-extraction cannot
    /// suggest it again here.</summary>
    Dismissed = 2,
}
```

- [ ] **Step 4: Add the fields and fix the doc comments**

Replace `src/Diariz.Domain/Entities/RecordingTag.cs` in full:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A topic tag on a recording. The LLM extracts candidates from the transcript, but a tag only
/// counts once the user has adopted it: <see cref="Status"/> separates a machine suggestion from the user's
/// own tag, and only <see cref="RecordingTagStatus.Adopted"/> rows reach the cross-transcript tag cloud.
/// A (re-)transcription replaces the <see cref="RecordingTagStatus.Suggested"/> rows only, so hand-applied
/// tags and dismissals survive it.</summary>
public class RecordingTag
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The tag text, stored as written. A machine suggestion arrives in the extraction prompt's
    /// Title Case ("Data Collection"); a hand-typed tag is kept verbatim with internal whitespace collapsed
    /// to hyphens ("data-collection"). Never contains a space. Unique per recording, case-insensitively.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>For a suggestion, the model's relative salience within this recording (0-1, clamped on
    /// ingest) - it orders the hint list. For an adopted tag, always 1.0, so the cloud's summed weight
    /// equals the number of recordings carrying the tag and sizes words by how often the user used them.</summary>
    public double Weight { get; set; }

    /// <summary>Sort order within the recording (0-based, the LLM's weight-descending order).</summary>
    public int Ordinal { get; set; }

    /// <summary>Whether this is a machine suggestion, the user's own tag, or a dismissal tombstone.</summary>
    public RecordingTagStatus Status { get; set; } = RecordingTagStatus.Suggested;

    /// <summary>When the user adopted it; null for suggestions and dismissals. Orders the chips in the hub's
    /// tag popover - <see cref="CreatedAt"/> cannot, because a promoted suggestion was created whenever the
    /// extraction happened to run.</summary>
    public DateTimeOffset? AdoptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Configure the unique index**

In `src/Diariz.Domain/DiarizDbContext.cs`, replace the `RecordingTag` block at lines 261-267:

```csharp
        // Tag-cloud tags: LLM suggestions plus the tags the user adopted (see RecordingTagStatus). Plain
        // columns only (no vector/jsonb), so the entity itself stays outside the Npgsql guard and loads
        // under the in-memory test provider.
        builder.Entity<RecordingTag>(e =>
        {
            e.HasIndex(t => new { t.RecordingId, t.Ordinal });
            e.Property(t => t.Tag).HasMaxLength(64);
        });

        // One row per tag per recording, case-insensitively: promotion flips an existing suggestion rather
        // than inserting, so a duplicate here means a race between two room members. A functional index on
        // lower(Tag) is Postgres-only, hence the guard - the in-memory provider cannot enforce it, which is
        // why RecordingTagStatusIntegrationTests covers it instead.
        if (Database.IsNpgsql())
        {
            builder.Entity<RecordingTag>()
                .HasIndex(t => new { t.RecordingId, t.Tag })
                .HasDatabaseName("IX_RecordingTags_RecordingId_TagLower")
                .IsUnique();
        }
```

Note: EF cannot express `lower(Tag)` in `HasIndex`, so the declared index above is a placeholder that the
migration replaces with the real functional index by hand in the next step. Keep the declaration so the
model snapshot and the database agree.

- [ ] **Step 6: Generate the migration**

Run:

```bash
dotnet ef migrations add AddRecordingTagStatus --project src/Diariz.Domain --startup-project src/Diariz.Api
```

- [ ] **Step 7: Hand-edit the migration**

Open the generated file. Keep the two `AddColumn` calls, but make the `Status` default explicit and replace
the generated `CreateIndex` with raw SQL that de-duplicates first and then builds the functional index:

```csharp
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Status 0 = Suggested. The default IS the demotion: every tag that exists today was applied by the
        // LLM without the user choosing it, so it becomes a suggestion. Same for an older backup restored
        // later, which is why this migration needs no data script and no CurrentFormat bump.
        migrationBuilder.AddColumn<int>(
            name: "Status", table: "RecordingTags", type: "integer", nullable: false, defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AdoptedAt", table: "RecordingTags", type: "timestamp with time zone", nullable: true);

        // The unique index below would fail on any legacy case-variant pair. The parse step already dedupes
        // case-insensitively and replace is wholesale, so this should delete nothing - but "should" is not a
        // deployment strategy. Keep the lowest Ordinal of each group.
        migrationBuilder.Sql("""
            DELETE FROM "RecordingTags" t
            USING "RecordingTags" other
            WHERE t."RecordingId" = other."RecordingId"
              AND lower(t."Tag") = lower(other."Tag")
              AND (t."Ordinal" > other."Ordinal"
                   OR (t."Ordinal" = other."Ordinal" AND t."Id" > other."Id"));
            """);

        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX "IX_RecordingTags_RecordingId_TagLower"
            ON "RecordingTags" ("RecordingId", lower("Tag"));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_RecordingTags_RecordingId_TagLower";""");
        migrationBuilder.DropColumn(name: "AdoptedAt", table: "RecordingTags");
        migrationBuilder.DropColumn(name: "Status", table: "RecordingTags");
    }
```

Delete the generated `CreateIndex`/`DropIndex` calls for that index name so it is not created twice.

C# raw string literals carry the source file's line endings, so never byte-compare this SQL in a test -
`docs` notes this bit Windows-vs-Linux CI before.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RecordingTagStatusIntegrationTests"`
Expected: PASS (5 tests). The migration runs automatically when `ContainersFixture` applies migrations.

- [ ] **Step 9: Confirm the rest of the suite still builds and passes**

Run: `dotnet build Diariz.slnx` then `dotnet test tests/Diariz.Api.Tests`
Expected: build succeeds, unit tests pass. Building the whole `.slnx` matters - a unit-only run misses integration and CodeQL compile breaks.

- [ ] **Step 10: Commit**

```bash
git add src/Diariz.Domain/Entities/RecordingTagStatus.cs src/Diariz.Domain/Entities/RecordingTag.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/RecordingTagStatusIntegrationTests.cs
git commit -m "feat(tags): add tag status so a tag can be suggested, adopted or dismissed"
```

---

### Task 2: Extraction writes suggestions and spares the user's tags

**Files:**
- Modify: `src/Diariz.Api/Services/TagsProcessor.cs:12-20` (class doc comment), `:68-78` (the replace block)
- Test: `tests/Diariz.Api.Tests/TagsProcessorTests.cs`

**Interfaces:**
- Consumes: `RecordingTagStatus`, `RecordingTag.Status`, `RecordingTag.AdoptedAt` from Task 1.
- Produces: no new API. Behaviour: `TagsProcessor.ProcessAsync` inserts `Suggested` rows only, deletes only `Suggested` rows, and skips any extracted tag that already exists on the recording as `Adopted` or `Dismissed` (case-insensitively).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/TagsProcessorTests.cs`. Read the top of that file first and reuse its existing helpers (it already builds a fake client, resolver, hub and `TestDb`); the four tests below follow whatever `Process...` helper the file already uses to invoke `ProcessAsync`.

```csharp
    [Fact]
    public async Task Extraction_InsertsTagsAsSuggested_WithNoAdoptedAt()
    {
        using var db = TestDb.Create();
        var (rec, tr) = SeedTranscribedRecording(db);
        await db.SaveChangesAsync();

        await RunAsync(db, client: FakeTags(("Roadmap", 0.9)), rec, tr);

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Suggested, tag.Status);
        Assert.Null(tag.AdoptedAt);
    }

    [Fact]
    public async Task Extraction_ReplacesSuggestions_ButKeepsAdoptedAndDismissed()
    {
        using var db = TestDb.Create();
        var (rec, tr) = SeedTranscribedRecording(db);
        db.RecordingTags.AddRange(
            new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "stale-suggestion", Weight = 0.5, Ordinal = 0 },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "my-tag", Weight = 1.0, Ordinal = 1,
                Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "never-again", Weight = 0.4, Ordinal = 2,
                Status = RecordingTagStatus.Dismissed,
            });
        await db.SaveChangesAsync();

        await RunAsync(db, client: FakeTags(("Fresh", 0.7)), rec, tr);

        var byStatus = db.RecordingTags.ToList().ToDictionary(t => t.Tag, t => t.Status);
        Assert.False(byStatus.ContainsKey("stale-suggestion"));           // replaced
        Assert.Equal(RecordingTagStatus.Adopted, byStatus["my-tag"]);      // survived
        Assert.Equal(RecordingTagStatus.Dismissed, byStatus["never-again"]); // survived
        Assert.Equal(RecordingTagStatus.Suggested, byStatus["Fresh"]);     // added
    }

    [Fact]
    public async Task Extraction_DoesNotResuggest_AnAlreadyAdoptedTag_EvenInADifferentCase()
    {
        using var db = TestDb.Create();
        var (rec, tr) = SeedTranscribedRecording(db);
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "metadata", Weight = 1.0, Ordinal = 0,
            Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await RunAsync(db, client: FakeTags(("Metadata", 0.9)), rec, tr);

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal("metadata", tag.Tag);
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
    }

    [Fact]
    public async Task Extraction_DoesNotResuggest_ADismissedTag_EvenInADifferentCase()
    {
        using var db = TestDb.Create();
        var (rec, tr) = SeedTranscribedRecording(db);
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Boilerplate", Weight = 0.3, Ordinal = 0,
            Status = RecordingTagStatus.Dismissed,
        });
        await db.SaveChangesAsync();

        await RunAsync(db, client: FakeTags(("boilerplate", 0.9)), rec, tr);

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Dismissed, tag.Status);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagsProcessorTests"`
Expected: the four new tests FAIL - today every existing tag is removed and every extracted tag inserted, so `my-tag` and `never-again` vanish and `Metadata`/`boilerplate` get re-suggested.

- [ ] **Step 3: Implement**

In `src/Diariz.Api/Services/TagsProcessor.cs`, replace lines 67-78:

```csharp
            // Replace only AFTER a successful extraction so a failed re-run keeps the previous set - and
            // replace only the SUGGESTIONS. Adopted tags are the user's own and must survive a
            // re-transcription; dismissals are tombstones that stop a word coming back here.
            var keep = rec.Tags
                .Where(t => t.Status != RecordingTagStatus.Suggested)
                .ToList();
            db.RecordingTags.RemoveRange(rec.Tags.Where(t => t.Status == RecordingTagStatus.Suggested));

            var spoken = keep.Select(t => t.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordinal = 0;
            var newTags = extracted
                // Never re-offer a word the user already holds or has already rejected on this recording.
                .Where(e => !spoken.Contains(e.Tag))
                .Select(e => new RecordingTag
                {
                    Id = Guid.NewGuid(),
                    RecordingId = rec.Id,
                    Tag = e.Tag.Length > 64 ? e.Tag[..64] : e.Tag,
                    Weight = Math.Clamp(e.Weight, 0.0, 1.0),
                    Ordinal = ordinal++,
                    Status = RecordingTagStatus.Suggested,
                })
                .ToList();
            db.RecordingTags.AddRange(newTags);
```

Also update the class doc comment at lines 12-20: the phrase "REPLACES the recording's `RecordingTag`s
wholesale" and "Tags are machine-only (never user-edited)" are now false. Say instead that it replaces the
*suggestions* only, and that adopted and dismissed rows are left alone.

`newTags` still feeds `PublishTagsReadyAsync`, so `recording.tags_ready` now carries exactly the words that
were newly suggested - which is what a subscriber can act on. Leave the payload shape untouched.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagsProcessorTests"`
Expected: PASS, including the file's pre-existing tests (the "wholesale replace" test may need its
expectation updated to "replaces suggestions" - update the test's *name and comment* too, do not leave a
test whose name contradicts what it asserts).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/TagsProcessor.cs tests/Diariz.Api.Tests/TagsProcessorTests.cs
git commit -m "feat(tags): extraction only writes suggestions and spares adopted tags"
```

---

### Task 3: The tag cloud counts adopted tags only

**Files:**
- Modify: `src/Diariz.Api/Controllers/TagsController.cs:12-17` (class doc), `:39-47` (endpoint description), `:61-64` (the query)
- Test: `tests/Diariz.Api.Tests/TagsControllerTests.cs`

**Interfaces:**
- Consumes: `RecordingTagStatus` from Task 1.
- Produces: no signature change. `GET /api/tags` returns adopted tags only.

This is the highest-risk change in the plan: a missing filter here silently restores the noise the whole
feature exists to remove, and every other test would still pass. The test is mutation-verified in Step 5.

- [ ] **Step 1: Write the failing test**

In `tests/Diariz.Api.Tests/TagsControllerTests.cs`, extend the existing `AddTag` helper with a status
parameter and add the test:

```csharp
    private static void AddTag(
        DiarizDbContext db, Guid recId, string tag, double weight, int ordinal = 0,
        RecordingTagStatus status = RecordingTagStatus.Adopted) =>
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = recId, Tag = tag, Weight = weight, Ordinal = ordinal,
            Status = status,
            AdoptedAt = status == RecordingTagStatus.Adopted ? DateTimeOffset.UtcNow : null,
        });

    [Fact]
    public async Task List_ReturnsAdoptedTagsOnly_IgnoringSuggestionsAndDismissals()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        AddTag(db, rec.Id, "chosen", 1.0, 0, RecordingTagStatus.Adopted);
        AddTag(db, rec.Id, "merely-suggested", 0.9, 1, RecordingTagStatus.Suggested);
        AddTag(db, rec.Id, "rejected", 0.8, 2, RecordingTagStatus.Dismissed);
        await db.SaveChangesAsync();

        var list = (await Build(db, me).List()).Value!;

        Assert.Equal("chosen", Assert.Single(list).Tag);
    }
```

The existing tests in this file create tags with no status; the helper's `Adopted` default keeps them
meaningful (they are about aggregation, not status) - but read each one and confirm its intent still holds
rather than assuming.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagsControllerTests"`
Expected: `List_ReturnsAdoptedTagsOnly_IgnoringSuggestionsAndDismissals` FAILS - the cloud currently returns all three entries, so `Assert.Single` throws.

- [ ] **Step 3: Implement**

In `src/Diariz.Api/Controllers/TagsController.cs`, change the query at lines 61-64:

```csharp
        var rows = await (
            from t in _db.RecordingTags
            join r in recs on t.RecordingId equals r.Id
            // Adopted only. A suggestion nobody picked, and a word the user rejected, are both invisible to
            // every aggregate - that separation is the entire point of RecordingTagStatus.
            where t.Status == RecordingTagStatus.Adopted
            select new { t.Tag, t.Weight, t.RecordingId }).ToListAsync();
```

Then fix the endpoint description at lines 43-45, which currently says the opposite of the new behaviour:

```csharp
        "Tags are yours: topics are extracted automatically when a recording is summarised, but they are only " +
        "**suggestions** until you accept one on the recording, and this cloud counts accepted tags only. Add " +
        "or remove them with the tag endpoints on a recording. Matching is **case-insensitive** and the most " +
        "common casing is used for display.\n\n" +
```

Update the class doc comment at lines 12-17 in the same spirit ("every tag the caller has adopted across
their recordings").

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagsControllerTests"`
Expected: PASS (all tests in the class).

- [ ] **Step 5: Mutation-verify the filter**

Temporarily delete the `where t.Status == RecordingTagStatus.Adopted` line, re-run the command from Step 4,
and confirm the new test FAILS. Restore the line and confirm it passes again. A filter this consequential
must be protected by a test that provably fails without it.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/TagsController.cs tests/Diariz.Api.Tests/TagsControllerTests.cs
git commit -m "feat(tags): the tag cloud counts adopted tags only"
```

---

### Task 4: Normalising tag text (server)

**Files:**
- Create: `src/Diariz.Api/Services/TagText.cs`
- Test: `tests/Diariz.Api.Tests/TagTextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class TagText` with `public static string? Normalize(string? raw)` - returns the cleaned tag, or `null` when the input carries no usable text. Task 6 calls it; Task 8 mirrors the same rules in TypeScript.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/TagTextTests.cs`:

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TagTextTests
{
    [Theory]
    [InlineData("metadata", "metadata")]
    [InlineData("  metadata  ", "metadata")]
    [InlineData("Data Collection", "Data-Collection")]
    [InlineData("budget planning 2026", "budget-planning-2026")]
    [InlineData("spaced\tout\nword", "spaced-out-word")]
    [InlineData("many   spaces", "many-spaces")]
    [InlineData("-leading", "leading")]
    [InlineData("trailing-", "trailing")]
    [InlineData("--both--", "both")]
    public void Normalize_CollapsesWhitespaceToHyphens_AndTrims(string raw, string expected) =>
        Assert.Equal(expected, TagText.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("---")]
    public void Normalize_ReturnsNull_WhenThereIsNoUsableText(string? raw) =>
        Assert.Null(TagText.Normalize(raw));

    [Fact]
    public void Normalize_PreservesCase()
    {
        Assert.Equal("Roadmap", TagText.Normalize("Roadmap"));
        Assert.Equal("iOS", TagText.Normalize("iOS"));
    }

    [Fact]
    public void Normalize_TruncatesToTheColumnLength()
    {
        var result = TagText.Normalize(new string('x', 100));
        Assert.Equal(64, result!.Length);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagTextTests"`
Expected: FAIL to compile - `TagText` does not exist.

- [ ] **Step 3: Implement**

Create `src/Diariz.Api/Services/TagText.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>The one place that decides what a tag may look like. A tag never contains whitespace: internal
/// whitespace collapses to a hyphen so a pasted phrase becomes one token ("budget planning 2026" ->
/// "budget-planning-2026"). Case is preserved deliberately - suggestions arrive in the extraction prompt's
/// Title Case and hand-typed tags stay as typed, while every comparison and the tag cloud are
/// case-insensitive, so the two styles coexist without a data migration.
/// Mirrored in TypeScript by <c>apps/web/src/lib/tagInput.ts</c>; change both together.</summary>
public static partial class TagText
{
    /// <summary>Longest tag the <c>RecordingTags.Tag</c> column holds.</summary>
    public const int MaxLength = 64;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Cleans a raw tag, or returns null when nothing usable is left (blank, or hyphens only).</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var joined = Whitespace().Replace(raw.Trim(), "-").Trim('-');
        if (joined.Length == 0) return null;

        return joined.Length > MaxLength ? joined[..MaxLength] : joined;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TagTextTests"`
Expected: PASS (16 cases).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/TagText.cs tests/Diariz.Api.Tests/TagTextTests.cs
git commit -m "feat(tags): add the shared tag-text normalisation rule"
```

---

### Task 5: The recording detail carries adopted tags and suggestions

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:294-338` (`RecordingDetailDto` - append two parameters)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:172-182` (add the Include), `:242-246` (the projection)
- Test: `tests/Diariz.Api.Tests/RecordingsControllerTests.cs`

**Interfaces:**
- Consumes: `RecordingTagStatus` from Task 1.
- Produces: `RecordingDetailDto.Tags` (`IReadOnlyList<string>?`, adopted, `AdoptedAt` ascending) and `RecordingDetailDto.SuggestedTags` (`IReadOnlyList<string>?`, suggested, weight descending). Task 9 consumes both from the web client.

Both are appended as optional parameters with `null` defaults - `RecordingDetailDto` is a positional record
with many defaulted parameters, and every other projection that constructs it must keep compiling.

- [ ] **Step 1: Write the failing test**

Add to `tests/Diariz.Api.Tests/RecordingsControllerTests.cs` (reuse that file's existing controller-building
helper rather than the sketch's `Build`):

```csharp
    [Fact]
    public async Task Get_ReturnsAdoptedTagsInAdoptionOrder_AndSuggestionsByWeight_NeverDismissed()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = me, BlobKey = "k" };
        db.Recordings.Add(rec);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.RecordingTags.AddRange(
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "second", Weight = 1.0, Ordinal = 0,
                Status = RecordingTagStatus.Adopted, AdoptedAt = t0.AddMinutes(1),
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "first", Weight = 1.0, Ordinal = 1,
                Status = RecordingTagStatus.Adopted, AdoptedAt = t0,
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "light", Weight = 0.2, Ordinal = 2,
                Status = RecordingTagStatus.Suggested,
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "heavy", Weight = 0.9, Ordinal = 3,
                Status = RecordingTagStatus.Suggested,
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "gone", Weight = 0.7, Ordinal = 4,
                Status = RecordingTagStatus.Dismissed,
            });
        await db.SaveChangesAsync();

        var dto = (await BuildController(db, me).Get(rec.Id)).Value!;

        Assert.Equal(new[] { "first", "second" }, dto.Tags);
        Assert.Equal(new[] { "heavy", "light" }, dto.SuggestedTags);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingsControllerTests"`
Expected: FAIL to compile - `RecordingDetailDto` has no `Tags` or `SuggestedTags`.

- [ ] **Step 3: Extend the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, append two parameters to `RecordingDetailDto`, after `EndedAt`
(change the `EndedAt` line's closing `);` to `,`):

```csharp
    DateTimeOffset? EndedAt = null,
    /// <summary>The tags the user adopted on this recording, in the order they adopted them. These are the
    /// only tags the tag cloud counts.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>Tags the LLM proposed that nobody has accepted or dismissed yet, heaviest first - the hub's
    /// "pick or ignore" hints. Dismissed suggestions are never returned.</summary>
    IReadOnlyList<string>? SuggestedTags = null);
```

- [ ] **Step 4: Project them**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, add the include to the `Get` query (after
`.Include(r => r.Actions)` at line 174):

```csharp
            .Include(r => r.Tags)
```

Then, just before the `return new RecordingDetailDto(...)` at line 242:

```csharp
        // Adopted tags in adoption order (AdoptedAt, not CreatedAt: a promoted suggestion was created when
        // the extraction ran). Suggestions heaviest first, which is the order the hint list offers them in.
        var adoptedTags = rec.Tags
            .Where(t => t.Status == RecordingTagStatus.Adopted)
            .OrderBy(t => t.AdoptedAt ?? t.CreatedAt)
            .Select(t => t.Tag)
            .ToList();
        var suggestedTags = rec.Tags
            .Where(t => t.Status == RecordingTagStatus.Suggested)
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.Ordinal)
            .Select(t => t.Tag)
            .ToList();
```

and add the two arguments to the constructor call, after `rec.EndedAt`:

```csharp
            rec.UserId, recordedByName, visibleRooms, rec.StartedAt, rec.EndedAt, adoptedTags, suggestedTags);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingsControllerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/RecordingsControllerTests.cs
git commit -m "feat(tags): the recording detail carries adopted tags and suggestions"
```

---

### Task 6: Adopt, remove and dismiss endpoints

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (add two request records near `ApplyMeetingTypeRequest` at line 371)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (three new actions, placed after `ApplyMeetingType` which ends at line 1156)
- Test: `tests/Diariz.Api.Tests/RecordingTagEndpointsTests.cs`

**Interfaces:**
- Consumes: `TagText.Normalize` (Task 4), `RecordingTagStatus` (Task 1), `IRoomScope.CanReadRecordingAsync(Guid userId, Guid recordingId, CancellationToken ct = default)`.
- Produces: `POST /api/recordings/{id}/tags` (body `SetRecordingTagRequest(string Tag)`), `DELETE /api/recordings/{id}/tags?tag=x`, `POST /api/recordings/{id}/tags/dismiss` (body `SetRecordingTagRequest`). Task 9's API client calls all three.

**These are the first mutating endpoints in the codebase open to a non-owner.** A sweep of every
`[HttpPost/Put/Delete/Patch]` action found zero others gated by read access - notes and screenshots use
`CanReadRecordingAsync` for reads while keeping writes owner-only. Say so in the doc comments so nobody
widens a gate by pattern-matching this one.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/RecordingTagEndpointsTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>The hub's manual tagging: adopt (typed or promoted), remove, dismiss. Unlike every other write
/// on a recording these are open to anyone who can READ it, so the room cases below are load-bearing.</summary>
public class RecordingTagEndpointsTests
{
    private static Recording AddRecording(DiarizDbContext db, Guid ownerId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = ownerId, BlobKey = "k" };
        db.Recordings.Add(rec);
        return rec;
    }

    private static RecordingTag Tag(
        DiarizDbContext db, Guid recId, string tag, double weight, RecordingTagStatus status, int ordinal = 0)
    {
        var row = new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = recId, Tag = tag, Weight = weight, Ordinal = ordinal,
            Status = status,
            AdoptedAt = status == RecordingTagStatus.Adopted ? DateTimeOffset.UtcNow : null,
        };
        db.RecordingTags.Add(row);
        return row;
    }

    [Fact]
    public async Task AddTag_TypedByHand_CreatesAnAdoptedTagWithWeightOne()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("metadata"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal("metadata", tag.Tag);
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal(1.0, tag.Weight);
        Assert.NotNull(tag.AdoptedAt);
    }

    [Fact]
    public async Task AddTag_CollapsesWhitespaceToHyphens()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("budget planning 2026"));

        Assert.Equal("budget-planning-2026", Assert.Single(db.RecordingTags.ToList()).Tag);
    }

    [Fact]
    public async Task AddTag_PromotesAMatchingSuggestion_InsteadOfInsertingASecondRow()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Data Collection", 0.8, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("Data Collection"));

        var tag = Assert.Single(db.RecordingTags.ToList());   // flipped, not duplicated
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal(1.0, tag.Weight);
        Assert.NotNull(tag.AdoptedAt);
    }

    [Fact]
    public async Task AddTag_MatchesCaseInsensitively_WhenPromoting()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Metadata", 0.8, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("metadata"));

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal("Metadata", tag.Tag);   // the stored casing wins; we do not rewrite it
    }

    [Fact]
    public async Task AddTag_AlreadyAdopted_IsAnIdempotentNoOp()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        var existing = Tag(db, rec.Id, "metadata", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();
        var adoptedAt = existing.AdoptedAt;

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("METADATA"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(adoptedAt, tag.AdoptedAt);   // not re-stamped
    }

    [Fact]
    public async Task AddTag_RevivesADismissedTag_WhenTheUserTypesItAnyway()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "boilerplate", 0.3, RecordingTagStatus.Dismissed);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("boilerplate"));

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public async Task AddTag_RejectsUnusableText(string raw)
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest(raw));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.RecordingTags.ToList());
    }

    [Fact]
    public async Task RemoveTag_DeletesTheRow_SoItDoesNotReturnAsAHint()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "metadata", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).RemoveTag(rec.Id, "METADATA");

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.RecordingTags.ToList());
    }

    [Fact]
    public async Task RemoveTag_ThatIsNotThere_IsStillNoContent()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Recordings.Build(db, me).RemoveTag(rec.Id, "absent"));
    }

    [Fact]
    public async Task DismissTag_MarksTheSuggestionDismissed_AndKeepsTheRowAsATombstone()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Boilerplate", 0.3, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).DismissTag(rec.Id, new SetRecordingTagRequest("boilerplate"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Dismissed, tag.Status);
    }

    [Fact]
    public async Task DismissTag_WithNoSuchSuggestion_Is404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "mine", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).DismissTag(rec.Id, new SetRecordingTagRequest("mine"));

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(RecordingTagStatus.Adopted, Assert.Single(db.RecordingTags.ToList()).Status);
    }

    [Fact]
    public async Task ARoomMemberWhoIsNotTheOwner_CanAddRemoveAndDismiss()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        Users.Ensure(db, owner);
        Users.Ensure(db, member);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var rec = AddRecording(db, owner);
        Tag(db, rec.Id, "suggested-word", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(rec.Id, owner, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, member, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(rec.Id, roomId, owner, sectionId: null);

        var ctl = Recordings.Build(db, member);
        Assert.IsType<NoContentResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("theirs")));
        Assert.IsType<NoContentResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("suggested-word")));
        Assert.IsType<NoContentResult>(await ctl.RemoveTag(rec.Id, "theirs"));
    }

    [Fact]
    public async Task SomeoneWhoCannotSeeTheRecording_Gets404FromAllThree()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        Users.Ensure(db, owner);
        Users.Ensure(db, stranger);
        var rec = AddRecording(db, owner);
        Tag(db, rec.Id, "theirs", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        var ctl = Recordings.Build(db, stranger);
        Assert.IsType<NotFoundResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("mine")));
        Assert.IsType<NotFoundResult>(await ctl.RemoveTag(rec.Id, "theirs"));
        Assert.IsType<NotFoundResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("theirs")));
        Assert.Equal(RecordingTagStatus.Suggested, Assert.Single(db.RecordingTags.ToList()).Status);
    }
}
```

`Recordings.Build(db, userId)` stands for however the existing tests construct a `RecordingsController` -
its constructor takes 15 dependencies, so **find the existing helper** in
`tests/Diariz.Api.Tests/RecordingsControllerTests.cs` (or `TestSupport`) and use it. If it is private to
that file, lift it into `tests/Diariz.Api.TestSupport` so both files share one construction site rather
than copying a 15-argument call. Do not change the controller's constructor.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingTagEndpointsTests"`
Expected: FAIL to compile - `SetRecordingTagRequest`, `AddTag`, `RemoveTag`, `DismissTag` do not exist.

- [ ] **Step 3: Add the request record**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, after `ApplyMeetingTypeRequest` (line 371):

```csharp
/// <summary>Adopt or dismiss one tag on a recording. The text is normalised server-side (whitespace becomes
/// hyphens, hyphens trimmed) and matched case-insensitively against the recording's existing tags.</summary>
public record SetRecordingTagRequest(string Tag);
```

- [ ] **Step 4: Implement the three actions**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, after `ApplyMeetingType` ends (line 1156):

```csharp
    /// <summary>Adopt a tag on a recording - either typed by hand or promoted from a suggestion. Idempotent.
    /// Unlike the other writes here this is open to ANYONE WHO CAN READ the recording (a room member, not
    /// just the owner), because the tag cloud is room-scoped and a shared room shares its organising layer.
    /// It is the only write on a recording with that gate - do not copy it onto a neighbouring endpoint
    /// without meaning to.</summary>
    [HttpPost("{id:guid}/tags")]
    [EndpointSummary("Add a tag to a recording")]
    [EndpointDescription(
        "Adds one tag. Automatic topic extraction only ever **suggests** tags; this is how a tag becomes real " +
        "and starts counting towards your tag cloud. Promoting a suggestion is the same call - pass its text " +
        "and it is accepted in place. Whitespace inside the tag becomes hyphens (`budget planning` -> " +
        "`budget-planning`), matching is case-insensitive, and adding a tag you already have does nothing. " +
        "Anyone who can see the recording can tag it; 400 for blank text, 404 if you cannot see it.")]
    public async Task<IActionResult> AddTag(Guid id, SetRecordingTagRequest req)
    {
        if (!await _rooms.CanReadRecordingAsync(UserId, id)) return NotFound();

        var tag = TagText.Normalize(req.Tag);
        if (tag is null) return BadRequest("A tag needs some text.");

        var existing = await _db.RecordingTags
            .FirstOrDefaultAsync(t => t.RecordingId == id && t.Tag.ToLower() == tag.ToLower());

        if (existing is null)
        {
            _db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(),
                RecordingId = id,
                Tag = tag,
                // Adopted tags all weigh the same: the cloud sums weight, so 1.0 makes the sum a recording
                // count and sizes words by how often the user reached for them.
                Weight = 1.0,
                Ordinal = await _db.RecordingTags.CountAsync(t => t.RecordingId == id),
                Status = RecordingTagStatus.Adopted,
                AdoptedAt = DateTimeOffset.UtcNow,
            });
        }
        else if (existing.Status != RecordingTagStatus.Adopted)
        {
            // Promotion (or reviving something previously dismissed): flip the row we already have, keeping
            // its stored casing, so the one-row-per-tag-per-recording index stays satisfied.
            existing.Status = RecordingTagStatus.Adopted;
            existing.Weight = 1.0;
            existing.AdoptedAt = DateTimeOffset.UtcNow;
        }
        // else: already adopted - leave AdoptedAt alone so re-adding does not reshuffle the chip order.

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Remove an adopted tag. Deletes the row outright, so it does not reappear as a suggestion;
    /// only a re-transcription can offer it again. Open to anyone who can read the recording (see
    /// <see cref="AddTag"/>).</summary>
    [HttpDelete("{id:guid}/tags")]
    [EndpointSummary("Remove a tag from a recording")]
    [EndpointDescription(
        "Removes the tag from this recording, case-insensitively. It does not come back as a suggestion - only " +
        "a re-transcription can propose it again. Removing a tag that is not there succeeds (204), so a retry " +
        "is safe. Anyone who can see the recording can do this; 404 if you cannot see it.")]
    public async Task<IActionResult> RemoveTag(Guid id, [FromQuery] string tag)
    {
        if (!await _rooms.CanReadRecordingAsync(UserId, id)) return NotFound();

        var normalized = TagText.Normalize(tag);
        if (normalized is null) return NoContent();

        var existing = await _db.RecordingTags
            .FirstOrDefaultAsync(t => t.RecordingId == id && t.Tag.ToLower() == normalized.ToLower());
        if (existing is not null)
        {
            _db.RecordingTags.Remove(existing);
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    /// <summary>Reject a suggested tag for this recording. The row is kept as a tombstone so a
    /// re-transcription cannot suggest it here again. Open to anyone who can read the recording (see
    /// <see cref="AddTag"/>).</summary>
    [HttpPost("{id:guid}/tags/dismiss")]
    [EndpointSummary("Dismiss a suggested tag")]
    [EndpointDescription(
        "Rejects one of the automatically suggested tags on this recording so it stops being offered here, " +
        "even after a re-transcription. The dismissal is per recording - the same word can still be suggested " +
        "on other meetings. 404 when there is no such suggestion (including when you already accepted it). " +
        "Anyone who can see the recording can do this.")]
    public async Task<IActionResult> DismissTag(Guid id, SetRecordingTagRequest req)
    {
        if (!await _rooms.CanReadRecordingAsync(UserId, id)) return NotFound();

        var tag = TagText.Normalize(req.Tag);
        if (tag is null) return BadRequest("A tag needs some text.");

        var suggestion = await _db.RecordingTags.FirstOrDefaultAsync(t =>
            t.RecordingId == id
            && t.Status == RecordingTagStatus.Suggested
            && t.Tag.ToLower() == tag.ToLower());
        if (suggestion is null) return NotFound();

        suggestion.Status = RecordingTagStatus.Dismissed;
        await _db.SaveChangesAsync();
        return NoContent();
    }
```

`t.Tag.ToLower() == tag.ToLower()` is deliberate: it translates to SQL `lower()` on Npgsql (matching the
functional index) and also works under the in-memory provider, whereas `string.Equals(..., OrdinalIgnoreCase)`
does not translate.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingTagEndpointsTests"`
Expected: PASS (15 tests).

- [ ] **Step 6: Run the whole .NET suite**

Run: `dotnet build Diariz.slnx` then `dotnet test tests/Diariz.Api.Tests`
Expected: all green. Then `dotnet test tests/Diariz.Api.IntegrationTests` (Docker) - the OpenAPI snapshot test rewrites its own snapshot, so if it fails once, re-run it and commit the regenerated file.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/RecordingTagEndpointsTests.cs
git commit -m "feat(tags): add, remove and dismiss tags on a recording"
```

If the snapshot test regenerated a file, add it in the same commit.

---

### Task 7: Integration coverage for the real database

**Files:**
- Modify: `tests/Diariz.Api.IntegrationTests/TagsIntegrationTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: nothing.

The in-memory provider does not translate `lower()` the way Postgres does and cannot enforce the unique
index, so the promotion path and the adopted-only aggregation get verified for real here.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Diariz.Api.IntegrationTests/TagsIntegrationTests.cs`, following that file's existing fixture
and controller-construction idiom:

```csharp
    [Fact]
    public async Task Cloud_AggregatesAdoptedTagsAcrossRecordings_IgnoringSuggestionsOnRealPostgres()
    {
        var userId = Guid.NewGuid();
        Guid recA, recB;
        await using (var db = _fx.CreateDbContext())
        {
            db.Users.Add(new User { Id = userId, Email = $"{userId}@example.test", PasswordHash = "x" });
            var a = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}" };
            var b = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}" };
            db.Recordings.AddRange(a, b);
            recA = a.Id;
            recB = b.Id;
            db.RecordingTags.AddRange(
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = a.Id, Tag = "roadmap", Weight = 1.0,
                    Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
                },
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "Roadmap", Weight = 1.0,
                    Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
                },
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "noise", Weight = 0.9,
                    Status = RecordingTagStatus.Suggested,
                });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDbContext())
        {
            var list = (await BuildTagsController(db, userId).List()).Value!;
            var entry = Assert.Single(list);
            Assert.Equal(2, entry.Count);            // case variants merge across recordings
            Assert.Equal(2.0, entry.Weight, 3);      // adopted weight 1.0 each -> sum == recording count
            Assert.Equal(2, entry.RecordingIds.Count);
            Assert.Contains(recA, entry.RecordingIds);
            Assert.Contains(recB, entry.RecordingIds);
        }
    }

    [Fact]
    public async Task AddTag_PromotesACaseVariantSuggestion_WithoutViolatingTheUniqueIndex()
    {
        var userId = Guid.NewGuid();
        Guid recId;
        await using (var db = _fx.CreateDbContext())
        {
            db.Users.Add(new User { Id = userId, Email = $"{userId}@example.test", PasswordHash = "x" });
            var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}" };
            db.Recordings.Add(rec);
            recId = rec.Id;
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Data Collection", Weight = 0.8,
                Status = RecordingTagStatus.Suggested,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _fx.CreateDbContext())
        {
            var result = await BuildRecordingsController(db, userId)
                .AddTag(recId, new SetRecordingTagRequest("data collection"));
            Assert.IsType<NoContentResult>(result);
        }

        await using (var db = _fx.CreateDbContext())
        {
            var tag = Assert.Single(await db.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
            Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
            Assert.Equal("Data Collection", tag.Tag);
        }
    }
```

`BuildTagsController` / `BuildRecordingsController` again stand for the file's real helpers. The recordings
controller needs 15 dependencies - if the integration project has no helper yet, reuse the `TestSupport`
one from Task 6 Step 1.

- [ ] **Step 2: Run to verify they fail, then pass**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~TagsIntegrationTests"`
Expected: they should pass immediately if Tasks 1-6 are correct - these tests exist to catch a
provider-behaviour difference, not to drive new code. **If either fails, that is a real bug in Task 3 or 6
on Postgres** (most likely the `ToLower()` translation or the weight arithmetic); fix the production code,
not the test.

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/TagsIntegrationTests.cs
git commit -m "test(tags): cover adopted-only aggregation and case-variant promotion on Postgres"
```

---

### Task 8: The tag input rules (web, pure)

**Files:**
- Create: `apps/web/src/lib/tagInput.ts`
- Test: `apps/web/src/lib/tagInput.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `normalizeTag(raw: string): string | null` and `addTag(list: string[], raw: string): { tags: string[]; added: string | null }`. Tasks 10 and 11 consume both.

`addTag` returns the added tag as well as the new list so the caller knows whether to fire a mutation - a
duplicate must not send a request.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/lib/tagInput.test.ts`:

```typescript
import { describe, expect, it } from "vitest";
import { addTag, normalizeTag } from "./tagInput";

describe("normalizeTag", () => {
  it("keeps a single word as typed, preserving case", () => {
    expect(normalizeTag("metadata")).toBe("metadata");
    expect(normalizeTag("Roadmap")).toBe("Roadmap");
    expect(normalizeTag("iOS")).toBe("iOS");
  });

  it("joins a phrase with hyphens", () => {
    expect(normalizeTag("budget planning 2026")).toBe("budget-planning-2026");
    expect(normalizeTag("Data Collection")).toBe("Data-Collection");
  });

  it("collapses runs of whitespace and trims the edges", () => {
    expect(normalizeTag("  many   spaces  ")).toBe("many-spaces");
    expect(normalizeTag("line\nbreak\ttab")).toBe("line-break-tab");
  });

  it("trims leading and trailing hyphens", () => {
    expect(normalizeTag("-leading")).toBe("leading");
    expect(normalizeTag("trailing-")).toBe("trailing");
    expect(normalizeTag("--both--")).toBe("both");
  });

  it("returns null when nothing usable is left", () => {
    expect(normalizeTag("")).toBeNull();
    expect(normalizeTag("   ")).toBeNull();
    expect(normalizeTag("-")).toBeNull();
    expect(normalizeTag("---")).toBeNull();
  });

  it("truncates to the column length", () => {
    expect(normalizeTag("x".repeat(100))).toHaveLength(64);
  });
});

describe("addTag", () => {
  it("appends a new tag and reports what it added", () => {
    expect(addTag(["one"], "two")).toEqual({ tags: ["one", "two"], added: "two" });
  });

  it("normalises before adding", () => {
    expect(addTag([], "budget planning")).toEqual({
      tags: ["budget-planning"],
      added: "budget-planning",
    });
  });

  it("rejects a case-insensitive duplicate without changing the list", () => {
    const list = ["metadata"];
    const result = addTag(list, "METADATA");
    expect(result).toEqual({ tags: ["metadata"], added: null });
    expect(result.tags).toEqual(list);
  });

  it("rejects unusable text", () => {
    expect(addTag(["one"], "  ")).toEqual({ tags: ["one"], added: null });
  });

  it("does not mutate the list it was given", () => {
    const list = ["one"];
    addTag(list, "two");
    expect(list).toEqual(["one"]);
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/lib/tagInput.test.ts`
Expected: FAIL - cannot resolve `./tagInput`.

- [ ] **Step 3: Implement**

Create `apps/web/src/lib/tagInput.ts`:

```typescript
/// What a tag may look like, client-side. Mirrors `TagText.Normalize` in
/// `src/Diariz.Api/Services/TagText.cs` - change both together, since the server normalises again and a
/// drift between the two would show up as a chip that renames itself after a refetch.
///
/// A tag never contains whitespace: internal whitespace becomes a hyphen so a pasted phrase lands as one
/// token. Case is kept as typed; every comparison is case-insensitive.

/// Longest tag the API stores (the `RecordingTags.Tag` column).
const MAX_LENGTH = 64;

/// Cleans a raw tag, or returns null when there is no usable text (blank, or hyphens only).
export function normalizeTag(raw: string): string | null {
  const joined = raw.trim().replace(/\s+/g, "-").replace(/^-+|-+$/g, "");
  if (joined.length === 0) return null;
  return joined.length > MAX_LENGTH ? joined.slice(0, MAX_LENGTH) : joined;
}

/// Adds `raw` to `list`, case-insensitively de-duplicated. `added` is the tag that went in, or null when
/// the input was unusable or already present - the caller uses it to decide whether to call the API, so a
/// duplicate never becomes a request. Never mutates `list`.
export function addTag(list: string[], raw: string): { tags: string[]; added: string | null } {
  const tag = normalizeTag(raw);
  if (tag === null) return { tags: list, added: null };

  const lower = tag.toLowerCase();
  if (list.some((t) => t.toLowerCase() === lower)) return { tags: list, added: null };

  return { tags: [...list, tag], added: tag };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/lib/tagInput.test.ts`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/tagInput.ts apps/web/src/lib/tagInput.test.ts
git commit -m "feat(tags): add the web tag-input normalisation rules"
```

---

### Task 9: Web API client, types and copy

**Files:**
- Modify: `apps/web/src/lib/types.ts` (the `RecordingDetail` interface; `TagCloudEntry` sits at lines 45-54 for reference)
- Modify: `apps/web/src/lib/api.ts` (three methods beside `listTags`, which is at lines 763-768)
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: none of its own - Tasks 10 and 11 exercise all of it. This task exists because it is the shared vocabulary those two tasks compile against.

**Interfaces:**
- Consumes: the endpoints from Task 6.
- Produces: `RecordingDetail.tags?: string[]`, `RecordingDetail.suggestedTags?: string[]`; `api.addRecordingTag(recordingId, tag)`, `api.removeRecordingTag(recordingId, tag)`, `api.dismissRecordingTag(recordingId, tag)`, all `Promise<void>`; the `tagsPill*` / `tagsPopover*` i18n keys listed below.

- [ ] **Step 1: Extend the detail type**

In `apps/web/src/lib/types.ts`, add to the `RecordingDetail` interface (the one whose `endedAt` field ends
at line 42):

```typescript
  /// Tags the user adopted on this recording, in adoption order. The only tags the tag cloud counts.
  tags?: string[];
  /// Automatically suggested tags nobody has accepted or dismissed yet, heaviest first - the hub's hints.
  suggestedTags?: string[];
```

- [ ] **Step 2: Add the API methods**

In `apps/web/src/lib/api.ts`, immediately after `listTags` (line 768):

```typescript
  /// Adopt a tag on a recording - typed by hand, or promoted from a suggestion (same call, pass its text).
  /// Idempotent, so the optimistic UI can retry safely.
  async addRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.post(`/api/recordings/${recordingId}/tags`, { tag });
  },

  /// Remove an adopted tag. It does not return as a suggestion - only a re-transcription can offer it again.
  async removeRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/tags`, { params: { tag } });
  },

  /// Reject a suggested tag so it stops being offered on this recording, even after a re-transcription.
  async dismissRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.post(`/api/recordings/${recordingId}/tags/dismiss`, { tag });
  },
```

- [ ] **Step 3: Add the copy to all four locales**

Add these keys to `apps/web/src/locales/en/workspace.json` beside the existing `tag*` keys (around lines
494-500). **No em or en dashes.**

```json
  "tagsPillLabel": "Tags",
  "tagsPillEmptyTitle": "No tags yet - click to add",
  "tagsPillMore": "+{{count}} more",
  "tagsPopoverTitle": "Tags",
  "tagsPopoverSaved": "saved as you type",
  "tagsPopoverClose": "Close",
  "tagsInputPlaceholder": "Add a tag...",
  "tagsInputLabel": "Add a tag",
  "tagsInputHint": "Space or Enter adds the word · pasting a phrase joins it with hyphens · Backspace removes the last tag",
  "tagsRemove": "Remove tag",
  "tagsSuggestedLabel": "AUTO-GENERATED · PICK OR IGNORE",
  "tagsSuggestedLeft": "{{count}} left",
  "tagsSuggestedAdd": "Add this tag",
  "tagsSuggestedDismiss": "Never suggest this",
  "tagsSuggestedDone": "All suggestions dealt with."
```

Then translate the same keys into `de`, `es` and `fr` `workspace.json`. Do not invent vocabulary: read each
locale's existing `tabTags`, `tagsEmpty`, `tagCloudTitle` and `tagCountLabel` values first and reuse whatever
word that language already uses for "tag", so the popover matches the Tags tab beside it. Keep the `·`
separators (a middot is not a dash, so it is fine); keep `{{count}}` interpolation intact; no em or en dashes.

- [ ] **Step 4: Re-read the Tags tab empty state**

The tag cloud is empty until tags are adopted, so `tagsEmpty` is the first thing a user sees in the Tags tab
after this ships. Read its current value in all four locales. It was written for "this library has no tags",
and if it now reads as though something is broken or missing, reword it to say the user has not tagged
anything yet and where to do it. This is the one piece of copy the feature makes misleading without touching
it.

- [ ] **Step 5: Typecheck**

Run: `cd apps/web && npm run build`
Expected: PASS (tsc + vite build). Nothing consumes the new methods yet, so this only proves the types and JSON parse.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(tags): add the web tag client, types and copy"
```

---

### Task 10: The Tags pill

**Files:**
- Modify: `apps/web/src/components/icons.tsx` (add `TagIcon`)
- Create: `apps/web/src/components/detail/TagsPill.tsx`
- Test: `apps/web/src/components/detail/TagsPill.test.tsx`

**Interfaces:**
- Consumes: `TagIcon`, the i18n keys from Task 9.
- Produces: `TagIcon({ size }: { size?: number })`; `TagsPill({ count, tags, open, onToggle })` where `count: number`, `tags: string[]`, `open: boolean`, `onToggle: () => void`. Task 11 renders it and owns `open`.

The pill is presentational: it holds no state and does no fetching, so its hover text and accessibility
attributes can be tested without a query client.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/detail/TagsPill.test.tsx`. Follow the render/i18n idiom in
`apps/web/src/components/RecordingsPanel.test.tsx` (i18n is booted globally by `src/test-setup.ts` and
pinned to `en`, so English strings can be asserted directly).

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import TagsPill from "./TagsPill";

describe("TagsPill", () => {
  it("shows the tag count", () => {
    render(<TagsPill count={3} tags={["a", "b", "c"]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill).toHaveTextContent("3");
  });

  it("names the first four tags in its hover text", () => {
    render(
      <TagsPill
        count={4}
        tags={["one", "two", "three", "four"]}
        open={false}
        onToggle={() => {}}
      />,
    );
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute(
      "title",
      "one · two · three · four",
    );
  });

  it("summarises the rest when there are more than four", () => {
    render(
      <TagsPill
        count={6}
        tags={["one", "two", "three", "four", "five", "six"]}
        open={false}
        onToggle={() => {}}
      />,
    );
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute(
      "title",
      "one · two · three · four · +2 more",
    );
  });

  it("invites a first tag when there are none", () => {
    render(<TagsPill count={0} tags={[]} open={false} onToggle={() => {}} />);
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill).toHaveAttribute("title", "No tags yet - click to add");
    expect(pill).toHaveTextContent("0");
  });

  it("reports its popover state and toggles on click", async () => {
    const onToggle = vi.fn();
    render(<TagsPill count={0} tags={[]} open={false} onToggle={onToggle} />);
    const pill = screen.getByRole("button", { name: "Tags" });

    expect(pill).toHaveAttribute("aria-expanded", "false");
    expect(pill).toHaveAttribute("aria-haspopup", "dialog");

    await userEvent.click(pill);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it("marks itself expanded while the popover is open", () => {
    render(<TagsPill count={1} tags={["a"]} open onToggle={() => {}} />);
    expect(screen.getByRole("button", { name: "Tags" })).toHaveAttribute("aria-expanded", "true");
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/components/detail/TagsPill.test.tsx`
Expected: FAIL - cannot resolve `./TagsPill`.

- [ ] **Step 3: Add the icon**

In `apps/web/src/components/icons.tsx`, following that file's existing icon style:

```tsx
/// A luggage-tag outline with its punched hole. The tag family's only new glyph.
export function TagIcon({ size = 16 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M20.6 13.4 12.6 21.4a2 2 0 0 1-2.8 0l-7-7a2 2 0 0 1-.6-1.4V4a2 2 0 0 1 2-2h8a2 2 0 0 1 1.4.6l7 7a2 2 0 0 1 0 2.8z" />
      <circle cx="7.5" cy="7.5" r="1.3" fill="currentColor" />
    </svg>
  );
}
```

Check the file's existing exports first: if icons there take `iconProps` or a different signature, match it.

- [ ] **Step 4: Implement the pill**

Create `apps/web/src/components/detail/TagsPill.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { TagIcon } from "../icons";

/// How many tags the hover text names before it summarises the rest.
const NAMED_IN_TITLE = 4;

/// The hero card's tag control: a count, and the way into the tag popover. Presentational - the parent owns
/// whether the popover is open, and all the tag data comes in as props, so this file has no state at all.
export default function TagsPill({
  count,
  tags,
  open,
  onToggle,
}: {
  count: number;
  tags: string[];
  open: boolean;
  onToggle: () => void;
}) {
  const { t } = useTranslation(["workspace"]);

  /// Hover text: name the first few tags, then say how many more there are. With none, invite a first one.
  const title =
    tags.length === 0
      ? t("workspace:tagsPillEmptyTitle")
      : [
          ...tags.slice(0, NAMED_IN_TITLE),
          ...(tags.length > NAMED_IN_TITLE
            ? [t("workspace:tagsPillMore", { count: tags.length - NAMED_IN_TITLE })]
            : []),
        ].join(" · ");

  return (
    <button
      type="button"
      onClick={onToggle}
      title={title}
      aria-label={t("workspace:tagsPillLabel")}
      aria-haspopup="dialog"
      aria-expanded={open}
      className="inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold transition-colors"
      style={{
        border: "1px solid rgba(47,107,237,.45)",
        background: "rgba(47,107,237,.16)",
        color: "var(--hub-blue-text)",
      }}
    >
      <TagIcon size={14} />
      <span>{t("workspace:tagsPillLabel")}</span>
      <span className="font-medium" style={{ color: "var(--hub-muted)" }}>
        {count}
      </span>
      <svg
        width={12}
        height={12}
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth={2}
        strokeLinecap="round"
        strokeLinejoin="round"
        style={{ color: "var(--hub-muted)" }}
        aria-hidden="true"
      >
        <path d="m6 9 6 6 6-6" />
      </svg>
    </button>
  );
}
```

The hover background (`rgba(47,107,237,.28)`) cannot be expressed in an inline style hover, so add a
`hover:` utility or a small class in `apps/web/src/index.css` beside the other `--hub-*` rules; do not drop
the hover state.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/detail/TagsPill.test.tsx`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/icons.tsx apps/web/src/components/detail/TagsPill.tsx apps/web/src/components/detail/TagsPill.test.tsx apps/web/src/index.css
git commit -m "feat(tags): add the hub Tags pill"
```

---

### Task 11: The Tags popover

**Files:**
- Create: `apps/web/src/components/detail/TagsPopover.tsx`
- Test: `apps/web/src/components/detail/TagsPopover.test.tsx`

**Interfaces:**
- Consumes: `HubPopover` (`apps/web/src/components/hub/HubPopover.tsx`, props `open`, `onClose`, `anchorClassName?`, `width?`, `ariaLabel?`), `addTag`/`normalizeTag` (Task 8), `TagIcon` (Task 10), the i18n keys (Task 9).
- Produces: `TagsPopover({ open, onClose, tags, suggested, onAdd, onRemove, onDismiss })` - `tags: string[]`, `suggested: string[]`, `onAdd: (tag: string) => void`, `onRemove: (tag: string) => void`, `onDismiss: (tag: string) => void`. Task 12 supplies the callbacks and the data.

Callbacks rather than mutations keep this file testable without a query client; Task 12 owns the server
round-trip and the optimistic state.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/detail/TagsPopover.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { describe, expect, it, vi } from "vitest";
import TagsPopover from "./TagsPopover";

function setup(props: Partial<ComponentProps<typeof TagsPopover>> = {}) {
  const handlers = {
    onClose: vi.fn(),
    onAdd: vi.fn(),
    onRemove: vi.fn(),
    onDismiss: vi.fn(),
  };
  render(
    <TagsPopover open tags={[]} suggested={[]} {...handlers} {...props} />,
  );
  return handlers;
}

describe("TagsPopover", () => {
  it("commits a word on space and keeps the field focused for the next one", async () => {
    const { onAdd } = setup();
    const input = screen.getByLabelText("Add a tag");

    await userEvent.type(input, "metadata ");

    expect(onAdd).toHaveBeenCalledWith("metadata");
    expect(input).toHaveFocus();
    expect(input).toHaveValue("");
  });

  it("commits on Enter and closes, because Enter means done", async () => {
    const { onAdd, onClose } = setup();

    await userEvent.type(screen.getByLabelText("Add a tag"), "metadata{Enter}");

    expect(onAdd).toHaveBeenCalledWith("metadata");
    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Enter with an empty field without adding anything", async () => {
    const { onAdd, onClose } = setup();

    await userEvent.type(screen.getByLabelText("Add a tag"), "{Enter}");

    expect(onAdd).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("does not add a tag it already has", async () => {
    const { onAdd } = setup({ tags: ["metadata"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "METADATA ");

    expect(onAdd).not.toHaveBeenCalled();
  });

  it("joins a pasted phrase with hyphens", async () => {
    const { onAdd } = setup();
    const input = screen.getByLabelText("Add a tag");

    await userEvent.click(input);
    await userEvent.paste("budget planning 2026");

    expect(onAdd).toHaveBeenCalledWith("budget-planning-2026");
  });

  it("removes the last tag on Backspace in an empty field", async () => {
    const { onRemove } = setup({ tags: ["first", "last"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "{Backspace}");

    expect(onRemove).toHaveBeenCalledWith("last");
  });

  it("leaves the tags alone when Backspace edits the draft", async () => {
    const { onRemove } = setup({ tags: ["first"] });

    await userEvent.type(screen.getByLabelText("Add a tag"), "ab{Backspace}");

    expect(onRemove).not.toHaveBeenCalled();
  });

  it("removes a tag from its chip", async () => {
    const { onRemove } = setup({ tags: ["metadata", "licensing"] });

    await userEvent.click(screen.getAllByRole("button", { name: "Remove tag" })[0]);

    expect(onRemove).toHaveBeenCalledWith("metadata");
  });

  it("promotes a suggestion when its label is clicked", async () => {
    const { onAdd } = setup({ suggested: ["templates", "document-map"] });

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    expect(onAdd).toHaveBeenCalledWith("templates");
  });

  it("dismisses a suggestion from its own control", async () => {
    const { onDismiss } = setup({ suggested: ["templates"] });

    await userEvent.click(screen.getByRole("button", { name: "Never suggest this" }));

    expect(onDismiss).toHaveBeenCalledWith("templates");
  });

  it("counts the suggestions still to deal with", () => {
    setup({ suggested: ["a", "b", "c"] });
    expect(screen.getByText("3 left")).toBeInTheDocument();
  });

  it("says so when every suggestion has been dealt with", () => {
    setup({ suggested: [] });
    expect(screen.getByText("All suggestions dealt with.")).toBeInTheDocument();
  });

  it("renders nothing when closed", () => {
    setup({ open: false });
    expect(screen.queryByLabelText("Add a tag")).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/components/detail/TagsPopover.test.tsx`
Expected: FAIL - cannot resolve `./TagsPopover`.

- [ ] **Step 3: Implement**

Create `apps/web/src/components/detail/TagsPopover.tsx`. Take every colour, size, radius and spacing value
from `docs/design_handoff_manual_tagging/README.md` (section 2) - it is final and specifies both themes via
the `--hub-*` tokens. The structure and behaviour:

```tsx
import { useRef, useState, type ClipboardEvent, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "../hub/HubPopover";
import { TagIcon } from "../icons";
import { addTag, normalizeTag } from "../../lib/tagInput";

/// The hub's tag editor: type tags, remove tags, and pick or ignore the automatically suggested ones.
/// Presentational and callback-driven - the parent owns the data and the server round-trip, so this file
/// can be tested without a query client. There is no Save button: each action is its own change, which is
/// what the header's "saved as you type" promises.
export default function TagsPopover({
  open,
  onClose,
  tags,
  suggested,
  onAdd,
  onRemove,
  onDismiss,
}: {
  open: boolean;
  onClose: () => void;
  tags: string[];
  suggested: string[];
  onAdd: (tag: string) => void;
  onRemove: (tag: string) => void;
  onDismiss: (tag: string) => void;
}) {
  const { t } = useTranslation(["workspace"]);
  const [draft, setDraft] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);

  /// Commits the draft if it is a tag we do not already have. Returns nothing: the parent decides what a
  /// successful add does to `tags`, and this component re-renders from the new props.
  function commit(raw: string) {
    const { added } = addTag(tags, raw);
    if (added !== null) onAdd(added);
    setDraft("");
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === " ") {
      // Space ends a word. Prevent default so the space never reaches the field - a tag has no spaces.
      e.preventDefault();
      if (draft.trim().length > 0) commit(draft);
      return;
    }
    if (e.key === "Enter") {
      // Enter means "done": commit whatever is there, then close.
      e.preventDefault();
      if (draft.trim().length > 0) commit(draft);
      onClose();
      return;
    }
    if (e.key === "Backspace" && draft.length === 0 && tags.length > 0) {
      // An empty field plus Backspace reaches back into the chips.
      e.preventDefault();
      onRemove(tags[tags.length - 1]);
    }
  }

  function onPaste(e: ClipboardEvent<HTMLInputElement>) {
    // A pasted phrase becomes one hyphenated tag rather than silently losing its spaces.
    const pasted = e.clipboardData.getData("text");
    if (normalizeTag(pasted) === null) return;
    e.preventDefault();
    commit(pasted);
  }

  return (
    <HubPopover open={open} onClose={onClose} width={392} ariaLabel={t("workspace:tagsPopoverTitle")}>
      <div className="flex flex-col gap-3" style={{ padding: "16px 18px 18px" }}>
        {/* a. Header: what this is, that it saves itself, and a way out. */}
        <div className="flex items-center gap-2.5">
          <span
            className="grid shrink-0 place-items-center"
            style={{ width: 19, height: 22, color: "var(--hub-blue)" }}
          >
            <TagIcon size={19} />
          </span>
          <h3 className="text-[16px] font-bold" style={{ color: "var(--hub-text)" }}>
            {t("workspace:tagsPopoverTitle")}
          </h3>
          <span className="text-[11px]" style={{ color: "var(--hub-muted)" }}>
            {t("workspace:tagsPopoverSaved")}
          </span>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("workspace:tagsPopoverClose")}
            className="ml-auto grid h-7 w-7 place-items-center rounded-lg text-base hover:bg-[var(--hub-surface-hover)]"
            style={{ color: "var(--hub-muted)" }}
          >
            ✕
          </button>
        </div>

        {/* b. One control: the chips the user has, and the field that adds the next one. */}
        <div>
          <div
            onClick={() => inputRef.current?.focus()}
            className="flex flex-wrap items-center gap-1.5"
            style={{
              minHeight: 46,
              padding: 8,
              borderRadius: 10,
              border: "1px solid var(--hub-field-border)",
              background: "var(--hub-surface)",
              cursor: "text",
            }}
          >
            {tags.map((tag) => (
              <span
                key={tag}
                className="inline-flex items-center text-[12.5px] font-medium"
                style={{
                  height: 28,
                  padding: "0 4px 0 10px",
                  borderRadius: 8,
                  background: "rgba(47,107,237,.16)",
                  border: "1px solid rgba(47,107,237,.35)",
                  color: "var(--hub-blue-text)",
                }}
              >
                {tag}
                <button
                  type="button"
                  onClick={() => onRemove(tag)}
                  aria-label={t("workspace:tagsRemove")}
                  className="ml-0.5 grid place-items-center rounded-md hover:bg-[var(--hub-surface-hover)]"
                  style={{ width: 22, height: 22, fontSize: 15 }}
                >
                  ✕
                </button>
              </span>
            ))}
            <input
              ref={inputRef}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={onKeyDown}
              onPaste={onPaste}
              aria-label={t("workspace:tagsInputLabel")}
              placeholder={t("workspace:tagsInputPlaceholder")}
              className="min-w-24 flex-1 bg-transparent text-[12.5px] outline-none"
              style={{ height: 26, color: "var(--hub-text)" }}
            />
          </div>
          <p className="mt-1.5 text-[11.5px]" style={{ color: "var(--hub-muted)" }}>
            {t("workspace:tagsInputHint")}
          </p>
        </div>

        {/* c. Divider. */}
        <div style={{ height: 1, background: "var(--hub-divider)" }} />

        {/* d. What the machine thought, offered rather than applied. */}
        <div>
          <div className="flex items-center">
            <span
              className="text-[11px] font-bold uppercase"
              style={{ letterSpacing: ".08em", color: "var(--hub-muted)" }}
            >
              {t("workspace:tagsSuggestedLabel")}
            </span>
            {suggested.length > 0 && (
              <span className="ml-auto text-[11px]" style={{ color: "var(--hub-muted-2)" }}>
                {t("workspace:tagsSuggestedLeft", { count: suggested.length })}
              </span>
            )}
          </div>

          {suggested.length === 0 ? (
            <p className="mt-2 text-[12px]" style={{ color: "var(--hub-muted-2)" }}>
              {t("workspace:tagsSuggestedDone")}
            </p>
          ) : (
            <div className="mt-2 flex flex-wrap gap-1.5">
              {suggested.map((tag) => (
                <span
                  key={tag}
                  className="inline-flex items-center"
                  style={{
                    height: 26,
                    borderRadius: 7,
                    border: "1px dashed var(--hub-hint-border)",
                  }}
                >
                  <button
                    type="button"
                    onClick={() => onAdd(tag)}
                    title={t("workspace:tagsSuggestedAdd")}
                    className="inline-flex h-full items-center gap-1 rounded-l-[7px] px-2 hover:bg-[var(--hub-surface-hover)]"
                  >
                    <span className="text-[11px]" style={{ color: "var(--hub-blue)" }}>
                      +
                    </span>
                    <span className="text-[12.5px]" style={{ color: "var(--hub-text-2)" }}>
                      {tag}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => onDismiss(tag)}
                    title={t("workspace:tagsSuggestedDismiss")}
                    aria-label={t("workspace:tagsSuggestedDismiss")}
                    className="mr-1 grid place-items-center rounded hover:bg-[var(--hub-surface-hover)]"
                    style={{ width: 18, height: 18, fontSize: 10, color: "var(--hub-muted-2)" }}
                  >
                    ✕
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </HubPopover>
  );
}
```

Three of those `var(--hub-*)` tokens do not exist yet - the handoff gives literal values per theme, and the
existing token layer has no name for them. Add them to both `:root` and `.dark` in
`apps/web/src/index.css`, beside the other `--hub-*` entries, rather than hard-coding two colours per
element:

```css
  --hub-field-border: rgba(15, 23, 42, .14);
  --hub-divider: rgba(15, 23, 42, .08);
  --hub-hint-border: rgba(15, 23, 42, .2);
```

and in `.dark`:

```css
  --hub-field-border: rgba(255, 255, 255, .14);
  --hub-divider: rgba(255, 255, 255, .08);
  --hub-hint-border: rgba(255, 255, 255, .2);
```

Check `index.css` first: if equivalents already exist under different names, use those and add nothing.
Every other colour above comes from the existing token layer or is a literal the handoff fixes for both
themes (the blue chip fill and borders are deliberately theme-independent).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/detail/TagsPopover.test.tsx`
Expected: PASS (13 tests).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/detail/TagsPopover.tsx apps/web/src/components/detail/TagsPopover.test.tsx
git commit -m "feat(tags): add the hub Tags popover"
```

---

### Task 12: Wire the pill into the hero card

**Files:**
- Create: `apps/web/src/components/detail/RecordingTags.tsx`
- Modify: `apps/web/src/components/detail/HeroSummaryCard.tsx:82-93` (row 1)
- Test: `apps/web/src/components/detail/RecordingTags.test.tsx`

**Interfaces:**
- Consumes: `TagsPill` (Task 10), `TagsPopover` (Task 11), `api.addRecordingTag` / `removeRecordingTag` / `dismissRecordingTag` (Task 9), `RecordingDetail.tags` / `.suggestedTags` (Task 9).
- Produces: `RecordingTags({ recordingId, tags, suggested })` - the stateful container that owns `open`, the mutations and the cache invalidation. `HeroSummaryCard` renders exactly this.

`RecordingTags` owns its own mutations rather than taking three more callbacks from `HeroSummaryCard` and
`RecordingDetail`. Both of those already carry long prop lists, and nothing outside this control needs to
know about tagging, so the round-trip belongs here. It invalidates the `["tags"]` prefix (not
`["tags", roomId]`), which covers every room variant of the cloud - the same thing
`RecordingsPanel.tsx:60-67` already does on a hub status event.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/detail/RecordingTags.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import RecordingTags from "./RecordingTags";

vi.mock("../../lib/api", () => ({
  api: {
    addRecordingTag: vi.fn().mockResolvedValue(undefined),
    removeRecordingTag: vi.fn().mockResolvedValue(undefined),
    dismissRecordingTag: vi.fn().mockResolvedValue(undefined),
  },
}));

import { api } from "../../lib/api";

function renderTags(props: Partial<ComponentProps<typeof RecordingTags>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <RecordingTags recordingId="rec-1" tags={["metadata"]} suggested={["templates"]} {...props} />
    </QueryClientProvider>,
  );
  return qc;
}

describe("RecordingTags", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows the adopted tag count on the pill and opens the popover on click", async () => {
    renderTags({ tags: ["metadata", "licensing"] });
    const pill = screen.getByRole("button", { name: "Tags" });
    expect(pill).toHaveTextContent("2");
    expect(screen.queryByLabelText("Add a tag")).not.toBeInTheDocument();

    await userEvent.click(pill);

    expect(screen.getByLabelText("Add a tag")).toBeInTheDocument();
  });

  it("sends a typed tag to the API", async () => {
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "licensing"));
  });

  it("shows a typed tag immediately, before the server answers", async () => {
    renderTags({ tags: [] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    expect(screen.getByRole("button", { name: "Tags" })).toHaveTextContent("1");
  });

  it("promotes a suggestion, moving it out of the hint list", async () => {
    renderTags({ tags: [], suggested: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: /templates/ }));

    await waitFor(() => expect(api.addRecordingTag).toHaveBeenCalledWith("rec-1", "templates"));
    expect(screen.getByText("All suggestions dealt with.")).toBeInTheDocument();
  });

  it("removes an adopted tag", async () => {
    renderTags({ tags: ["metadata"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: "Remove tag" }));

    await waitFor(() => expect(api.removeRecordingTag).toHaveBeenCalledWith("rec-1", "metadata"));
  });

  it("dismisses a suggestion", async () => {
    renderTags({ suggested: ["templates"] });
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.click(screen.getByRole("button", { name: "Never suggest this" }));

    await waitFor(() => expect(api.dismissRecordingTag).toHaveBeenCalledWith("rec-1", "templates"));
    expect(api.removeRecordingTag).not.toHaveBeenCalled();
  });

  it("invalidates the tag cloud after a change, so the Tags tab keeps up", async () => {
    const qc = renderTags({ tags: [] });
    const invalidate = vi.spyOn(qc, "invalidateQueries");
    await userEvent.click(screen.getByRole("button", { name: "Tags" }));

    await userEvent.type(screen.getByLabelText("Add a tag"), "licensing ");

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["tags"] })),
    );
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/components/detail/RecordingTags.test.tsx`
Expected: FAIL - cannot resolve `./RecordingTags`.

- [ ] **Step 3: Implement the container**

Create `apps/web/src/components/detail/RecordingTags.tsx`:

```tsx
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { api } from "../../lib/api";
import TagsPill from "./TagsPill";
import TagsPopover from "./TagsPopover";

/// The hero card's tagging control: the pill, its popover, and the server round-trip behind them. Owns the
/// mutations itself rather than taking callbacks down through HeroSummaryCard and RecordingDetail - nothing
/// outside this control needs to know about tagging, and both of those already carry long prop lists.
///
/// Every action persists on its own (no Save button). Local `pending` state applies the change immediately
/// so the pill count and the chips react to a click without waiting for the refetch; the authoritative
/// lists arrive back as props when the recording detail query settles.
export default function RecordingTags({
  recordingId,
  tags,
  suggested,
}: {
  recordingId: string;
  tags: string[];
  suggested: string[];
}) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  /// Optimistic overlay, cleared whenever fresh props arrive.
  const [pending, setPending] = useState<{ tags: string[]; suggested: string[] } | null>(null);

  const shownTags = pending?.tags ?? tags;
  const shownSuggested = pending?.suggested ?? suggested;

  /// Both queries: the recording detail feeds this control's props, and the ["tags"] prefix covers every
  /// room variant of the tag cloud the Tags tab renders.
  async function refresh() {
    setPending(null);
    await Promise.all([
      qc.invalidateQueries({ queryKey: ["recording", recordingId] }),
      qc.invalidateQueries({ queryKey: ["tags"] }),
    ]);
  }

  const add = useMutation({
    mutationFn: (tag: string) => api.addRecordingTag(recordingId, tag),
    onSettled: refresh,
  });
  const remove = useMutation({
    mutationFn: (tag: string) => api.removeRecordingTag(recordingId, tag),
    onSettled: refresh,
  });
  const dismiss = useMutation({
    mutationFn: (tag: string) => api.dismissRecordingTag(recordingId, tag),
    onSettled: refresh,
  });

  const lower = (t: string) => t.toLowerCase();

  function onAdd(tag: string) {
    // A promoted suggestion leaves the hint list in the same beat it becomes a chip.
    setPending({
      tags: [...shownTags, tag],
      suggested: shownSuggested.filter((s) => lower(s) !== lower(tag)),
    });
    add.mutate(tag);
  }

  function onRemove(tag: string) {
    setPending({
      tags: shownTags.filter((t) => lower(t) !== lower(tag)),
      suggested: shownSuggested,
    });
    remove.mutate(tag);
  }

  function onDismiss(tag: string) {
    setPending({
      tags: shownTags,
      suggested: shownSuggested.filter((s) => lower(s) !== lower(tag)),
    });
    dismiss.mutate(tag);
  }

  return (
    <div className="relative">
      <TagsPill
        count={shownTags.length}
        tags={shownTags}
        open={open}
        onToggle={() => setOpen((o) => !o)}
      />
      <TagsPopover
        open={open}
        onClose={() => setOpen(false)}
        tags={shownTags}
        suggested={shownSuggested}
        onAdd={onAdd}
        onRemove={onRemove}
        onDismiss={onDismiss}
      />
    </div>
  );
}
```

Check how `apps/web/src/lib/api.ts` is exported (default object vs named `api`) and match it, in both the
component and the test's `vi.mock`.

- [ ] **Step 4: Render it in the hero card**

In `apps/web/src/components/detail/HeroSummaryCard.tsx`, import it and place it after `MeetingTypeMenu`
(which ends at line 93) and before the `ml-auto` cluster (line 95):

```tsx
        <RecordingTags
          recordingId={rec.id}
          tags={rec.tags ?? []}
          suggested={rec.suggestedTags ?? []}
        />
```

The pill deliberately sits between the meeting-type chip and the `ml-auto` group so the two chips read as
a pair, per the handoff. No prop changes to `HeroSummaryCard` - it already receives `rec`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/detail/RecordingTags.test.tsx`
Expected: PASS (7 tests).

- [ ] **Step 6: Run the whole web suite and build**

Run: `cd apps/web && npm test` then `npm run build`
Expected: all green. If a `HeroSummaryCard` or `RecordingDetail` test now fails because the pill renders
inside it, fix it by supplying the new fields - do not weaken an assertion. Watch for tests that guarded an
api method by leaving it out of their `vi.mock` factory: adding a child that calls one destroys that guard,
so convert such a test to a call assertion rather than patching the mock.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/detail/RecordingTags.tsx apps/web/src/components/detail/RecordingTags.test.tsx apps/web/src/components/detail/HeroSummaryCard.tsx
git commit -m "feat(tags): wire the Tags pill into the hero summary card"
```

---

### Task 13: Verify it in the running app

**Files:** none (verification only).

**Interfaces:**
- Consumes: everything.
- Produces: evidence.

jsdom computes no geometry, so nothing so far proves the pill fits row 1 beside a long meeting-type name,
or that the 392px popover does not overflow the hub column. This repo has been bitten by exactly that.

- [ ] **Step 1: Bring the stack up**

Run the local Docker stack (`cd deploy && docker compose up --build`), or the API plus `npm run dev` in
`apps/web`. If the API changed and Redis is unpublished locally, the api container needs rebuilding rather
than restarting.

- [ ] **Step 2: Open a recording that has suggestions**

Any recording tagged before this change now shows its old tags as suggestions - that is the demotion
working, and it is the best possible fixture. Confirm the pill reads `Tags 0` and the popover's hint list is
full.

- [ ] **Step 3: Walk the interactions**

Type two tags separated by a space; confirm both become chips and the pill count rises. Press Enter and
confirm it closes. Re-open and confirm the chips survived the refetch (this proves the server round-trip,
not just the optimistic overlay). Promote a hint, dismiss another, then reload the page and confirm the
promoted tag is a chip and the dismissed one is gone from the hints. Remove a chip and confirm it does not
reappear as a hint.

- [ ] **Step 4: Check the Tags tab**

Open the left panel's Tags tab and confirm the cloud now shows only the tags just adopted, and that
clicking one drills into the right recording.

- [ ] **Step 5: Check both themes and the layout**

Screenshot the hub with the popover open in light and dark, and compare against
`docs/design_handoff_manual_tagging/screenshots/`. Confirm the pill does not wrap awkwardly on a recording
with a long meeting-type name, and that the popover stays inside the column.

- [ ] **Step 6: Confirm a re-transcription spares the tags**

Re-transcribe a recording that has adopted tags. Confirm the chips are still there afterwards and the hint
list has refreshed. This is the behaviour with the worst failure mode (silently losing hand-applied tags),
and it is the only step that exercises the worker end to end.

---

### Task 14: Release, docs and the n8n node

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts` (new `RELEASES[0]`, plus the `CAPABILITIES` tags row)
- Modify: `README.md` (Features table row), `docs/features.md` (tags bullet)
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/en/search-and-tags.md`, `apps/web/src/content/help/en/automations-and-signals.md`
- Modify: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` + `generated/index.ts` (regenerated)

**Interfaces:**
- Consumes: everything.
- Produces: a releasable PR.

- [ ] **Step 1: Bump the version everywhere**

Set `0.212.0` in `version.json` and all four mirrors. `apps/web/src/lib/versionMirrors.test.ts` fails the
build if any of them drifts - the n8n mirror especially, since a published npm version cannot be corrected.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version: "0.212.0"`, today's date, the PR
number, a headline, a prose `summary` explaining that tags are now the user's to add and automatic topics
are only suggestions, and `added`/`changed` bullets. `releases.test.ts` asserts `RELEASES[0].version`
equals `version.json`.

The `pr` number is needed before `gh pr create` exists to supply it, and guessing "last + 1" fails because
Dependabot and issues share the sequence. Check the real next number with
`gh pr list --state all --limit 1 --json number`, and no test will catch it if it is wrong.

- [ ] **Step 3: Update the tags row in the three inventories**

The tags capability changes meaning, so update all three in lockstep: the `CAPABILITIES` table row in
`releases.ts`, the README Features table row, and the `docs/features.md` prose bullet. Each should now say
tags are added by hand with automatic suggestions offered per recording.

- [ ] **Step 4: Update the reference docs**

`docs/Data_Schema.md`: the `RecordingTags` section gains the `Status` and `AdoptedAt` columns, the
`RecordingTagStatus` enum values, the `IX_RecordingTags_RecordingId_TagLower` unique index, and a migration
history row for `AddRecordingTagStatus`.

`docs/Overall_Synopsis_of_Platform.md`: the three new endpoints, the suggestion lifecycle, the fact that
the cloud counts adopted tags only, that a re-transcription now spares adopted tags, and that these are the
first writes gated by read access rather than ownership.

- [ ] **Step 5: Update the help articles**

`search-and-tags.md`: the behaviour a user relies on has changed - tags are theirs to add from the hub's
Tags pill, and the automatic ones are suggestions to pick or ignore. Keep it ASCII only, keep the
`title`/`summary`/`group`/`order` front matter, and keep the `summary` to two or three sentences (the
contextual `?` popover shows it). `automations-and-signals.md`: the `recording.tags_ready` row now describes
suggested tags.

- [ ] **Step 6: Regenerate the n8n node**

Run:

```bash
cd integrations/n8n-nodes-diariz && npm run generate && npm test
```

Expected: `openapi.snapshot.json` and `generated/index.ts` pick up the three new operations and the two new
`RecordingDetailDto` fields. Commit the regenerated files.

- [ ] **Step 7: Run everything**

Run, and expect all green:

```bash
dotnet build Diariz.slnx
dotnet test
cd apps/web && npm test && npm run build
```

The OpenAPI snapshot test rewrites its own snapshot, so a first-run failure there is expected - commit the
regenerated file and re-run.

- [ ] **Step 8: Commit and open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help/en/search-and-tags.md apps/web/src/content/help/en/automations-and-signals.md integrations/n8n-nodes-diariz/nodes/Diariz/generated
git commit -m "chore(release): 0.212.0 - manual tagging with automatic suggestions"
git push -u origin feat/manual-tagging
gh pr create --title "Manual tagging, with auto tags as suggestions only" --body "..."
```

The PR body must state the **deployment surface**: web + API only, so this needs a **server redeploy and no
desktop release** (nothing under `apps/desktop/src/**` or the builder config is touched; the desktop
version bump is lockstep only).
