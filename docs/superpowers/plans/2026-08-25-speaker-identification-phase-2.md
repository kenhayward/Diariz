# Speaker Identification Quality, Phase 2 - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover the identification matches that already qualify but were never applied, and turn the borderline ones into one-click decisions that accumulate as calibration evidence.

**Architecture:** The single compiled `Identification:Threshold` becomes four `PlatformSettings` columns. A pure `IdentificationRules.Decide` turns ranked candidates plus a speech duration into accept / suggest / ignore. `ISpeakerIdentifier` starts returning a *ranked* list so a margin can be measured between people. A re-scan runs the same rules over every stored speaker embedding, with a dry run first. Suggestions land on `Speakers`, are reviewed from a queue and inline in the transcript, and every accept or reject is written to a new `SpeakerIdentityDecisions` table - the labelled ground truth Phase 4's bench calibrates from.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Npgsql/pgvector, React 19 + TypeScript + Vite + Tailwind v4, vitest, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-25-speaker-identification-quality-design.md` (sections 3.2, 3.3, 3.4, 4.3, 4.4, 4.6, 7.3, 10 Phase 2).

**Measured starting point** (live instance, 2026-08-25): 1,125 speakers with embeddings, 532 linked. **128 speakers sit inside the current 0.4 threshold, are still anonymous, and are unlinked** - the matches this phase recovers. A further ~74 sit between 0.4 and 0.5.

## Global Constraints

- **TDD is mandatory.** Failing test first, watch it fail, minimal code to pass.
- **Mutation-verify every new assertion.** Break the production code, confirm red, restore **by editing in place** - restoring from a backup preserves the mtime and MSBuild skips the rebuild.
- **No em or en dashes in user-facing text.** Plain hyphen only.
- **All four locale catalogs** (`en`/`de`/`fr`/`es`) stay at exact key parity with no empty values; `src/locales.test.ts` enforces both. Translations use normal accented characters - the ASCII rule is for `content/help/**`, not the catalogs.
- **Never `git add -A`**, and **`git add -u` will not stage a new file** - stage new test files by explicit path or they ship untested (this happened in Phase 1).
- **`--filter "Name=X"` does not work here.** Use `FullyQualifiedName~X`.
- **Build `Diariz.slnx`**, not just the unit project, before pushing.
- **The OpenAPI snapshot self-heals** (run 1 fails and rewrites, run 2 passes); **`integrations/n8n-nodes-diariz/nodes/Diariz/generated/index.ts` does not** - run `npm run generate` there.
- `Person` maps to `SpeakerProfiles`, `VoiceSample` to `ProfileContributions`, `Speaker.PersonId` to the `ProfileId` column. **Do not rename them** - a table rename rejects every existing backup.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/IdentificationRules.cs` | Pure accept/suggest/ignore arithmetic |
| `src/Diariz.Api/Services/SpeakerAssignment.cs` | The one place a speaker becomes a person (extracted from `RecordingsController`) |
| `src/Diariz.Api/Services/IdentificationRescan.cs` | Re-scan, dry run and apply |
| `src/Diariz.Api/Controllers/SpeakerSuggestionsController.cs` | The queue, accept and reject |
| `src/Diariz.Domain/Entities/SpeakerIdentityDecision.cs` | The labelled-decision log |
| `apps/web/src/pages/SpeakerSuggestions.tsx` | The review queue |
| `apps/web/src/components/detail/SpeakerSuggestionPrompt.tsx` | The inline prompt |
| Matching test files for each | |

**Modified**

| File | Change |
|---|---|
| `src/Diariz.Domain/Entities/PlatformSettings.cs` | Four identification columns |
| `src/Diariz.Domain/Entities/Speaker.cs` | `SuggestedProfileId`, `SuggestedDistance`, `SuggestedAt` |
| `src/Diariz.Api/Services/SpeakerIdentifier.cs` | `RankAsync` replaces `IdentifyAsync` |
| `src/Diariz.Api/Services/SpeakerLabeling.cs` | Applies the rules; writes suggestions; honours rejections |
| `src/Diariz.Api/Controllers/RecordingsController.cs` | Assignment logic moves to `SpeakerAssignment` |
| `src/Diariz.Api/Controllers/PlatformSettingsController.cs` | The four knobs |
| `apps/web/src/components/SettingsModal.tsx` | Identification section + re-scan |
| `apps/web/src/pages/RecordingDetail.tsx` | Inline prompt |

---

## Task 1: The four knobs on `PlatformSettings`

**Files:**
- Modify: `src/Diariz.Domain/Entities/PlatformSettings.cs`
- Create: migration `AddIdentificationSettings`
- Test: `tests/Diariz.Api.Tests/IdentificationSettingsTests.cs`

**Interfaces:**
- Produces: `PlatformSettings.IdentificationThreshold` (double, default 0.40), `IdentificationConfirmBand` (double, 0.50), `IdentificationMargin` (double, 0.05), `IdentificationMinSpeechMs` (int, 3000), each with a `Default*` const beside the existing ones.

**Why 0.50 and not 0.55:** measured. At 0.55 the day-one queue is roughly 150 items; at 0.50 it is about 74. Ship the smaller backlog and let the evidence argue for widening.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The identification knobs' defaults. They are asserted rather than left implicit because a
/// deployment that never touches them runs on exactly these numbers, and the 0.40 threshold is the one the
/// live distance distribution was measured against.</summary>
public class IdentificationSettingsTests
{
    [Fact]
    public void Defaults_match_the_measured_operating_point()
    {
        var s = new PlatformSettings();

        Assert.Equal(0.40, s.IdentificationThreshold, 3);
        Assert.Equal(0.50, s.IdentificationConfirmBand, 3);
        Assert.Equal(0.05, s.IdentificationMargin, 3);
        Assert.Equal(3000, s.IdentificationMinSpeechMs);
    }

    [Fact]
    public void The_confirm_band_is_looser_than_the_threshold()
    {
        // Inverted, every match would be a suggestion and nothing would ever auto-apply. The two are
        // independent columns, so nothing but this stops an administrator inverting them either.
        var s = new PlatformSettings();
        Assert.True(s.IdentificationConfirmBand > s.IdentificationThreshold);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~IdentificationSettings"
```

Expected: compile error, no such properties.

- [ ] **Step 3: Add the columns**

In `src/Diariz.Domain/Entities/PlatformSettings.cs`, beside the existing `Default*` consts:

```csharp
    /// <summary>Max cosine distance (0..2) at which a voice match is applied automatically. Measured against
    /// the live distance distribution: true matches cluster around 0.2-0.3 and impostors around 0.55-0.75,
    /// so the valley sits near 0.45 and this is the conservative side of it.</summary>
    public const double DefaultIdentificationThreshold = 0.40;

    /// <summary>Max distance at which a match is *suggested* rather than applied. Between this and
    /// <see cref="DefaultIdentificationThreshold"/> the user is asked. 0.50 rather than 0.55 deliberately:
    /// at 0.55 the day-one review queue is roughly 150 items and at 0.50 about 74, and a backlog nobody
    /// works through produces worse evidence than a smaller one that gets read.</summary>
    public const double DefaultIdentificationConfirmBand = 0.50;

    /// <summary>How far the best-matching person must beat the next *person* before either is acted on.
    /// Guards confusable voices, where the nearest is close to a coin-flip.</summary>
    public const double DefaultIdentificationMargin = 0.05;

    /// <summary>Below this much speech, a speaker is not matched at all. Accuracy climbs steeply up to
    /// 10-20s and sub-2s utterances are unreliable, so scoring them lends false confidence to noise.</summary>
    public const int DefaultIdentificationMinSpeechMs = 3000;
```

and the four properties:

```csharp
    public double IdentificationThreshold { get; set; } = DefaultIdentificationThreshold;
    public double IdentificationConfirmBand { get; set; } = DefaultIdentificationConfirmBand;
    public double IdentificationMargin { get; set; } = DefaultIdentificationMargin;
    public int IdentificationMinSpeechMs { get; set; } = DefaultIdentificationMinSpeechMs;
```

- [ ] **Step 4: Create the migration**

```bash
dotnet ef migrations add AddIdentificationSettings --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Read the generated file. It must be four additive not-null columns **with defaults**, and nothing else. A not-null column with no default would fail on the existing singleton row.

- [ ] **Step 5: Run the test and watch it pass**

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Domain tests/Diariz.Api.Tests/IdentificationSettingsTests.cs
git commit -m "feat: identification thresholds as platform settings"
```

---

## Task 2: The pure decision rules

**Files:**
- Create: `src/Diariz.Api/Services/IdentificationRules.cs`
- Create: `tests/Diariz.Api.Tests/IdentificationRulesTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum IdentificationOutcome { Ignore, Suggest, Accept }

  public record IdentificationThresholds(double Accept, double Suggest, double Margin, int MinSpeechMs)
  {
      public static IdentificationThresholds From(PlatformSettings s);
  }

  /// One person's best distance. Ranked ascending, one entry per person.
  public record RankedCandidate(Guid PersonId, string Name, double Distance);

  public record IdentificationVerdict(IdentificationOutcome Outcome, RankedCandidate? Match);

  public static class IdentificationRules
  {
      public static IdentificationVerdict Decide(
          IReadOnlyList<RankedCandidate> ranked, long speechMs, IdentificationThresholds t);
  }
  ```

**The rule that is easiest to get wrong:** the margin is measured **between different people**, never between two candidates of the same person. Phase 3 gives a person several templates and their distances will sit close together by design; rejecting on that would break exactly the case being built for. `ranked` therefore carries **one entry per person** - collapsing templates to their owner's best is the caller's job, and this contract says so.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Accept, suggest or ignore - the whole of identification's policy, with no database in sight.</summary>
public class IdentificationRulesTests
{
    private static readonly IdentificationThresholds T = new(Accept: 0.40, Suggest: 0.50, Margin: 0.05, MinSpeechMs: 3000);

    private static RankedCandidate C(double d, string name = "Alice") => new(Guid.NewGuid(), name, d);

    [Fact]
    public void Inside_the_threshold_is_accepted()
    {
        var v = IdentificationRules.Decide([C(0.30), C(0.80)], 30_000, T);
        Assert.Equal(IdentificationOutcome.Accept, v.Outcome);
        Assert.Equal(0.30, v.Match!.Distance, 3);
    }

    [Fact]
    public void Between_the_two_thresholds_is_suggested()
    {
        var v = IdentificationRules.Decide([C(0.45), C(0.80)], 30_000, T);
        Assert.Equal(IdentificationOutcome.Suggest, v.Outcome);
    }

    [Fact]
    public void Beyond_the_band_is_ignored()
    {
        Assert.Equal(
            IdentificationOutcome.Ignore,
            IdentificationRules.Decide([C(0.65), C(0.80)], 30_000, T).Outcome);
    }

    [Fact]
    public void Exactly_on_the_threshold_is_accepted()
    {
        // Inclusive, matching how the existing identifier compared. An off-by-one here silently moves the
        // operating point away from the one the distance distribution was measured against.
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.40), C(0.90)], 30_000, T).Outcome);
    }

    [Fact]
    public void A_runner_up_too_close_is_refused()
    {
        // Two similar voices where the nearest is close to a coin-flip. Applying either would be a guess
        // wearing a name.
        var v = IdentificationRules.Decide([C(0.30, "Alice"), C(0.33, "Bob")], 30_000, T);
        Assert.Equal(IdentificationOutcome.Ignore, v.Outcome);
    }

    [Fact]
    public void The_margin_is_measured_against_the_next_person_not_the_next_template()
    {
        // The load-bearing one. Ranked carries one entry per person precisely so that two templates of the
        // same human - their office voice and their car voice, which Phase 3 introduces - cannot look like
        // a confusable pair and veto a correct match.
        var alice = Guid.NewGuid();
        var v = IdentificationRules.Decide(
            [new RankedCandidate(alice, "Alice", 0.30), new RankedCandidate(Guid.NewGuid(), "Bob", 0.90)],
            30_000, T);

        Assert.Equal(IdentificationOutcome.Accept, v.Outcome);
        Assert.Equal(alice, v.Match!.PersonId);
    }

    [Fact]
    public void A_lone_candidate_has_no_runner_up_to_beat()
    {
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.30)], 30_000, T).Outcome);
    }

    [Fact]
    public void Too_little_speech_is_ignored_however_close_the_match()
    {
        // A 1.5s utterance produces an embedding the model has no business being confident about. Scoring it
        // anyway is how a voiceprint learns from noise.
        Assert.Equal(
            IdentificationOutcome.Ignore,
            IdentificationRules.Decide([C(0.05)], 1_500, T).Outcome);
    }

    [Fact]
    public void An_empty_gallery_is_ignored()
    {
        Assert.Equal(IdentificationOutcome.Ignore, IdentificationRules.Decide([], 30_000, T).Outcome);
    }

    [Fact]
    public void The_margin_applies_to_a_suggestion_too()
    {
        // A borderline match that is also ambiguous is worse than either alone - it would put a coin-flip in
        // front of someone as though it were a considered guess.
        Assert.Equal(
            IdentificationOutcome.Ignore,
            IdentificationRules.Decide([C(0.45, "Alice"), C(0.47, "Bob")], 30_000, T).Outcome);
    }

    [Fact]
    public void From_reads_the_platform_settings()
    {
        var t = IdentificationThresholds.From(new PlatformSettings());
        Assert.Equal(0.40, t.Accept, 3);
        Assert.Equal(0.50, t.Suggest, 3);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

- [ ] **Step 3: Implement**

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

public enum IdentificationOutcome { Ignore, Suggest, Accept }

/// <summary>One person's best distance to the probe. A list of these is ranked ascending with <b>one entry
/// per person</b>.</summary>
public record RankedCandidate(Guid PersonId, string Name, double Distance);

public record IdentificationThresholds(double Accept, double Suggest, double Margin, int MinSpeechMs)
{
    public static IdentificationThresholds From(PlatformSettings s) =>
        new(s.IdentificationThreshold, s.IdentificationConfirmBand, s.IdentificationMargin,
            s.IdentificationMinSpeechMs);
}

public record IdentificationVerdict(IdentificationOutcome Outcome, RankedCandidate? Match);

/// <summary>Whether a voice match is applied, offered, or dropped. Pure, so the policy can be reasoned about
/// and swept over without a database.</summary>
public static class IdentificationRules
{
    private static readonly IdentificationVerdict Nothing = new(IdentificationOutcome.Ignore, null);

    public static IdentificationVerdict Decide(
        IReadOnlyList<RankedCandidate> ranked, long speechMs, IdentificationThresholds t)
    {
        if (ranked.Count == 0) return Nothing;
        // Too short to be worth scoring. Accuracy climbs steeply up to 10-20s of speech, so a confident
        // number derived from a second and a half is confidence in noise.
        if (speechMs < t.MinSpeechMs) return Nothing;

        var best = ranked[0];
        if (best.Distance > t.Suggest) return Nothing;

        // The runner-up is the next PERSON. Callers collapse a person's templates to their best before
        // ranking, so two templates of one human can never look like a confusable pair.
        if (ranked.Count > 1 && ranked[1].Distance - best.Distance < t.Margin) return Nothing;

        return new IdentificationVerdict(
            best.Distance <= t.Accept ? IdentificationOutcome.Accept : IdentificationOutcome.Suggest,
            best);
    }
}
```

- [ ] **Step 4: Run and watch them pass**

- [ ] **Step 5: Mutation-verify three**

- Change `speechMs < t.MinSpeechMs` to `false` -> `Too_little_speech_is_ignored_however_close_the_match` must fail.
- Change `best.Distance <= t.Accept` to `<` -> `Exactly_on_the_threshold_is_accepted` must fail.
- Remove the margin line -> `A_runner_up_too_close_is_refused` **and** `The_margin_applies_to_a_suggestion_too` must fail.

Restore in place after each.

- [ ] **Step 6: Commit**

---

## Task 3: `ISpeakerIdentifier` returns a ranking

**Files:**
- Modify: `src/Diariz.Api/Services/SpeakerIdentifier.cs`
- Modify: `src/Diariz.Api/Services/SpeakerLabeling.cs` (call site)
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs` (the fake)
- Modify: `tests/Diariz.Api.IntegrationTests/SpeakerIdentificationIntegrationTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<RankedCandidate>> RankAsync(Vector embedding, int take = 2, CancellationToken ct = default)`, replacing `IdentifyAsync`. Ordered ascending by distance, one entry per person, **unfiltered by any threshold** - policy now lives in `IdentificationRules`.

**Why replace rather than add:** two methods would mean two places that decide what "a match" is, and the older one already embeds a threshold. Seven files reference the interface; a compile error at each is the point.

- [ ] **Step 1: Write the failing test**

Extend `SpeakerIdentificationIntegrationTests` (real pgvector - the in-memory provider `Ignore`s the vector column, so ranking cannot be proven there):

```csharp
    [Fact]
    public async Task RankAsync_orders_people_by_distance_and_applies_no_threshold()
    {
        // Deliberately includes a candidate far beyond any acceptance distance: the ranking is evidence,
        // and deciding what to do with it belongs to IdentificationRules.
    }

    [Fact]
    public async Task RankAsync_excludes_opted_out_people_and_those_with_no_voiceprint()
    {
        // A CosineDistance over a NULL column does nothing useful, and an opted-out person must never be
        // matched at all.
    }

    [Fact]
    public async Task RankAsync_take_limits_the_result()
    {
    }
```

- [ ] **Step 2: Run and watch it fail**

- [ ] **Step 3: Implement**

```csharp
    public async Task<IReadOnlyList<RankedCandidate>> RankAsync(
        Vector embedding, int take = 2, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return [];

        // A person's voiceprint is optional, so much of the directory has no embedding to compare against:
        // someone added by hand, or one who opted out and had theirs erased. Both must be excluded before the
        // distance projection - CosineDistance over a NULL column does not do anything useful.
        //
        // Sequential scan, and that is fine: roughly 0.35 ms per 1,000 people, once per speaker. Revisit an
        // HNSW index past ~25,000 people - it is approximate, and a miss here is a speaker silently going
        // unidentified.
        return await _db.People
            .Where(p => p.Embedding != null && !p.VoiceprintOptOut)
            .Select(p => new RankedCandidate(p.Id, p.Name, p.Embedding!.CosineDistance(embedding)))
            .OrderBy(x => x.Distance)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);
    }
```

Delete `IdentifyAsync` and the now-unused `SpeakerMatch` record, and fix every call site the compiler names.

**`IdentificationOptions.Threshold` becomes unused** - delete the property and its `appsettings` entry, so nothing reads a stale second copy of a number that now lives in the database. Keep `IdentificationOptions.Enabled`.

- [ ] **Step 4: Run and watch it pass**

- [ ] **Step 5: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

- [ ] **Step 6: Commit**

---

## Task 4: The decision log

**Files:**
- Create: `src/Diariz.Domain/Entities/SpeakerIdentityDecision.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Create: migration `AddSpeakerIdentityDecisions`
- Create: `tests/Diariz.Api.IntegrationTests/SpeakerIdentityDecisionSchemaTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum IdentityDecisionKind { Rejected = 0, Accepted = 1 } // append only, never renumber

  public class SpeakerIdentityDecision
  {
      public Guid Id { get; set; }
      public Guid SpeakerId { get; set; }
      public Guid ProfileId { get; set; }   // CLR PersonId
      public IdentityDecisionKind Decision { get; set; }
      public double Distance { get; set; }
      public DateTimeOffset DecidedAt { get; set; }
      public Guid? DecidedByUserId { get; set; }
  }
  ```

**Why this table is the point of the whole phase.** A rejected suggestion at 0.47 is a **labelled hard negative**, and there is no other source of them: the 176 manual links are all positives. Phase 4's threshold sweep is only as good as this table, so it records the distance **as it was at the moment of the decision**, not one recomputed later against a gallery that has moved.

Index `(SpeakerId, ProfileId)` - the rejected-pair guard reads it on every re-scan.

- [ ] **Step 1: Write the failing test** (round-trip, cascade from `Speakers`, and the index exists)
- [ ] **Step 2: Run and watch it fail**
- [ ] **Step 3: Add the entity, the `DbSet`, and `OnModelCreating` config**
- [ ] **Step 4: Create the migration and read it** - additive table only
- [ ] **Step 5: Run and watch it pass**
- [ ] **Step 6: Commit**

---

## Task 5: Suggestions on `Speakers`

**Files:**
- Modify: `src/Diariz.Domain/Entities/Speaker.cs`
- Create: migration `AddSpeakerSuggestion`

**Interfaces:**
- Produces: `Speaker.SuggestedProfileId` (`Guid?`), `SuggestedDistance` (`double?`), `SuggestedAt` (`DateTimeOffset?`).

Three nullable columns, all null together or all set together. FK on `SuggestedProfileId` -> `SpeakerProfiles` with **`ON DELETE SET NULL`**: deleting a person must not delete the speaker row, only withdraw the suggestion.

- [ ] Steps as Task 4: failing schema test, entity, migration, green, commit.

---

## Task 6: Extract `SpeakerAssignment`

**Files:**
- Create: `src/Diariz.Api/Services/SpeakerAssignment.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:577-615`

**Interfaces:**
- Produces: `Task<AssignResult> AssignAsync(Speaker speaker, Person person, Guid actorId, CancellationToken ct)` - sets `PersonId`/`DisplayName`, clears `IdentifiedAuto` and `IsMultiSpeaker`, enrols a `VoiceSample` when the speaker has an embedding and the person has not opted out, and recomputes the centroid.

**Why extract before writing accept:** accepting a suggestion *is* assigning a speaker to a person. Writing it a second time would duplicate the opt-out guard, the embedding guard and the centroid recompute - three rules that must not drift. This task is a pure refactor: **no behaviour changes, and the existing `RecordingsController` speaker tests must pass untouched.**

- [ ] **Step 1: Run the existing speaker-assignment tests and record the count** - this is the safety net.
- [ ] **Step 2: Extract the service, calling it from the controller**
- [ ] **Step 3: Run the same tests - identical count, still green**
- [ ] **Step 4: Mutation-verify the net is real** - break the opt-out guard inside the new service and confirm an existing test reds. If none does, that rule was never covered and needs a test **before** the extraction is trusted.
- [ ] **Step 5: Commit**

---

## Task 7: Rules applied at transcription time

**Files:**
- Modify: `src/Diariz.Api/Services/SpeakerLabeling.cs`
- Create: `tests/Diariz.Api.Tests/SpeakerLabelingSuggestionTests.cs`

**Interfaces:**
- Consumes: `IdentificationRules`, `RankAsync`, `SpeakerAssignment`, the decision log.
- `ApplyAsync` gains the speech durations (computed by the caller from the segments it already has) and the thresholds.

**Behaviour:**

| Verdict | Effect |
|---|---|
| Accept | As today: name, link, `IdentifiedAuto = true` |
| Suggest | `SuggestedProfileId`/`Distance`/`At` set; **the speaker stays anonymous** |
| Ignore | Nothing, except the existing revert of a stale auto-label |

**Guards, in order:** skip manually named or manually assigned; skip `IsMultiSpeaker`; skip below `MinSpeechMs`; **skip any `(speaker, person)` pair already rejected**, so a declined suggestion stays declined forever.

- [ ] **Step 1: Write the failing tests**

Cover at minimum: a suggestion does **not** set `PersonId`; a rejected pair is never re-suggested; a manual name is untouched at both outcomes; a stale auto-label still reverts.

- [ ] **Step 2-6:** fail, implement, pass, mutation-verify the rejected-pair guard, commit.

---

## Task 8: The re-scan

**Files:**
- Create: `src/Diariz.Api/Services/IdentificationRescan.cs`
- Create: `tests/Diariz.Api.IntegrationTests/IdentificationRescanIntegrationTests.cs`
- Modify: `src/Diariz.Api/Controllers/PlatformSettingsController.cs` (the trigger)

**Interfaces:**
- Produces:
  ```csharp
  public record RescanReport(int Scanned, int Applied, int Suggested, int Skipped);
  Task<RescanReport> RunAsync(Guid? personId, bool dryRun, CancellationToken ct);
  ```

**Synchronous by design.** ~0.35 ms per 1,000 gallery rows per probe, 1,125 speakers: well under a second. A dry run returns the same report **without saving**, so the UI can state "this would apply 128 and queue 74" before committing.

**Re-scan adds; it never revokes.** Revocation of a stale auto-label stays at transcription time. A knob change must not mass-unlabel history - you have said auto-association is almost always right, and stripping correct labels because a slider moved is strictly worse than leaving them.

**Integration, not unit.** The whole point is real pgvector ranking over many speakers; the in-memory provider `Ignore`s the vector column, so a unit test would prove nothing about the thing being built.

- [ ] **Step 1: Write the failing test** - seed a person and several speakers at known distances (construct unit vectors with a chosen first component, as `VoiceprintCallbackIntegrationTests` does), then assert the report and that a dry run wrote nothing.
- [ ] **Step 2-6:** fail, implement, pass, mutation-verify that dry run really does not save, commit.

---

## Task 9: The queue, accept and reject

**Files:**
- Create: `src/Diariz.Api/Controllers/SpeakerSuggestionsController.cs`
- Create: `tests/Diariz.Api.Tests/SpeakerSuggestionsEndpointTests.cs`

**Interfaces:**
- `GET /api/speaker-suggestions` -> pending suggestions **in recordings the caller owns**
- `POST /api/speaker-suggestions/{speakerId}/accept`
- `POST /api/speaker-suggestions/{speakerId}/reject`

**Scoping, and why it is not an admin queue.** A suggestion says "this speaker in recording X might be Ken". The person who can judge that was in the meeting - so the queue is **the caller's own recordings**, and needs no special permission. A platform-wide queue would show an administrator who appears in every meeting in the instance, which is the same privacy hole `ManagePeople` exists to close on the directory.

**Accept** delegates to `SpeakerAssignment` (Task 6), clears the suggestion, and writes an `Accepted` decision carrying `SuggestedDistance`.
**Reject** clears the suggestion and writes a `Rejected` decision. The speaker stays anonymous.

Both are idempotent: a second call on a cleared suggestion is `204`, not `500` - two browser tabs are not a server error.

- [ ] Steps as Task 5. Mutation-verify the ownership filter: removing it must red a test proving another user's suggestion is invisible.

---

## Task 10: Web - the queue and the inline prompt

**Files:**
- Create: `apps/web/src/pages/SpeakerSuggestions.tsx` + test
- Create: `apps/web/src/components/detail/SpeakerSuggestionPrompt.tsx` + test
- Modify: `apps/web/src/pages/RecordingDetail.tsx`
- Modify: `apps/web/src/lib/api.ts`, `types.ts`
- Modify: the four locale catalogs

Both surfaces call the same accept/reject. The queue is how you find a backlog; the transcript is where you have the words and the audio to judge one.

**Testing notes:** `userEvent`, not `fireEvent`, for anything asserting a disabled control. No `jest-dom` matchers - none of the 230+ existing web test files use them. `vi.waitFor` checks once immediately, so a "this must not appear" assertion needs a flushed macrotask tick and a synchronous assert.

- [ ] Steps as Task 11 of Phase 1, including a browser check: jsdom computes no geometry, so confirm in the running app that a long recording name truncates rather than pushing the row off-screen.

---

## Task 11: Settings UI and release

**Files:**
- Modify: `apps/web/src/components/SettingsModal.tsx` (the four knobs + "Re-scan now" with its dry-run preview)
- Modify: `src/Diariz.Api/Controllers/PlatformSettingsController.cs`, `ApiDtos.cs`
- The full release checklist

**On placement:** the knobs go in the existing platform panel rather than a new admin page - `SettingsModal` already hosts exactly this shape of numeric setting. The dedicated `/admin/speaker-identification` page earns its keep in Phase 4, when the sweep curve needs somewhere to live. `SettingsModal` is 845 lines and growing; if it passes ~1,000 while doing this, split the platform panel into its own component rather than adding to it.

- [ ] **Step 1: The knobs, with validation** - reject a confirm band below the threshold with a message, rather than storing an inversion that silently stops anything auto-applying.
- [ ] **Step 2: Re-scan with dry-run preview**
- [ ] **Step 3: Regenerate the OpenAPI snapshot (run twice) and the n8n node (`npm run generate`)**
- [ ] **Step 4: Version bump 0.250.0 -> 0.251.0** (functional enhancement) across **six** mirrors: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`, and `apps/web/package-lock.json` **in both places npm writes it**.
- [ ] **Step 5: `RELEASES[0]`** - check the real PR number first (`gh issue list` / `gh pr list`); issues and Dependabot share the sequence.
- [ ] **Step 6: README Features row, `docs/features.md` bullet, CAPABILITIES row - all three or none.**
- [ ] **Step 7: `docs/Data_Schema.md`** (two new tables' worth of columns + three migration rows) **and `docs/Overall_Synopsis_of_Platform.md`** (the rules, the re-scan, the decision log, the queue's ownership scoping).
- [ ] **Step 8: Help article** - `transcription-and-speakers.md` gains the confirmation prompt; ASCII only.
- [ ] **Step 9: Full suites** - `dotnet build Diariz.slnx`, unit, integration, web, web build.
- [ ] **Step 10: `git status` before pushing** - confirm no new file is untracked. `git add -u` will not stage one, and in Phase 1 that nearly shipped the security boundary's tests out of the PR.
- [ ] **Step 11: PR.** Deployment surface: **server redeploy only** - no ffmpeg change, no worker change, no desktop release.

---

## Self-review notes

**Spec coverage.** Phase 2 lists: knobs into `PlatformSettings` (Tasks 1, 11), the confirmation band (Tasks 2, 7), the decision log (Task 4), re-scan with dry run (Task 8), queue plus inline prompts (Tasks 9, 10). All covered.

**Deliberately deferred to Phase 3/4,** so an implementer does not build them here: `ProfileVoiceprints` and clustering, `Segments.VoiceEmbedding`, the `segment-embed-jobs` stream, the admin bench and its leave-one-out sweep. Task 2's contract is shaped for Phase 3 (one candidate per person) but must not grow templates now.

**Two deviations from the spec, both deliberate and argued above:** the queue is scoped to the caller's own recordings rather than being an admin surface, and the knobs live in the existing settings panel rather than a new admin page.

**Known risk.** Task 6 is a refactor whose safety net is existing tests. If Step 4's mutation check finds the opt-out guard uncovered, that gap must be filled before the extraction is trusted - a silent behaviour change there would enrol a biometric for someone who asked not to have one.
