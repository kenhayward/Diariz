# Is this recording somebody else? - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ask, of every recording behind a voiceprint, whether **somebody else is closer** - and let a
human vouch for the ones that are genuinely right, so the training set can be cleaned before anything
clusters it.

**Architecture:** `VoiceprintDiagnosis` gains the whole training set instead of one person's slice, and
reports a nearest **impostor** alongside the nearest sibling. One new verdict takes precedence over the
existing three. `VoiceSample` gains `ConfirmedAt`/`ConfirmedByUserId`; confirmed rows leave the queue.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Npgsql/pgvector, xUnit, Testcontainers, React 19 + TS,
vitest + @testing-library/react.

**Design:** `docs/superpowers/specs/2026-08-26-voiceprint-impostor-check-design.md`.

**Branch from `main` once [#632](https://github.com/kenhayward/Diariz/pull/632) has merged** - it carries
0.255.0 and touches `PersonAttributionRow`, `PersonVoiceprintTab` and the same locale files.

## Global Constraints

- **TDD.** Failing test first, watch it fail for the stated reason, then the minimal code.
- **Mutation-verify every new test** and quote the failure. Tests that cannot fail are this repo's
  dominant defect class.
- **Never put production data in the repo** - no real names, emails, company names or recording titles in
  fixtures, comments, docs, commit messages or the PR. Invent names (`Ada`, `Grace`, `Alice`). Findings
  are reported as counts.
- **No em or en dashes in user-facing text.** Plain `-`.
- **Four locale catalogs** (`en`, `de`, `es`, `fr`) with key parity enforced by `locales.test.ts`. `de` is
  written without accents in these files; `es` and `fr` use them.
- **Store UTC.** Npgsql throws at `SaveChanges` for a non-zero-offset `DateTimeOffset` on `timestamptz`;
  the in-memory provider will not catch it.
- **Unit tests use the EF in-memory provider**, which Ignores `vector` columns and does not enforce FKs.
  Anything turning on an embedding, a real query translation or a FK goes in the integration project.
- **`--filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- **`npm run build` runs `tsc` with test files excluded**, so a required prop added to a component will
  not fail the build from its harness. Update harnesses by hand and run `npm test`.
- **No jest-dom.** Plain assertions only.
- **Never `git add -A`.** Stage explicit paths; check `git status` for `??` before committing.
- **`api/people` is in the published OpenAPI document**, so DTO changes need the snapshot regenerating
  (it self-heals on a second run) and `integrations/n8n-nodes-diariz` regenerating (it does not).

## The decision the spec left open

**The impostor verdict requires a sibling to compare against.** A person with a single sample stays
`Only`, however close somebody else sits.

The evidence for a misattribution is *relative* - "this sits closer to another person than to any of
their own" - and that comparison needs at least one sibling. With one sample there is only an absolute
distance, and two different people genuinely can sound similar; flagging on that alone would open 61 more
people on the weakest possible evidence and bury the 27 real ones.

**The cost:** a single-sample person who was misattributed stays invisible. That is a real limitation and
belongs in the release notes, not buried here.

---

### Task 1: The impostor measurement

**Files:**
- Modify: `src/Diariz.Api/Services/VoiceprintDiagnosis.cs`
- Test: `tests/Diariz.Api.Tests/VoiceprintDiagnosisTests.cs`

**Interfaces:**
- Produces:
  - `record TrainingSample(Guid Id, Guid PersonId, float[] Embedding)`
  - `SampleVerdict.Impostor` (a new member - the enum is **not** persisted, so ordering is free)
  - `SampleDiagnosis` gains `double? NearestImpostorDistance, Guid? NearestImpostorPersonId`
  - `Diagnose(Guid personId, IReadOnlyList<TrainingSample> all, IdentificationThresholds t)`

  The signature changes from "this person's samples" to "the whole training set, plus whose samples to
  diagnose". Both endpoints already have or can cheaply get the whole set, and it is the only shape that
  can answer the impostor question at all.

- [ ] **Step 1: Rewrite the existing tests to the new signature, then add the new cases.**

The existing file builds `List<(Guid, float[])>` and calls `Diagnose(samples, T)`. Convert each to
`TrainingSample` with a shared `Person` id, and pass that id. The existing assertions are unchanged -
they are all about siblings, and none of them should move.

Then add:

```csharp
    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Grace = Guid.NewGuid();

    [Fact]
    public void A_sample_closer_to_someone_else_says_so()
    {
        // The finding this whole phase exists for: 27 of 92 live samples sit closer to a different person
        // than to any of their own. Clustering promotes each of those from a diluted nuisance into a
        // confident match for whoever that voice really belongs to.
        var odd = Guid.NewGuid();
        List<TrainingSample> all =
        [
            new(Guid.NewGuid(), Ada, At(0)),
            new(odd, Ada, At(0.9)),
            new(Guid.NewGuid(), Grace, At(0.95)),   // nearer to `odd` than Ada's own sample is
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single(x => x.SampleId == odd);

        Assert.Equal(SampleVerdict.Impostor, d.Verdict);
        Assert.Equal(Grace, d.NearestImpostorPersonId);
    }

    [Fact]
    public void A_close_impostor_does_not_outrank_a_closer_sibling()
    {
        // Two people can genuinely sound alike. What makes a misattribution is the impostor being closer
        // than the person's own recordings, not merely being close.
        var mine = Guid.NewGuid();
        List<TrainingSample> all =
        [
            new(mine, Ada, At(0)),
            new(Guid.NewGuid(), Ada, At(0.10)),
            new(Guid.NewGuid(), Grace, At(0.20)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single(x => x.SampleId == mine);

        Assert.Equal(SampleVerdict.Core, d.Verdict);
        // Still reported, because the number is worth seeing even when it is not a finding.
        Assert.NotNull(d.NearestImpostorDistance);
    }

    [Fact]
    public void A_lone_sample_is_never_an_impostor_however_close_the_neighbour()
    {
        // Deliberate. The evidence for a misattribution is relative - closer to them than to their own -
        // and one sample has no "own" to compare with. Flagging on absolute distance would open every
        // single-sample person in the directory on the weakest possible evidence.
        var lone = Guid.NewGuid();
        List<TrainingSample> all = [new(lone, Ada, At(0)), new(Guid.NewGuid(), Grace, At(0.05))];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single();

        Assert.Equal(SampleVerdict.Only, d.Verdict);
    }

    [Fact]
    public void A_directory_of_one_person_has_no_impostor_to_report()
    {
        List<TrainingSample> all = [new(Guid.NewGuid(), Ada, At(0)), new(Guid.NewGuid(), Ada, At(0.1))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(Ada, all, T),
            d => Assert.Null(d.NearestImpostorDistance));
    }

    [Fact]
    public void Only_the_named_persons_samples_are_diagnosed()
    {
        List<TrainingSample> all =
        [
            new(Guid.NewGuid(), Ada, At(0)),
            new(Guid.NewGuid(), Ada, At(0.1)),
            new(Guid.NewGuid(), Grace, At(0.5)),
        ];

        Assert.Equal(2, VoiceprintDiagnosis.Diagnose(Ada, all, T).Count);
    }
```

- [ ] **Step 2: Run and watch them fail.** Compile error on `TrainingSample`. Quote it.

- [ ] **Step 3: Implement.** `Verdict` takes the impostor distance and checks it first:

```csharp
    private static SampleVerdict Verdict(double nearest, double? impostor, IdentificationThresholds t) =>
        // Checked before anything else, and strictly more serious: if a sibling were closer the question
        // could not arise. It replaces Alone for most of the samples that had it, and catches the ones no
        // sibling-only verdict could - a sample sitting comfortably inside its own cluster while sitting
        // closer still to somebody else's.
        impostor is { } other && other < nearest ? SampleVerdict.Impostor
        : nearest <= t.Accept ? SampleVerdict.Core
        : nearest <= t.Suggest ? SampleVerdict.Variant
        : SampleVerdict.Alone;
```

The `Only` branch returns before `Verdict` is reached, which is what keeps a lone sample out of it.

- [ ] **Step 4: Green, then mutation-verify.** Change `other < nearest` to `other < t.Accept` and confirm
  `A_close_impostor_does_not_outrank_a_closer_sibling` fails. Move the impostor check below the `Core`
  branch and confirm `A_sample_closer_to_someone_else_says_so` fails.

- [ ] **Step 5: Commit.**

---

### Task 2: Both endpoints report it

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs` (`Diagnostics`, `DirectoryDiagnostics`)
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.IntegrationTests/VoiceprintDiagnosticsIntegrationTests.cs` (append)

**Interfaces:**
- Produces: `SampleDiagnosisDto` gains `double? NearestImpostorDistance, Guid? NearestImpostorPersonId,
  string? NearestImpostorName`; `PersonDiagnosticsSummaryDto` gains `int ImpostorCount`.

`DirectoryDiagnostics` already loads every training sample, so it pays nothing. `Diagnostics(id)` loads
only the person's slice today and must load the whole set - about 118 KB at the current scale, and the
same approach the directory endpoint already takes.

- [ ] **Step 1: Write the failing integration tests.**

1. A person with two samples, one of which sits closer to another person's sample, comes back with
   verdict `Impostor` and the **other person's name** - not just a distance. The name is the point: it
   turns the finding into a reassignment, and the control for that is already on the row.
2. `DirectoryDiagnostics` reports a non-zero `ImpostorCount` for that person and sorts them **above** a
   person who only has an `Alone`.
3. A person whose samples are all mutually close, in a directory containing other people, still reports
   `ImpostorCount` 0 and does not appear in the ranking.

Use the `SeedOrthogonalAsync` / `Controller` patterns already in that file.

- [ ] **Step 2: Run and watch them fail.** Quote the failures.

- [ ] **Step 3: Implement.** In `Diagnostics`, replace the per-person load with the whole training set,
  filtered through `VoiceprintTraining.Trains` exactly as now, then diagnose with the person's id. Look
  the impostor's name up from `_db.People` for the ids that came back - one query, not one per row.

  In `DirectoryDiagnostics`, call `Diagnose(g.Key, allSamples, thresholds)` per person over the list it
  already has, count `Impostor` alongside `Alone`, and reorder:

```csharp
        .Where(r => r.ImpostorCount > 0 || r.AloneCount > 0)
        // Impostors first: "this is somebody else" is a different order of problem from "this sounds
        // unlike the rest", and it is the one that turns into a confident false match if it is clustered.
        .OrderByDescending(r => r.ImpostorCount)
        .ThenByDescending(r => r.AloneCount)
        .ThenByDescending(r => r.WidestPair ?? 0)
```

- [ ] **Step 4: Green, then mutation-verify** the ordering (drop the `ImpostorCount` sort key) and the
  name lookup (return null).

- [ ] **Step 5: Regenerate the OpenAPI snapshot** (fails once, passes on the second run - commit the
  regenerated file) and `cd integrations/n8n-nodes-diariz && npm run generate`.

- [ ] **Step 6: Commit.**

---

### Task 3: Confirmation - domain and endpoint

**Files:**
- Modify: `src/Diariz.Domain/Entities/VoiceSample.cs`
- Create: a migration, `AddVoiceSampleConfirmation`
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`, `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/VoiceprintConfirmationTests.cs` (create)

**Interfaces:**
- Produces:
  - `VoiceSample.ConfirmedAt` (`DateTimeOffset?`), `VoiceSample.ConfirmedByUserId` (`Guid?`)
  - `PUT /api/people/{id}/voiceprint/samples/{sampleId}/confirmed`, body `{ confirmed: bool }`
  - `VoiceSampleDto` gains `bool Confirmed`

Route shape matches the existing `/voiceprint/samples/{sampleId}/spans`. Gate on
`CanManageBiometricsAsync(person)`, the same as spans.

- [ ] **Step 1: Write the failing tests.** Confirming stamps both columns; unconfirming clears both;
  someone without the biometrics permission is refused; an unknown sample is 404. Assert on the columns,
  not on a returned DTO, so the test survives the DTO changing shape.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement**, then `dotnet ef migrations add AddVoiceSampleConfirmation --project
  src/Diariz.Domain --startup-project src/Diariz.Api`. **Read the generated migration**: both columns
  must be nullable and there must be no data movement. Additive and forward-restore-safe, so **no
  `CurrentFormat` bump** - say so in the PR.

- [ ] **Step 4: Green, then mutation-verify** the permission gate (remove it and confirm the refusal test
  fails).

- [ ] **Step 5: Commit.**

---

### Task 4: Confirmed rows leave the queue

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs` (`DirectoryDiagnostics`)
- Test: `tests/Diariz.Api.IntegrationTests/VoiceprintDiagnosticsIntegrationTests.cs` (append)

Confirmation has to *do* something now, or it is a tick box that changes nothing until a later release.

- [ ] **Step 1: Write the failing tests.** A person whose only flagged sample is confirmed drops out of
  the ranking entirely; unconfirming puts them back. A person with two flagged samples, one confirmed,
  stays - with the count reduced by one.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.** The counts exclude confirmed samples. **The verdict itself is unchanged** -
  the distance is still what it is, and `Diagnostics(id)` still reports it, muted in the UI. Only the
  *queue* shrinks.

- [ ] **Step 4: Green, then mutation-verify.**

- [ ] **Step 5: Commit.**

---

### Task 5: The web says who it sounds like

**Files:**
- Modify: `apps/web/src/lib/voiceprintVerdict.ts`, `voiceprintVerdict.test.ts`
- Modify: `apps/web/src/components/PersonAttributionRow.tsx`, `PersonVoiceprintTab.tsx`
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/components/PersonVoiceprintTab.test.tsx`
- Modify: all four `apps/web/src/locales/*/people.json`

- [ ] **Step 1: Write the failing tests.**

1. `rowVerdict` maps the server's `"Impostor"` to `"impostor"`, and `sortKey` puts it **above** `alone`.
2. A row with that verdict reads **"Sounds more like {name}"**, naming the other person - assert on the
   name being present, since a verdict without it is not actionable.
3. It sorts to the very top of the list, above a row that only sounds unlike the others.
4. A confirmed row still shows its verdict (muted, not hidden) and is excluded from the header's
   "worth a listen" count.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.** `RowVerdict` gains `"impostor"`; `sortKey` returns 0 for it and pushes the
  others down; `worthChecking` includes it. The chip is red rather than amber - it is a different order
  of problem from "sounds unlike the rest".

- [ ] **Step 4: Green, then mutation-verify** the ordering and the name.

- [ ] **Step 5: Commit.**

---

### Task 6: The confirm control

**Files:**
- Modify: `apps/web/src/components/PersonAttributionRow.tsx`, `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/components/PersonVoiceprintTab.test.tsx`
- Modify: all four `people.json`

- [ ] **Step 1: Write the failing tests.**

1. Ticking **Confirmed as this person** calls the endpoint with the sample id and `true`, and re-reads
   the list.
2. Unticking sends `false`.
3. It is a **separate control from "Trains the voiceprint"**, and toggling one does not call the other's
   endpoint. This is the test that stops the two being quietly merged later: they answer different
   questions - is this the right person, versus is this audio good enough to learn from.
4. A row with no sample offers no confirm control - there is nothing to confirm and no verdict.
5. There is **no bulk confirm** anywhere on the tab. Assert the absence of a control matching
   /confirm all|confirm every/i, with the reason in a comment: the gate exists because only listening
   separates the two cases, so a button that confirms unheard audio reintroduces the failure.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Green, then mutation-verify** that test 3 fails if the confirm handler is pointed at
  `setAttributionTraining`.

- [ ] **Step 5: Commit.**

---

### Task 7: Release

**Version: `0.255.0` -> `0.256.0`.** A functional enhancement.

- [ ] **Step 1: Bump `version.json` and all five mirrors** - `apps/web/package.json`,
  `apps/web/package-lock.json` (**two** places), `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`.

- [ ] **Step 2: `RELEASES[0]`.** Build `\n` escapes with `chr(92)` if writing via a script - a bash
  heredoc collapses them and breaks the string literal. **Confirm the PR number** with `gh pr list`
  rather than assuming last + 1; issues share the sequence and no test catches a wrong one.

  Say plainly what it found and what it does not cover: that a single-sample person who was misattributed
  stays invisible, because the evidence is relative and there is nothing to compare against.

- [ ] **Step 3: Scope changed**, so all three inventories move in lockstep - the `CAPABILITIES` row, the
  **README Features** row, and the **`docs/features.md`** prose.

- [ ] **Step 4: `docs/Data_Schema.md`** - both new columns and a migration-history row.
  **`docs/Overall_Synopsis_of_Platform.md`** - the impostor comparison and what confirmation gates.

- [ ] **Step 5: `apps/web/src/content/help/en/people-directory.md`** - what "sounds more like someone
  else" means, that it names who, and what confirming does.

- [ ] **Step 6: Full green** - `dotnet build Diariz.slnx && dotnet test`, then `npm run build && npm test`
  in `apps/web`. **No edits after the last green run.**

- [ ] **Step 7: Push and open the PR.** Deployment surface: **server redeploy only**, one additive
  migration. No worker or GPU involvement.

- [ ] **Step 8: Re-run the measurement** once the queue has been worked, and report the counts - that is
  the input to the Phase 4 re-decision, and the reason this plan exists instead of Phase 4.
