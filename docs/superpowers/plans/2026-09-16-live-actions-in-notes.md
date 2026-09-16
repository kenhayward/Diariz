# Live Actions in the Notes Popover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user add actions from the live notes popover (and the desktop pop-out) during a meeting, attached to the recording as **pinned** ("adopted") actions, without stopping the AI action extraction that runs after the meeting.

**Architecture:** Actions become a second `kind` of line in the existing live notes stream, so they reuse its timeline, IndexedDB stash and pop-out channel. After upload the recorder splits the lines, sending notes to the existing notes endpoint and actions to a new `POST api/recordings/{id}/actions/live`, which creates pinned `Source = Live` rows and leaves `ActionsExtractedAt` alone. The pipeline's `ActionsProcessor` then merges instead of replacing: it tells the LLM what was already recorded and drops normalised duplicates. Explicit re-extract replaces unpinned rows only.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core/Npgsql, xUnit (no mocking library; fakes in `tests/Diariz.Api.TestSupport`), React 19 + TS + Vitest + @testing-library/react (no jest-dom matchers).

**Spec:** `docs/superpowers/specs/2026-09-16-live-actions-in-notes-design.md`

## Global Constraints

- TDD: every production change is preceded by a failing test that you watched fail. Quote the real failure.
- No em/en dashes in any user-facing string (UI, all four locale catalogs, release notes, help). Use `-`. Enforced by `apps/web/src/lib/noFancyDashes.test.ts`.
- Help articles are ASCII only.
- Every new `workspace.json` key goes into **en, de, es, fr** (parity asserted by `apps/web/src/locales.test.ts`, no empty values).
- Enum ints are append only: `ActionSource` `Extracted = 0`, `Manual = 1`, `Live = 2`. Never renumber.
- The web suite fails on any `act(...)` warning (`src/test-setup.ts`). Resolve elements before acting; use testing-library `waitFor`, not `vi.waitFor`.
- No jest-dom matchers in `apps/web`. Use plain `expect(...).toBe(...)` on DOM properties.
- Never `git add -A`; stage explicit paths.
- Never put production data (names, titles, transcript text) in fixtures. Use invented names (Ada, Grace).
- `dotnet test --filter "Name=X"` does not work here; use `--filter "FullyQualifiedName~X"`.
- Before pushing, `dotnet build Diariz.slnx` (catches integration-project compile breaks that unit runs miss).
- Version for this PR: **0.272.0** (feature: minor +1, build reset). Server redeploy only, no desktop release.
- Work on a branch (`claude/live-actions-in-notes`), finish with a PR. This is a feature, so no GitHub issue is required.

## File map

| File | Responsibility | Task |
|---|---|---|
| `src/Diariz.Domain/Entities/ActionSource.cs` (new) | where an action came from | 1 |
| `src/Diariz.Domain/Entities/RecordingAction.cs` | `Source`, `CapturedAtMs` | 1 |
| `src/Diariz.Domain/Migrations/*_AddActionSourceAndCapturedAt.cs` (generated) | columns | 1 |
| `src/Diariz.Api/Controllers/RecordingActionsController.cs` | `Manual` on create (1), live endpoint (3), re-extract keeps pinned (5) | 1,3,5 |
| `src/Diariz.Api/Controllers/RecordingsController.cs` | merge copies `Source` | 1 |
| `src/Diariz.Api/Services/ActionsPrompt.cs` | `alreadyRecorded` in the user message | 2 |
| `src/Diariz.Api/Services/ActionMerge.cs` (new) | pure normalised dedupe | 2 |
| `src/Diariz.Api/Services/ActionsClient.cs` + `tests/Diariz.Api.TestSupport/Fakes.cs` | pass `alreadyRecorded` through | 2 |
| `src/Diariz.Api/Contracts/ApiDtos.cs` | `CreateLiveActionsRequest`, `CreateLiveActionLine` | 3 |
| `src/Diariz.Api/Services/ActionsProcessor.cs` | merge instead of replace | 4 |
| `apps/web/src/lib/types.ts` | `LiveNoteLine`, `LineKind` | 6 |
| `apps/web/src/lib/notesStream.ts` | `action` item, `actions` filter/count | 6 |
| `apps/web/src/lib/pendingNotes.ts`, `useLiveNotes.ts` | kind/actor/deadline, `setKind`, `updateAction` | 7 |
| `apps/web/src/lib/notesChannel.ts`, `useNotesPopout.ts` | carry the new commands | 8 |
| `apps/web/src/components/hub/notesStreamRows.tsx`, `LiveNotesStream.tsx`, locales | composer toggle, chip, rows | 9 |
| `apps/web/src/components/hub/NotesPopover.tsx`, `pages/NotesPopout.tsx`, `components/Recorder.tsx`, `lib/api.ts` | wiring + split attach | 10 |
| release/doc files | checklist | 11 |

---

### Task 1: `ActionSource` and `CapturedAtMs` columns

**Files:**
- Create: `src/Diariz.Domain/Entities/ActionSource.cs`
- Modify: `src/Diariz.Domain/Entities/RecordingAction.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingActionsController.cs:145-153` (Create)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:2316-2328` (Merge)
- Generate: migration `AddActionSourceAndCapturedAt` in `src/Diariz.Domain/Migrations/`
- Test: `tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs`, `tests/Diariz.Api.Tests/RecordingsControllerTests.cs`, `tests/Diariz.Api.IntegrationTests/ActionsIntegrationTests.cs`

**Interfaces:**
- Produces: `enum ActionSource { Extracted = 0, Manual = 1, Live = 2 }` (namespace `Diariz.Domain.Entities`); `RecordingAction.Source` (`ActionSource`), `RecordingAction.CapturedAtMs` (`long?`).

- [ ] **Step 1: Create the branch**

```bash
git checkout -b claude/live-actions-in-notes
```

- [ ] **Step 2: Write the failing unit tests**

Add to `RecordingActionsControllerTests`:

```csharp
[Fact]
public async Task Create_RecordsTheActionAsManual()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var rec = await SeedTranscribed(db, userId);

    var dto = (await Build(db, userId, new FakeActionsClient())
        .Create(rec.Id, new CreateRecordingActionRequest("Book the room", "Ada", ""))).Value!;

    var row = await db.RecordingActions.SingleAsync(a => a.Id == dto.Id);
    Assert.Equal(ActionSource.Manual, row.Source);
    Assert.Null(row.CapturedAtMs);
}
```

Add to `RecordingsControllerTests`, next to `Merge_CarriesThePinnedFlagOntoTheSurvivor`:

```csharp
[Fact]
public async Task Merge_CarriesSource_AndDropsCapturedAtFromTheOtherClock()
{
    // CapturedAtMs is an offset into the recording it was typed in. The folded-in recording's clock is not
    // the survivor's, so its offsets would point at the wrong moment - they are dropped. Source is a fact
    // about the action and travels with it.
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello");
    var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
    db.RecordingActions.Add(new RecordingAction
    {
        Id = Guid.NewGuid(), RecordingId = later.Id, Text = "Send the deck", Ordinal = 0,
        Source = ActionSource.Live, CapturedAtMs = 42_000, Pinned = true,
    });
    await db.SaveChangesAsync();

    await Build(db, userId, new FakeJobQueue()).Merge(new MergeRecordingsRequest([later.Id, early.Id]));

    var moved = await db.RecordingActions.SingleAsync(a => a.RecordingId == early.Id);
    Assert.Equal(ActionSource.Live, moved.Source);
    Assert.Null(moved.CapturedAtMs);
}
```

- [ ] **Step 3: Run and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Create_RecordsTheActionAsManual|FullyQualifiedName~Merge_CarriesSource"`
Expected: build FAILS with `CS0103: The name 'ActionSource' does not exist` / `'RecordingAction' does not contain a definition for 'Source'`.

- [ ] **Step 4: Add the enum and properties**

`src/Diariz.Domain/Entities/ActionSource.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Where an action item came from. Stored as an int: append only, never renumber.</summary>
public enum ActionSource
{
    /// <summary>Found by the LLM extraction (the default, and what every row predating this column was).</summary>
    Extracted = 0,
    /// <summary>Added by hand on the recording's page or through the API.</summary>
    Manual = 1,
    /// <summary>Typed into the live notes panel while the meeting was being recorded.</summary>
    Live = 2,
}
```

Append to `RecordingAction` (after `Pinned`):

```csharp
    /// <summary>Where this action came from. Live actions are the ones extraction must not duplicate and
    /// re-extraction must not discard (the latter via <see cref="Pinned"/>, which live actions always start with).</summary>
    public ActionSource Source { get; set; } = ActionSource.Extracted;

    /// <summary>Offset (ms) into the recorded clock when a live action was typed; null for every other
    /// source. An immutable capture fact, like <see cref="MeetingNote.CapturedAtMs"/>.</summary>
    public long? CapturedAtMs { get; set; }
```

In `RecordingActionsController.Create`, add `Source = ActionSource.Manual,` to the `new RecordingAction { ... }` initialiser.

In `RecordingsController.Merge`, change the initialiser line `Pinned = a.Pinned, Completed = a.Completed, CompletedAt = a.CompletedAt,` to:

```csharp
                Pinned = a.Pinned, Completed = a.Completed, CompletedAt = a.CompletedAt,
                // Source is a fact about the action. CapturedAtMs is not copied: it is an offset into the
                // folded-in recording's clock, which is not the survivor's.
                Source = a.Source,
```

- [ ] **Step 5: Run the unit tests and watch them pass**

Run: same command as Step 3. Expected: 2 passed.

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add AddActionSourceAndCapturedAt --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated `Up`: it must add `Source integer NOT NULL DEFAULT 0` and `CapturedAtMs bigint NULL` to `RecordingActions`, and nothing else. If it contains anything else, stop and investigate (pending model drift), do not commit it.

- [ ] **Step 7: Write the integration test (real Postgres)**

Add to `ActionsIntegrationTests`:

```csharp
[Fact]
public async Task SourceAndCapturedAt_RoundTrip_AndExistingRowsDefaultToExtracted()
{
    Guid live, legacy;
    await using (var db = fx.CreateDbContext())
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", Title = "Planning" };
        var a1 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Send the deck", Ordinal = 0,
            Source = ActionSource.Live, CapturedAtMs = 61_000, Pinned = true };
        var a2 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 1 };
        db.AddRange(user, rec, a1, a2);
        await db.SaveChangesAsync();
        (live, legacy) = (a1.Id, a2.Id);
    }

    await using var read = fx.CreateDbContext();
    var l = await read.RecordingActions.SingleAsync(a => a.Id == live);
    Assert.Equal(ActionSource.Live, l.Source);
    Assert.Equal(61_000, l.CapturedAtMs);
    Assert.Equal(ActionSource.Extracted, (await read.RecordingActions.SingleAsync(a => a.Id == legacy)).Source);
    Assert.Null((await read.RecordingActions.SingleAsync(a => a.Id == legacy)).CapturedAtMs);
}
```

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~SourceAndCapturedAt_RoundTrip"` (needs Docker). Expected: PASS. Mutation-check it: temporarily delete the `Source` column line from the migration's `Up`, rerun, confirm it fails with a Postgres `42703 column "Source" does not exist`, then restore the file **and `touch` it** (a restore keeps the old mtime and MSBuild would skip the rebuild).

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/ActionSource.cs src/Diariz.Domain/Entities/RecordingAction.cs src/Diariz.Domain/Migrations/ src/Diariz.Api/Controllers/RecordingActionsController.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs tests/Diariz.Api.Tests/RecordingsControllerTests.cs tests/Diariz.Api.IntegrationTests/ActionsIntegrationTests.cs
git commit -m "feat(actions): record where an action came from and when it was typed"
```

---

### Task 2: Prompt context and the dedupe helper

**Files:**
- Modify: `src/Diariz.Api/Services/ActionsPrompt.cs:72-80`
- Create: `src/Diariz.Api/Services/ActionMerge.cs`
- Modify: `src/Diariz.Api/Services/ActionsClient.cs:13-30` (interface + impl)
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs:310-332` (`FakeActionsClient`)
- Modify call sites: `src/Diariz.Api/Services/ActionsProcessor.cs:81`, `src/Diariz.Api/Controllers/RecordingActionsController.cs:107`, and any other `ExtractAsync(` on `IActionsClient` (grep `\.ExtractAsync\(cfg` and `LlmHttpClientTimeoutTests.cs`)
- Test: `tests/Diariz.Api.Tests/ActionsPromptTests.cs` (exists? if not, create), `tests/Diariz.Api.Tests/ActionMergeTests.cs` (new)

**Interfaces:**
- Produces:
  - `ActionsPrompt.BuildMessages(string template, IReadOnlyList<SegmentDto> segments, DateTimeOffset? meetingDate = null, int charBudget = PromptTranscript.DefaultCharBudget, IReadOnlyList<string>? alreadyRecorded = null)`
  - `IActionsClient.ExtractAsync(LlmRequestConfig config, IReadOnlyList<SegmentDto> segments, string template, DateTimeOffset? meetingDate, IReadOnlyList<string>? alreadyRecorded = null, CancellationToken ct = default)`
  - `FakeActionsClient.LastAlreadyRecorded` (`IReadOnlyList<string>?`)
  - `static class ActionMerge { string Normalize(string text); IReadOnlyList<ExtractedAction> WithoutDuplicates(IReadOnlyList<ExtractedAction> extracted, IEnumerable<string> existingTexts); }`

- [ ] **Step 1: Write the failing tests**

Check whether `tests/Diariz.Api.Tests/ActionsPromptTests.cs` exists (`ls tests/Diariz.Api.Tests | grep ActionsPrompt`). Add these tests to it (create the class `public class ActionsPromptTests` with the usings from `ActionsProcessorTests.cs` if the file is new):

```csharp
private static readonly SegmentDto[] OneSegment =
    [new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Ada", 0, 1000, "Grace will book the room.", null)];

[Fact]
public void BuildMessages_ListsAlreadyRecordedActions_InTheUserMessage_NotTheTemplate()
{
    var msgs = ActionsPrompt.BuildMessages(ActionsPrompt.DefaultTemplate, OneSegment,
        alreadyRecorded: ["Book the room", "Send the deck"]);

    Assert.DoesNotContain("Book the room", msgs[0].Content); // system = the editable template, untouched
    Assert.Contains("- Book the room\n- Send the deck", msgs[1].Content);
    Assert.Contains("do not repeat", msgs[1].Content, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void BuildMessages_WithNothingAlreadyRecorded_IsUnchanged()
{
    var without = ActionsPrompt.BuildMessages(ActionsPrompt.DefaultTemplate, OneSegment);
    var empty = ActionsPrompt.BuildMessages(ActionsPrompt.DefaultTemplate, OneSegment, alreadyRecorded: []);

    Assert.Equal(without[1].Content, empty[1].Content);
    Assert.DoesNotContain("do not repeat", without[1].Content, StringComparison.OrdinalIgnoreCase);
}
```

`tests/Diariz.Api.Tests/ActionMergeTests.cs`:

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ActionMergeTests
{
    [Theory]
    [InlineData("Book the room.", "book the room")]
    [InlineData("  Send   the\tdeck!! ", "send the deck")]
    [InlineData("Follow-up with Grace", "follow up with grace")]
    public void Normalize_IgnoresCasePunctuationAndSpacing(string input, string expected) =>
        Assert.Equal(expected, ActionMerge.Normalize(input));

    [Fact]
    public void WithoutDuplicates_DropsItemsMatchingExisting_AndRepeatsWithinTheExtraction()
    {
        var extracted = new[]
        {
            new ExtractedAction("Book the room.", "Grace", ""),
            new ExtractedAction("Chase the invoice", "Ada", "Friday"),
            new ExtractedAction("chase the invoice", "", ""),
        };

        var kept = ActionMerge.WithoutDuplicates(extracted, ["book the ROOM"]);

        Assert.Equal(["Chase the invoice"], kept.Select(a => a.Text));
        Assert.Equal("Ada", kept[0].Actor); // the first occurrence wins, with its fields
    }
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsPromptTests|FullyQualifiedName~ActionMergeTests"`
Expected: build FAILS (`'ActionMerge' does not exist`, no parameter named `alreadyRecorded`).

- [ ] **Step 3: Implement**

Replace `BuildMessages` in `ActionsPrompt.cs`:

```csharp
    /// <param name="alreadyRecorded">Actions the attendees recorded themselves during the meeting. Listed in the
    /// USER message rather than substituted into the template, because the template is admin-editable
    /// (<c>prompts/extract-actions.md</c>) and a placeholder there would silently vanish from an edited copy.</param>
    public static IReadOnlyList<ChatMessage> BuildMessages(
        string template, IReadOnlyList<SegmentDto> segments, DateTimeOffset? meetingDate = null,
        int charBudget = PromptTranscript.DefaultCharBudget, IReadOnlyList<string>? alreadyRecorded = null)
    {
        var system = (template ?? DefaultTemplate)
            .Replace("{calendar_date}", meetingDate?.ToString("yyyy-MM-dd") ?? "[unknown]");
        var user = "Transcript:\n" + PromptTranscript.Build(segments, charBudget);
        if (alreadyRecorded is { Count: > 0 })
            user += "\n\nThese actions were already recorded during the meeting. Do not repeat them, " +
                    "even in different words - output only actions that are not on this list:\n" +
                    string.Join("\n", alreadyRecorded.Select(a => "- " + a));
        return [new ChatMessage("system", system), new ChatMessage("user", user)];
    }
```

`src/Diariz.Api/Services/ActionMerge.cs`:

```csharp
using System.Text;

namespace Diariz.Api.Services;

/// <summary>The backstop behind the prompt's "already recorded" list: an LLM told not to repeat an action
/// sometimes does anyway, word for word. Only catches the same words - paraphrases are the prompt's job.</summary>
public static class ActionMerge
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(c); space = false; }
            else space = true; // punctuation and whitespace both separate words
        }
        return sb.ToString();
    }

    public static IReadOnlyList<ExtractedAction> WithoutDuplicates(
        IReadOnlyList<ExtractedAction> extracted, IEnumerable<string> existingTexts)
    {
        var seen = new HashSet<string>(existingTexts.Select(Normalize));
        return extracted.Where(e => seen.Add(Normalize(e.Text))).ToList();
    }
}
```

In `ActionsClient.cs` add `IReadOnlyList<string>? alreadyRecorded = null,` before `CancellationToken ct = default` in both the interface and the implementation, and pass it: `ActionsPrompt.BuildMessages(template, segments, meetingDate, config.ContextCharBudget, alreadyRecorded)`.

In `FakeActionsClient` add the same parameter, a `public IReadOnlyList<string>? LastAlreadyRecorded { get; private set; }` property, and `LastAlreadyRecorded = alreadyRecorded;` in the body.

Fix every caller that passed `ct` positionally so it compiles: `ActionsProcessor.cs:81` becomes `client.ExtractAsync(cfg, segs, template, rec.StartedAt ?? rec.CreatedAt, ct: ct)`. Find the rest with Grep for `ExtractAsync(` across `src` and `tests` and fix only `IActionsClient` calls (the tags client has its own signature).

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet build Diariz.slnx` then the Step 2 command. Expected: build succeeds, all new tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/ActionsPrompt.cs src/Diariz.Api/Services/ActionMerge.cs src/Diariz.Api/Services/ActionsClient.cs src/Diariz.Api/Services/ActionsProcessor.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/ActionsPromptTests.cs tests/Diariz.Api.Tests/ActionMergeTests.cs
git commit -m "feat(actions): tell extraction what was already recorded, and drop exact repeats"
```
(Add any other call-site files you touched to that `git add`.)

---

### Task 3: `POST api/recordings/{id}/actions/live`

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (after `UpdateRecordingActionRequest`, line ~510)
- Modify: `src/Diariz.Api/Controllers/RecordingActionsController.cs` (new action after `Create`)
- Test: `tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs`

**Interfaces:**
- Consumes: `ActionSource.Live`, `RecordingAction.CapturedAtMs` (Task 1).
- Produces: `record CreateLiveActionsRequest(IReadOnlyList<CreateLiveActionLine> Actions)`; `record CreateLiveActionLine(string Text, string? Actor = null, string? Deadline = null, long? CapturedAtMs = null)`; `RecordingActionsController.CreateLive(Guid recordingId, CreateLiveActionsRequest req) : Task<ActionResult<IReadOnlyList<RecordingActionDto>>>`; JSON body `{ "actions": [{ "text", "actor", "deadline", "capturedAtMs" }] }`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task CreateLive_AddsPinnedLiveActions_AfterExisting_WithoutMarkingExtracted()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var rec = await SeedTranscribed(db, userId);
    db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Existing", Ordinal = 4 });
    await db.SaveChangesAsync();

    var result = await Build(db, userId, new FakeActionsClient()).CreateLive(rec.Id, new CreateLiveActionsRequest(
    [
        new CreateLiveActionLine("  Book the room ", "Ada", "Friday", 61_000),
        new CreateLiveActionLine("   "),                       // blank: skipped
        new CreateLiveActionLine("Send the deck"),
    ]));

    var dtos = result.Value!;
    Assert.Equal(["Book the room", "Send the deck"], dtos.Select(d => d.Text));
    Assert.All(dtos, d => Assert.True(d.Pinned));
    Assert.Equal([5, 6], dtos.Select(d => d.Ordinal));

    var rows = await db.RecordingActions.Where(a => a.Source == ActionSource.Live).OrderBy(a => a.Ordinal).ToListAsync();
    Assert.Equal(61_000, rows[0].CapturedAtMs);
    Assert.Equal("Ada", rows[0].Actor);
    Assert.Equal("", rows[1].Actor);
    // The whole point: the pipeline must still extract after a live action lands.
    Assert.Null((await db.Recordings.FindAsync(rec.Id))!.ActionsExtractedAt);
}

[Fact]
public async Task CreateLive_OnSomeoneElsesRecording_Is404_AndWritesNothing()
{
    using var db = TestDb.Create();
    var rec = await SeedTranscribed(db, Guid.NewGuid());

    var result = await Build(db, Guid.NewGuid(), new FakeActionsClient())
        .CreateLive(rec.Id, new CreateLiveActionsRequest([new CreateLiveActionLine("Book the room")]));

    Assert.IsType<NotFoundResult>(result.Result);
    Assert.Empty(await db.RecordingActions.ToListAsync());
}

[Fact]
public async Task CreateLive_TruncatesLongText()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var rec = await SeedTranscribed(db, userId);

    var dto = (await Build(db, userId, new FakeActionsClient())
        .CreateLive(rec.Id, new CreateLiveActionsRequest([new CreateLiveActionLine(new string('x', 3000))]))).Value!.Single();

    Assert.Equal(2048, dto.Text.Length);
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~CreateLive"`
Expected: build FAILS (`CreateLiveActionsRequest` not found).

- [ ] **Step 3: Implement**

DTOs in `ApiDtos.cs`:

```csharp
/// <summary>Actions typed into the live notes panel during a meeting, attached in one call after the upload.</summary>
public record CreateLiveActionsRequest(IReadOnlyList<CreateLiveActionLine> Actions);
/// <param name="CapturedAtMs">Offset into the recorded clock when the action was typed.</param>
public record CreateLiveActionLine(string Text, string? Actor = null, string? Deadline = null, long? CapturedAtMs = null);
```

Controller action, after `Create`:

```csharp
    [HttpPost("live")]
    [EndpointSummary("Add actions recorded during the meeting")]
    [EndpointDescription(
        "Appends the actions someone recorded while the meeting was running, in one call. Each is created " +
        "**already pinned**, so it appears in the Actions views straight away, and carries the point in the " +
        "recording where it was typed.\n\n" +
        "Unlike adding an action by hand, this does **not** stop automatic extraction: when the transcript is " +
        "ready the extracted actions are added alongside these, skipping any that repeat them. Blank lines are " +
        "skipped and text over 2048 characters is truncated, so read the response for what was created. Owner only.")]
    public async Task<ActionResult<IReadOnlyList<RecordingActionDto>>> CreateLive(Guid recordingId, CreateLiveActionsRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var next = (await _db.RecordingActions
            .Where(a => a.RecordingId == recordingId)
            .Select(a => (int?)a.Ordinal)
            .MaxAsync() ?? -1) + 1;

        var fresh = new List<RecordingAction>();
        foreach (var line in req.Actions)
        {
            var text = (line.Text ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > 2048) text = text[..2048];
            fresh.Add(new RecordingAction
            {
                Id = Guid.NewGuid(),
                RecordingId = recordingId,
                Text = text,
                Actor = line.Actor?.Trim() ?? "",
                Deadline = line.Deadline?.Trim() ?? "",
                Ordinal = next++,
                Pinned = true,
                Source = ActionSource.Live,
                CapturedAtMs = line.CapturedAtMs,
            });
        }
        // ActionsExtractedAt is deliberately left alone - see ActionsProcessor, which merges rather than skips.
        _db.RecordingActions.AddRange(fresh);
        await _db.SaveChangesAsync();
        return fresh.Select(ToDto).ToList();
    }
```

- [ ] **Step 4: Run and watch them pass**

Run: the Step 2 command. Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingActionsController.cs tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs
git commit -m "feat(actions): endpoint for actions recorded during the meeting"
```

---

### Task 4: Pipeline extraction merges instead of replacing

**Files:**
- Modify: `src/Diariz.Api/Services/ActionsProcessor.cs:49-102`
- Test: `tests/Diariz.Api.Tests/ActionsProcessorTests.cs`, `tests/Diariz.Api.Tests/RecordingAiWebhookEmitTests.cs`

**Interfaces:**
- Consumes: `ActionMerge.WithoutDuplicates`, `IActionsClient.ExtractAsync(..., alreadyRecorded, ct)`, `FakeActionsClient.LastAlreadyRecorded` (Task 2); `ActionSource.Live` (Task 1).

- [ ] **Step 1: Write the failing tests**

In `ActionsProcessorTests`:

```csharp
[Fact]
public async Task ProcessAsync_KeepsLiveActions_AppendsExtraction_SkippingRepeats()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var (rec, tr) = await Seed(db, userId);
    db.RecordingActions.Add(new RecordingAction
    {
        Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Send the report", Actor = "Ada", Ordinal = 0,
        Pinned = true, Source = ActionSource.Live, CapturedAtMs = 500,
    });
    await db.SaveChangesAsync();
    var client = new FakeActionsClient
    {
        Result = { new ExtractedAction("Send the report.", "Bob", "Friday"), new ExtractedAction("Book the room", "Grace", "") },
    };

    await ActionsProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
        Job(rec, tr), Template, NullLogger.Instance, new CapturingWebhookPublisher(), "");

    var actions = await db.RecordingActions.Where(a => a.RecordingId == rec.Id).OrderBy(a => a.Ordinal).ToListAsync();
    Assert.Equal(["Send the report", "Book the room"], actions.Select(a => a.Text));
    Assert.Equal("Ada", actions[0].Actor);                     // the live row is untouched
    Assert.True(actions[0].Pinned);
    Assert.False(actions[1].Pinned);                           // extracted rows arrive unpinned, as before
    Assert.Equal(ActionSource.Extracted, actions[1].Source);
    Assert.Equal(1, actions[1].Ordinal);
    Assert.Equal(["Send the report"], client.LastAlreadyRecorded);
    Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.ActionsExtractedAt);
}

[Fact]
public async Task ProcessAsync_WithNoLiveActions_PassesNothingAlreadyRecorded()
{
    using var db = TestDb.Create();
    var (rec, tr) = await Seed(db, Guid.NewGuid());
    var client = new FakeActionsClient { Result = { new ExtractedAction("Book the room", "", "") } };

    await ActionsProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
        Job(rec, tr), Template, NullLogger.Instance, new CapturingWebhookPublisher(), "");

    Assert.Empty(client.LastAlreadyRecorded ?? []);
    Assert.Single(await db.RecordingActions.Where(a => a.RecordingId == rec.Id).ToListAsync());
}
```

In `RecordingAiWebhookEmitTests`, find the existing `action_items_ready` test (Grep `RecordingActionItemsReady`) and add a sibling that seeds one pinned `Source = Live` action before processing, has the fake return one different action, and asserts the published payload's `count` is `2` and both texts appear. Copy that file's existing pattern for reading the captured payload (it serialises the anonymous object, see how the existing test reads `actionItems`).

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsProcessorTests|FullyQualifiedName~RecordingAiWebhookEmitTests"`
Expected: `ProcessAsync_KeepsLiveActions...` FAILS (the live row is removed by `RemoveRange`, so the text list is `["Send the report.", "Book the room"]`); the webhook test fails on `count`.

- [ ] **Step 3: Implement**

In `ActionsProcessor.ProcessAsync`, replace from `var extracted = await client.ExtractAsync(...)` through `db.RecordingActions.AddRange(newActions);` with:

```csharp
            // Actions recorded live during the meeting are already on the recording (pinned). Extraction adds to
            // them rather than replacing them: the model is told what is there, and exact repeats are dropped as a
            // backstop. Only live rows are listed - this path only runs when ActionsExtractedAt is null, so there
            // is nothing else a user could have curated yet.
            var existingTexts = rec.Actions.OrderBy(a => a.Ordinal).Select(a => a.Text).ToList();
            var extracted = await client.ExtractAsync(
                cfg, segs, template, rec.StartedAt ?? rec.CreatedAt, existingTexts, ct);

            var ordinal = rec.Actions.Count == 0 ? 0 : rec.Actions.Max(a => a.Ordinal) + 1;
            var newActions = ActionMerge.WithoutDuplicates(extracted, existingTexts).Select(e => new RecordingAction
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Text = e.Text,
                Actor = e.Actor,
                Deadline = e.Deadline,
                Ordinal = ordinal++,
            }).ToList();
            db.RecordingActions.AddRange(newActions);
```

Change the webhook call to publish the whole list:

```csharp
            await PublishActionItemsReadyAsync(
                db, webhooks, publicUrl, rec, rec.Actions.OrderBy(a => a.Ordinal).Concat(newActions).ToList(), logger, ct);
```

(`rec.Actions` is the tracked collection loaded by the `Include`; after `SaveChangesAsync` EF fixup may already have added `newActions` to it. Guard against a double count: build the list as `rec.Actions.Where(a => !newActions.Contains(a)).OrderBy(a => a.Ordinal).Concat(newActions).ToList()`. The webhook test's `count == 2` assertion is what proves this is right.)

Update the comment block above `ProcessAsync`'s skip check only if it now reads wrong; it still describes the skip correctly.

- [ ] **Step 4: Run and watch them pass**

Run: the Step 2 command. Expected: all tests in both classes pass, including the pre-existing ones (`ProcessAsync_SkipsWhenAlreadyExtracted_DoesNotClobberUserEdits` must still pass unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/ActionsProcessor.cs tests/Diariz.Api.Tests/ActionsProcessorTests.cs tests/Diariz.Api.Tests/RecordingAiWebhookEmitTests.cs
git commit -m "feat(actions): extraction merges with actions recorded during the meeting"
```

---

### Task 5: Re-extract keeps pinned actions

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingActionsController.cs:63-126` (Extract) and the `Create` description (line ~134: "a later extraction replaces the whole list")
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json` key `confirmReextract`
- Regenerate: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` and `generated/index.ts`
- Test: `tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Extract_KeepsPinnedActions_ReplacesTheRest_AndSkipsRepeatsOfWhatItKept()
{
    using var db = TestDb.Create();
    var userId = Guid.NewGuid();
    var rec = await SeedTranscribed(db, userId);
    db.RecordingActions.AddRange(
        new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true, Source = ActionSource.Live },
        new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Old unpinned", Ordinal = 1 });
    await db.SaveChangesAsync();
    var client = new FakeActionsClient
    {
        Result = { new ExtractedAction("book the room", "", ""), new ExtractedAction("Send the report", "Bob", "") },
    };

    var result = (await Build(db, userId, client).Extract(rec.Id)).Value!;

    var all = await db.RecordingActions.Where(a => a.RecordingId == rec.Id).OrderBy(a => a.Ordinal).ToListAsync();
    Assert.Equal(["Book the room", "Send the report"], all.Select(a => a.Text));
    Assert.Equal(["Book the room"], client.LastAlreadyRecorded);
    Assert.Equal(["Book the room", "Send the report"], result.Select(a => a.Text)); // the full list, not just the new rows
}
```

Check the existing `Extract_ReplacesActions_SetsFlag_AndReturnsOrderedList`: if it seeds only unpinned actions it keeps passing; if it seeds a pinned one, it now encodes the old behaviour and must be updated to the new rule in this step (say so in the commit message).

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingActionsControllerTests"`
Expected: the new test FAILS (`Book the room` removed by `RemoveRange`).

- [ ] **Step 3: Implement**

Replace from `var extracted = await _client.ExtractAsync(...)` to `return fresh.Select(ToDto).ToList();` with:

```csharp
        // Pinned actions are ones someone adopted (live during the meeting, or by pinning later). A re-run must
        // not throw that away, so it replaces only the unpinned rows and is told what it is keeping.
        var kept = rec.Actions.Where(a => a.Pinned).OrderBy(a => a.Ordinal).ToList();
        var keptTexts = kept.Select(a => a.Text).ToList();
        var extracted = await _client.ExtractAsync(cfg, segs, template, rec.StartedAt ?? rec.CreatedAt, keptTexts);

        _db.RecordingActions.RemoveRange(rec.Actions.Where(a => !a.Pinned));
        var ordinal = kept.Count == 0 ? 0 : kept.Max(a => a.Ordinal) + 1;
        var fresh = ActionMerge.WithoutDuplicates(extracted, keptTexts).Select(e => new RecordingAction
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            Text = e.Text,
            Actor = e.Actor,
            Deadline = e.Deadline,
            Ordinal = ordinal++,
        }).ToList();
        _db.RecordingActions.AddRange(fresh);
        rec.ActionsExtractedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return kept.Concat(fresh).Select(ToDto).ToList();
```

Update the XML summary above `Extract` ("replace the recording's action list") and its `EndpointDescription` second paragraph to:

```
"It **replaces every unpinned action**, so unpinned items you added or edited by hand are discarded, " +
"including their completion state. **Pinned actions are kept**, and the extraction skips anything that " +
"repeats them. Returns the full list afterwards. Returns 404 when the " +
```

(keep the rest of the sentence). In `Create`'s description change "Note that a later extraction replaces the whole list, including anything added this way." to "Note that a later extraction replaces every unpinned action, including anything added this way unless it is pinned."

Locale `confirmReextract`:
- en: `"Replace the unpinned actions with a fresh extraction? Pinned actions are kept."`
- de: `"Die nicht angehefteten Aktionen durch eine neue Extraktion ersetzen? Angeheftete Aktionen bleiben erhalten."`
- es: `"¿Reemplazar las acciones no fijadas por una nueva extracción? Las acciones fijadas se conservan."`
- fr: `"Remplacer les actions non épinglées par une nouvelle extraction ? Les actions épinglées sont conservées."`

(Check the existing pin wording in each catalog (`pinActionAria`) and match its verb if it differs from the above.)

- [ ] **Step 4: Run tests, regenerate the API document**

Run: the Step 2 command. Expected: PASS.

Run the OpenAPI snapshot test twice (it rewrites its snapshot on run 1): `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"`. Run 1 FAILS and rewrites; run 2 PASSES.
Then `cd integrations/n8n-nodes-diariz && npm run generate && npm test`. Expected: generated `index.ts` gains the live-actions operation and the tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/RecordingActionsController.cs tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json integrations/n8n-nodes-diariz/nodes/Diariz/generated/
git commit -m "feat(actions): re-extracting keeps pinned actions"
```
(Also add the OpenAPI snapshot path the test rewrote, if it lives outside `integrations/`: check `git status`.)

---

### Task 6: Web types and the stream builder

**Files:**
- Modify: `apps/web/src/lib/types.ts` (after `MeetingNote`, line ~507)
- Modify: `apps/web/src/lib/notesStream.ts`
- Test: `apps/web/src/lib/notesStream.test.ts`

**Interfaces:**
- Produces:
  - `export type LineKind = "note" | "action";`
  - `export interface LiveNoteLine extends MeetingNote { kind?: LineKind; actor?: string; deadline?: string }` (absent `kind` = note)
  - `StreamFilter = "all" | "notes" | "actions" | "captures"`
  - `StreamItem` gains `{ kind: "action"; id: string; atMs: number; note: LiveNoteLine }` (id prefix `a:`); the `note` variant's `note` becomes `LiveNoteLine`
  - `StreamInput.lines: LiveNoteLine[]`
  - `streamCounts({ lines, shots }): { notes: number; actions: number; captures: number }`

- [ ] **Step 1: Write the failing tests** (in `notesStream.test.ts`, using its `note`/`build` factories; widen the `note` factory's parameter to `Partial<LiveNoteLine>` if it is typed `Partial<MeetingNote>`)

```ts
describe("actions", () => {
  it("files an action line as its own kind, on the same timeline as notes", () => {
    const items = build({
      lines: [note({ id: "1", capturedAtMs: 20_000 }), note({ id: "2", capturedAtMs: 10_000, kind: "action" })],
    });
    expect(items.map((i) => [i.kind, i.id])).toEqual([
      ["action", "a:2"],
      ["note", "n:1"],
    ]);
  });

  it("shows only actions under the actions filter, and only notes under notes", () => {
    const lines = [note({ id: "1" }), note({ id: "2", kind: "action" })];
    expect(build({ lines, filter: "actions" }).map((i) => i.kind)).toEqual(["action"]);
    expect(build({ lines, filter: "notes" }).map((i) => i.kind)).toEqual(["note"]);
  });

  it("counts notes and actions separately", () => {
    const lines = [note({ id: "1" }), note({ id: "2", kind: "action" }), note({ id: "3", kind: "note" })];
    expect(streamCounts({ lines, shots: [] })).toEqual({ notes: 2, actions: 1, captures: 0 });
  });
});
```

Also update the existing `streamCounts` expectation(s) in this file to include `actions: 0`.

- [ ] **Step 2: Run and watch them fail**

Run: `cd apps/web && npx vitest run src/lib/notesStream.test.ts`
Expected: FAIL (`kind` "note" where "action" expected; counts object missing `actions`).

- [ ] **Step 3: Implement**

`types.ts`, after `MeetingNote`:

```ts
export type LineKind = "note" | "action";

/// A line in the live notes panel. Either a note or an action typed during the meeting. `kind` absent
/// means note, which is what keeps every `MeetingNote` and every stash written before actions existed valid.
export interface LiveNoteLine extends MeetingNote {
  kind?: LineKind;
  /// Actions only: who owns it (free text, like `RecordingAction.actor`) and when it is due.
  actor?: string;
  deadline?: string;
}
```

`notesStream.ts`:
- import `LiveNoteLine` instead of `MeetingNote`;
- `export type StreamFilter = "all" | "notes" | "actions" | "captures";`
- `StreamItem`: `| { kind: "note"; id: string; atMs: number; note: LiveNoteLine } | { kind: "action"; id: string; atMs: number; note: LiveNoteLine }`;
- `StreamInput.lines: LiveNoteLine[]`;
- replace the `notes` block and the `adopted`/`stamped` split with:

```ts
  const isAction = (l: LiveNoteLine) => l.kind === "action";
  const wanted = (l: LiveNoteLine) =>
    filter === "all" || (filter === "notes" && !isAction(l)) || (filter === "actions" && isAction(l));

  const notes: StreamItem[] = lines.filter(wanted).map((note) =>
    isAction(note)
      ? { kind: "action", id: `a:${note.id}`, atMs: note.capturedAtMs ?? 0, note }
      : {
          kind: "note",
          id: `n:${note.id}`,
          // A line adopted from a pre-meeting stash has no recorded moment. It belongs at the top - it
          // was written before anything else in the list - and `null` would sort nowhere sensible.
          atMs: note.capturedAtMs ?? 0,
          note,
        },
  );

  // Split out ahead of the sort rather than given a sentinel stamp - see the note on `atMs` above.
  const adopted = notes.filter((n) => (n.kind === "note" || n.kind === "action") && n.note.capturedAtMs === null);
  const stamped = notes.filter((n) => (n.kind === "note" || n.kind === "action") && n.note.capturedAtMs !== null);
```

- `streamCounts`:

```ts
export function streamCounts({ lines, shots }: { lines: LiveNoteLine[]; shots: ShotView[] }): {
  notes: number;
  actions: number;
  captures: number;
} {
  const actions = lines.filter((l) => l.kind === "action").length;
  return { notes: lines.length - actions, actions, captures: shots.length };
}
```

- [ ] **Step 4: Run and watch them pass; typecheck**

Run: `npx vitest run src/lib/notesStream.test.ts` then `npx tsc --noEmit -p .`
Expected: tests PASS. `tsc` may report `LiveNotesStream.tsx` narrowing errors where it falls through to `CaptureRow` for non-note kinds. Fix those in Task 9, but make the build green now with the minimal change: in `LiveNotesStream.tsx`, change `if (item.kind === "note")` to `if (item.kind === "note" || item.kind === "action")` (Task 9 gives actions their own row).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/notesStream.ts apps/web/src/lib/notesStream.test.ts apps/web/src/components/hub/LiveNotesStream.tsx
git commit -m "feat(web): actions as a kind of line in the live notes stream"
```

---

### Task 7: The hook and its stash carry actions

**Files:**
- Modify: `apps/web/src/lib/pendingNotes.ts:10-21`
- Modify: `apps/web/src/lib/useLiveNotes.ts`
- Test: `apps/web/src/lib/useLiveNotes.test.tsx`, `apps/web/src/lib/pendingNotes.test.ts`

**Interfaces:**
- Consumes: `LiveNoteLine`, `LineKind` (Task 6).
- Produces:
  - `PendingNoteLine { text: string; capturedAtMs: number | null; kind?: LineKind; actor?: string; deadline?: string }`
  - `useLiveNotes({ userId, stampMs, defaultActor })` where `defaultActor: () => string`
  - `LiveNotes.lines: LiveNoteLine[]`, `snapshot(): LiveNoteLine[]`
  - `LiveNotes.add(text: string, atMs?: number, kind?: LineKind): void`
  - `LiveNotes.setKind(id: string, kind: LineKind): void`
  - `LiveNotes.updateAction(id: string, patch: { actor?: string; deadline?: string }): void`

- [ ] **Step 1: Write the failing tests**

In `useLiveNotes.test.tsx`, make `Harness` pass `defaultActor={() => "Ada Lovelace"}` through to the hook (add a prop with that default), then:

```tsx
it("files an action owned by the default actor", () => {
  render(<Harness />);
  stamp = 5_000;
  act(() => api.add("book the room", undefined, "action"));
  const [line] = api.snapshot();
  expect(line.kind).toBe("action");
  expect(line.actor).toBe("Ada Lovelace");
  expect(line.capturedAtMs).toBe(5_000);
});

it("promotes a note to an action, filling the owner only if it has none, and back", () => {
  render(<Harness />);
  act(() => api.add("chase the invoice"));
  const id = api.snapshot()[0].id;
  act(() => api.setKind(id, "action"));
  expect(api.snapshot()[0]).toMatchObject({ kind: "action", actor: "Ada Lovelace" });
  act(() => api.updateAction(id, { actor: "Grace", deadline: "Friday" }));
  act(() => api.setKind(id, "note"));
  act(() => api.setKind(id, "action"));
  expect(api.snapshot()[0]).toMatchObject({ kind: "action", actor: "Grace", deadline: "Friday" });
});

it("mirrors kind, owner and due date into the stash", () => {
  render(<Harness />);
  act(() => api.add("book the room", undefined, "action"));
  const saved = vi.mocked(savePendingNotes).mock.calls.at(-1)![0];
  expect(saved.lines[0]).toEqual({ text: "book the room", capturedAtMs: expect.any(Number), kind: "action", actor: "Ada Lovelace", deadline: undefined });
});
```

(Adjust the `savePendingNotes` import/mocked access to match how the file's `vi.mock("./pendingNotes", ...)` exposes it.)

In `pendingNotes.test.ts`:

```ts
it("round-trips an action line and still reads a line with no kind as a note", async () => {
  await savePendingNotes({
    userId: "u2", recordingId: null, updatedAt: 1,
    lines: [{ text: "old note", capturedAtMs: 1 }, { text: "book the room", capturedAtMs: 2, kind: "action", actor: "Ada" }],
  });
  const loaded = await loadPendingNotes("u2");
  expect(loaded?.lines[0].kind).toBe(undefined);
  expect(loaded?.lines[1]).toEqual({ text: "book the room", capturedAtMs: 2, kind: "action", actor: "Ada" });
});
```

- [ ] **Step 2: Run and watch them fail**

Run: `npx vitest run src/lib/useLiveNotes.test.tsx src/lib/pendingNotes.test.ts`
Expected: FAIL (`setKind is not a function`; `kind` undefined; the pendingNotes test fails to typecheck-free compile only at tsc, vitest may pass it. That is fine, it guards the type and the stored shape.)

- [ ] **Step 3: Implement**

`pendingNotes.ts` `PendingNoteLine`:

```ts
export interface PendingNoteLine {
  text: string;
  capturedAtMs: number | null;
  /// Absent on every line stashed before actions existed, which is read as a note.
  kind?: LineKind;
  actor?: string;
  deadline?: string;
}
```
(import `type { LineKind } from "./types"`.)

`useLiveNotes.ts`:
- types: `MeetingNote` to `LiveNoteLine`, import `LineKind`;
- add to the options: `/// Who a new action belongs to until someone says otherwise: the signed-in user. A function, like stampMs, so the hook never caches a name across a sign-in.` `defaultActor: () => string;`
- `mirror` maps `lines: next.map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs, kind: l.kind, actor: l.actor, deadline: l.deadline }))`;
- `add(text, atMs?, kind: LineKind = "note")` builds the line with `...(kind === "action" ? { kind, actor: defaultActor() } : {})`;
- new members:

```ts
    setKind(id: string, kind: LineKind) {
      mirror(
        linesRef.current.map((l) =>
          l.id !== id ? l
            // An owner someone typed survives a round trip through "note"; only a line that never had one
            // is given the default.
            : kind === "action" ? { ...l, kind, actor: l.actor ?? defaultActor() }
            : { ...l, kind },
        ),
      );
    },

    updateAction(id: string, patch: { actor?: string; deadline?: string }) {
      mirror(linesRef.current.map((l) => (l.id === id ? { ...l, ...patch } : l)));
    },
```

- add both to the `LiveNotes` interface with doc comments, and update `add`'s signature there.

- [ ] **Step 4: Run and watch them pass; typecheck**

Run: the Step 2 command, then `npx tsc --noEmit -p .`. Expected: tests PASS. `tsc` flags `Recorder.tsx` for the missing `defaultActor`; add it now so the build stays green:

```ts
  const notes = useLiveNotes({
    userId,
    stampMs: () => timing.elapsedMs(timingRef.current, Date.now()),
    // The signed-in user owns what they type, until they say otherwise.
    defaultActor: () => fullNameFromToken(getToken()) ?? "",
  });
```
(import `fullNameFromToken` from `../lib/jwt` beside `userIdFromToken`.)

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/pendingNotes.ts apps/web/src/lib/pendingNotes.test.ts apps/web/src/lib/useLiveNotes.ts apps/web/src/lib/useLiveNotes.test.tsx apps/web/src/components/Recorder.tsx
git commit -m "feat(web): live notes hook files, promotes and edits actions"
```

---

### Task 8: The pop-out channel carries action commands

**Files:**
- Modify: `apps/web/src/lib/notesChannel.ts`
- Modify: `apps/web/src/lib/useNotesPopout.ts:37-51`
- Test: `apps/web/src/lib/notesChannel.test.ts`, `apps/web/src/lib/useNotesPopout.test.tsx`

**Interfaces:**
- Consumes: `LineKind`, `LiveNoteLine` (Task 6).
- Produces:
  - `NotesState.lines: LiveNoteLine[]`
  - `NotesHostHandlers.onAdd(text: string, atMs?: number, kind?: LineKind)`, `onSetKind(id: string, kind: LineKind)`, `onUpdateAction(id: string, patch: { actor?: string; deadline?: string })`
  - `NotesClient.add(text, atMs?, kind?)`, `setKind(id, kind)`, `updateAction(id, patch)`

- [ ] **Step 1: Write the failing tests** (in `notesChannel.test.ts`, using `makeBus`/`noopHandlers`; add `onSetKind: () => {}, onUpdateAction: () => {}` to `noopHandlers`)

```ts
it("relays action commands from the pop-out to the host", () => {
  const channel = makeBus();
  const onAdd = vi.fn();
  const onSetKind = vi.fn();
  const onUpdateAction = vi.fn();
  createNotesHost({ ...noopHandlers, onAdd, onSetKind, onUpdateAction, getState: () => state() }, { channel: channel() });
  const client = createNotesClient({ onState: vi.fn(), onEnded: vi.fn(), onDisconnected: vi.fn() }, { channel: channel() });

  client.add("book the room", undefined, "action");
  client.setKind("n1", "action");
  client.updateAction("n1", { actor: "Grace", deadline: "Friday" });

  expect(onAdd).toHaveBeenCalledWith("book the room", undefined, "action");
  expect(onSetKind).toHaveBeenCalledWith("n1", "action");
  expect(onUpdateAction).toHaveBeenCalledWith("n1", { actor: "Grace", deadline: "Friday" });
});
```

If `FakeChannel` delivers asynchronously in that file, follow the existing test's pattern for flushing before asserting.

In `useNotesPopout.test.tsx`, find the test that checks handlers are forwarded to the host (Grep `onAdd`) and extend it the same way for `onSetKind` and `onUpdateAction`.

- [ ] **Step 2: Run and watch them fail**

Run: `npx vitest run src/lib/notesChannel.test.ts src/lib/useNotesPopout.test.tsx`
Expected: FAIL (`client.setKind is not a function`).

- [ ] **Step 3: Implement**

`notesChannel.ts`:
- `NotesState.lines: LiveNoteLine[]` (import the types);
- `ClientMessage`: change add to `{ type: "add"; text: string; atMs?: number; kind?: LineKind }` and add `| { type: "setKind"; id: string; kind: LineKind } | { type: "updateAction"; id: string; actor?: string; deadline?: string }`;
- `NotesHostHandlers`: `onAdd(text: string, atMs?: number, kind?: LineKind): void;` plus
  `/// Turn a note into an action or back. Both windows show the same line, so this is addressed by id.` `onSetKind(id: string, kind: LineKind): void;` and `onUpdateAction(id: string, patch: { actor?: string; deadline?: string }): void;`
- host switch: `case "add": handlers.onAdd(m.text, m.atMs, m.kind); break;` `case "setKind": handlers.onSetKind(m.id, m.kind); break;` `case "updateAction": handlers.onUpdateAction(m.id, { actor: m.actor, deadline: m.deadline }); break;`
- `NotesClient` interface + `createNotesClient` return object: follow how `add`/`edit` post their messages today (read the body below line 235) and add `add(text, atMs, kind)` posting `kind`, `setKind(id, kind)` posting `{ type: "setKind", id, kind }`, `updateAction(id, patch)` posting `{ type: "updateAction", id, ...patch }`.

`updateAction`'s host call passes `{ actor: m.actor, deadline: m.deadline }`. Both keys may be `undefined`, and the hook spreads the patch, so an undefined key would **overwrite** a real value. Make `useLiveNotes.updateAction` skip undefined keys:

```ts
    updateAction(id: string, patch: { actor?: string; deadline?: string }) {
      const clean = Object.fromEntries(Object.entries(patch).filter(([, v]) => v !== undefined));
      mirror(linesRef.current.map((l) => (l.id === id ? { ...l, ...clean } : l)));
    },
```

and add a test to `useLiveNotes.test.tsx` proving it: `updateAction(id, { actor: "Grace" })` then `updateAction(id, { deadline: "Friday", actor: undefined })` leaves `actor === "Grace"`. Watch that test fail against the Task 7 implementation first.

`useNotesPopout.ts`: forward `onAdd: (text, atMs, kind) => handlersRef.current.onAdd(text, atMs, kind)`, `onSetKind: (id, kind) => handlersRef.current.onSetKind(id, kind)`, `onUpdateAction: (id, patch) => handlersRef.current.onUpdateAction(id, patch)`.

- [ ] **Step 4: Run and typecheck**

Run: the Step 2 command plus `npx vitest run src/lib/useLiveNotes.test.tsx`, then `npx tsc --noEmit -p .`
Expected: PASS. `tsc` flags `Recorder.tsx`'s `useNotesPopout` handlers; add `onSetKind: notes.setKind, onUpdateAction: notes.updateAction` there now.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/notesChannel.ts apps/web/src/lib/notesChannel.test.ts apps/web/src/lib/useNotesPopout.ts apps/web/src/lib/useNotesPopout.test.tsx apps/web/src/lib/useLiveNotes.ts apps/web/src/lib/useLiveNotes.test.tsx apps/web/src/components/Recorder.tsx
git commit -m "feat(web): pop-out notes window can file and edit actions"
```

---

### Task 9: Composer toggle, Actions chip and action rows

**Files:**
- Modify: `apps/web/src/components/hub/notesStreamRows.tsx` (`NoteRow`, new `ActionRow`)
- Modify: `apps/web/src/components/hub/LiveNotesStream.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/components/hub/LiveNotesStream.test.tsx`

**Interfaces:**
- Consumes: `LiveNoteLine`, `LineKind`, `StreamFilter` with `actions`, `streamCounts().actions` (Task 6).
- Produces: `LiveNotesStreamProps` changes: `lines: LiveNoteLine[]`; `onAdd: (text: string, atMs?: number, kind?: LineKind) => void`; new required `onSetKind: (id: string, kind: LineKind) => void`; new required `onUpdateAction: (id: string, patch: { actor?: string; deadline?: string }) => void`. `ActionRow` props: `{ note: LiveNoteLine; stampColumnPx: number; onEdit; onDelete; onSetKind; onUpdateAction }`. Test ids: `stream-action`, `composer-kind`.

New `workspace` keys (en / de / es / fr):

| key | en | de | es | fr |
|---|---|---|---|---|
| `notesComposerActionPlaceholder` | `Add an action...` | `Aktion hinzufügen...` | `Añade una acción...` | `Ajouter une action...` |
| `notesKindAction` | `Action` | `Aktion` | `Acción` | `Action` |
| `notesKindToggleHint` | `Record as an action (Alt+A)` | `Als Aktion erfassen (Alt+A)` | `Registrar como acción (Alt+A)` | `Enregistrer comme action (Alt+A)` |
| `notesFilterActions` | `Actions {{n}}` | `Aktionen {{n}}` | `Acciones {{n}}` | `Actions {{n}}` |
| `notesFilterActionsOnly` | `actions only` | `nur Aktionen` | `solo acciones` | `actions seulement` |
| `notesMakeAction` | `Make action` | `Zur Aktion machen` | `Convertir en acción` | `Transformer en action` |
| `notesMakeNote` | `Make note` | `Zur Notiz machen` | `Convertir en nota` | `Transformer en note` |
| `notesActionOwner` | `Owner` | `Verantwortlich` | `Responsable` | `Responsable` |
| `notesActionDue` | `Due` | `Fällig` | `Vence` | `Échéance` |
| `notesActionNoOwner` | `No owner` | `Niemand zugewiesen` | `Sin responsable` | `Sans responsable` |
| `notesActionDetails` | `Owner and due date` | `Verantwortlich und Fälligkeit` | `Responsable y fecha` | `Responsable et échéance` |
| `notesActionsEmpty` | `No actions yet. Switch the composer to Action to record one - it is tracked in Actions straight away.` | `Noch keine Aktionen. Stellen Sie das Eingabefeld auf Aktion um - sie wird sofort unter Aktionen verfolgt.` | `Aún no hay acciones. Cambia el campo a Acción para registrar una: se sigue en Acciones al momento.` | `Pas encore d'actions. Passez le champ sur Action pour en noter une : elle est suivie dans Actions tout de suite.` |

- [ ] **Step 1: Write the failing tests** (in `LiveNotesStream.test.tsx`; add `onSetKind: vi.fn(), onUpdateAction: vi.fn()` to `base`)

```tsx
describe("actions", () => {
  const kindToggle = () => screen.getByTestId("composer-kind") as HTMLButtonElement;

  it("files an action when the toggle is on, then drops back to notes", () => {
    const onAdd = vi.fn();
    renderStream({ elapsedMs: 61_000, onAdd });
    fireEvent.click(kindToggle());
    expect(kindToggle().getAttribute("aria-pressed")).toBe("true");
    expect(composer().placeholder).toBe("Add an action...");
    fireEvent.change(composer(), { target: { value: "book the room" } });
    fireEvent.keyDown(composer(), { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("book the room", undefined, "action");
    expect(kindToggle().getAttribute("aria-pressed")).toBe("false");
  });

  it("toggles with Alt+A in the composer without typing into it", () => {
    renderStream();
    fireEvent.keyDown(composer(), { key: "a", altKey: true });
    expect(kindToggle().getAttribute("aria-pressed")).toBe("true");
    expect(composer().value).toBe("");
  });

  it("files a note as a note with no toggle", () => {
    const onAdd = vi.fn();
    renderStream({ onAdd });
    fireEvent.change(composer(), { target: { value: "a thought" } });
    fireEvent.keyDown(composer(), { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("a thought", undefined, "note");
  });

  it("renders an action row with its owner, and turns it back into a note", () => {
    const onSetKind = vi.fn();
    renderStream({ lines: [note({ id: "x", text: "book the room", kind: "action", actor: "Ada" })], onSetKind });
    const row = screen.getByTestId("stream-action");
    expect(row.textContent).toContain("book the room");
    expect(row.textContent).toContain("Ada");
    fireEvent.click(screen.getByRole("button", { name: "Make note" }));
    expect(onSetKind).toHaveBeenCalledWith("x", "note");
  });

  it("offers Make action on a note row", () => {
    const onSetKind = vi.fn();
    renderStream({ lines: [note({ id: "y", text: "chase the invoice" })], onSetKind });
    fireEvent.click(screen.getByRole("button", { name: "Make action" }));
    expect(onSetKind).toHaveBeenCalledWith("y", "action");
  });

  it("edits an action's owner and due date", () => {
    const onUpdateAction = vi.fn();
    renderStream({ lines: [note({ id: "z", text: "send the deck", kind: "action", actor: "Ada" })], onUpdateAction });
    fireEvent.click(screen.getByRole("button", { name: "Owner and due date" }));
    fireEvent.change(screen.getByLabelText("Owner"), { target: { value: "Grace" } });
    fireEvent.change(screen.getByLabelText("Due"), { target: { value: "Friday" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(onUpdateAction).toHaveBeenCalledWith("z", { actor: "Grace", deadline: "Friday" });
  });

  it("shows an Actions chip with its count, filtering to actions", () => {
    renderStream({ lines: [note({ id: "1" }), note({ id: "2", kind: "action" })] });
    fireEvent.click(screen.getByRole("radio", { name: "Actions 1" }));
    expect(screen.queryAllByTestId("stream-note").length).toBe(0);
    expect(screen.getAllByTestId("stream-action").length).toBe(1);
  });
});
```

Update the existing composer test "files a note at the running clock and clears the box" to expect `toHaveBeenCalledWith("a thought", undefined, "note")`.

- [ ] **Step 2: Run and watch them fail**

Run: `npx vitest run src/components/hub/LiveNotesStream.test.tsx`
Expected: FAIL (`Unable to find an element by: [data-testid="composer-kind"]`).

- [ ] **Step 3: Implement**

**Locales:** add the table's keys to all four `workspace.json` files next to the existing `notesComposerPlaceholder` block.

**`notesStreamRows.tsx`:**
- import `LiveNoteLine`, `LineKind`; `NoteRow`'s `note` prop becomes `LiveNoteLine` and it gains `onSetKind?: (id: string, kind: LineKind) => void`. In view mode, before the edit `RowButton`, render:

```tsx
          {onSetKind && (
            <RowButton label={t("notesMakeAction")} onClick={() => onSetKind(note.id, "action")}>
              <IconCheck size={12} />
            </RowButton>
          )}
```
(import `IconCheck` from `./hubGlyphs`.)

- New `ActionRow`, placed after `NoteRow`. It reuses `NoteRow`'s layout and `pillButton`, with a green left rail and a details editor:

```tsx
export function ActionRow({
  note,
  stampColumnPx,
  onEdit,
  onDelete,
  onSetKind,
  onUpdateAction,
}: {
  note: LiveNoteLine;
  stampColumnPx: number;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onSetKind: (id: string, kind: LineKind) => void;
  onUpdateAction: (id: string, patch: { actor?: string; deadline?: string }) => void;
}) {
  const { t } = useTranslation("workspace");
  const [mode, setMode] = useState<"view" | "text" | "details">("view");
  const [draft, setDraft] = useState("");
  const [actor, setActor] = useState("");
  const [deadline, setDeadline] = useState("");

  const field: React.CSSProperties = {
    minWidth: 0, flex: 1, border: "1px solid var(--hub-field-border)", borderRadius: 6,
    background: "var(--hub-surface)", color: "var(--hub-text)", fontSize: 13, padding: "2px 6px",
  };

  return (
    <li
      data-testid="stream-action"
      className="hub-note-row hub-stream-row"
      style={{
        display: "flex", alignItems: "flex-start", gap: 8, padding: "5px 4px 5px 0", borderRadius: 7,
        boxShadow: "inset 2px 0 0 var(--hub-green)",
      }}
    >
      <Stamp ms={note.capturedAtMs ?? 0} widthPx={stampColumnPx} color="var(--hub-green-text)" weight={600} />
      {mode === "text" ? (
        <span style={{ display: "flex", minWidth: 0, flex: 1, alignItems: "center", gap: 4 }}>
          <input value={draft} onChange={(e) => setDraft(e.target.value)} aria-label={t("notesEdit")} autoFocus style={field} />
          <button type="button" style={pillButton} onClick={() => { onEdit(note.id, draft.trim()); setMode("view"); }}>
            {t("notesSave")}
          </button>
          <button type="button" style={pillButton} onClick={() => setMode("view")}>{t("notesCancel")}</button>
        </span>
      ) : mode === "details" ? (
        <span style={{ display: "flex", minWidth: 0, flex: 1, flexWrap: "wrap", alignItems: "center", gap: 4 }}>
          <input value={actor} onChange={(e) => setActor(e.target.value)} aria-label={t("notesActionOwner")}
            placeholder={t("notesActionOwner")} autoFocus style={field} />
          <input value={deadline} onChange={(e) => setDeadline(e.target.value)} aria-label={t("notesActionDue")}
            placeholder={t("notesActionDue")} style={field} />
          <button type="button" style={pillButton}
            onClick={() => { onUpdateAction(note.id, { actor: actor.trim(), deadline: deadline.trim() }); setMode("view"); }}>
            {t("notesSave")}
          </button>
          <button type="button" style={pillButton} onClick={() => setMode("view")}>{t("notesCancel")}</button>
        </span>
      ) : (
        <>
          <span style={{ minWidth: 0, flex: 1, fontSize: 13, lineHeight: 1.6, fontWeight: 500, color: "var(--hub-text)", wordBreak: "break-word" }}>
            {note.text}
            <span style={{ display: "block", fontSize: 11, color: "var(--hub-muted)" }}>
              {note.actor || t("notesActionNoOwner")}
              {note.deadline ? ` - ${note.deadline}` : ""}
            </span>
          </span>
          <RowButton label={t("notesActionDetails")}
            onClick={() => { setActor(note.actor ?? ""); setDeadline(note.deadline ?? ""); setMode("details"); }}>
            <IconArrowRight size={12} />
          </RowButton>
          <RowButton label={t("notesEdit")} onClick={() => { setDraft(note.text); setMode("text"); }}>
            <IconPencil size={12} />
          </RowButton>
          <RowButton label={t("notesMakeNote")} onClick={() => onSetKind(note.id, "note")}>
            <IconClose size={10} />
          </RowButton>
          <RowButton label={t("notesDelete")} onClick={() => onDelete(note.id)} className="hub-row-delete">
            <IconClose size={12} />
          </RowButton>
        </>
      )}
    </li>
  );
}
```

Check `apps/web/src/index.css` (or wherever `--hub-blue-text` is defined, Grep `--hub-blue-text`) for green tokens. If `--hub-green`/`--hub-green-text` do not exist, add them beside the blue ones in **both** the light and dark definitions, copying the blue pair's structure. Pick greens that read against the same backgrounds. Then check the result in the running app (Task 10, Step 6); jsdom cannot.

**`LiveNotesStream.tsx`:**
- props: `lines: LiveNoteLine[]`, `onAdd: (text: string, atMs?: number, kind?: LineKind) => void`, add `onSetKind` and `onUpdateAction` with doc comments; destructure them.
- state: `const [kind, setKind] = useState<LineKind>("note");`
- `file()`:

```ts
  function file() {
    const text = draft.trim();
    if (!text || disabled) return;
    onAdd(text, pinnedAtMs ?? undefined, kind);
    setDraft("");
    setPinnedAtMs(null);
    // Back to Note after every action. Most lines are notes, and a toggle left on by mistake would file
    // the next thoughts as tracked actions in everyone's Actions tab.
    setKind("note");
  }
```

- input `onKeyDown`:

```tsx
                onKeyDown={(e) => {
                  if (e.altKey && e.key.toLowerCase() === "a") {
                    e.preventDefault();
                    setKind((k) => (k === "action" ? "note" : "action"));
                    return;
                  }
                  if (e.key !== "Enter") return;
                  e.preventDefault();
                  file();
                }}
```

- placeholder and aria-label: `kind === "action" ? t("notesComposerActionPlaceholder") : t("notesComposerPlaceholder")`. **Keep the aria-label fixed at `t("notesComposerPlaceholder")`**: every existing test finds the box with `getByLabelText(/note this moment/i)`, and the toggle's `aria-pressed` already announces the mode. Only the `placeholder` changes.
- toggle button, rendered immediately before the `<input>` inside the composer row:

```tsx
              <button
                type="button"
                data-testid="composer-kind"
                aria-pressed={kind === "action"}
                title={t("notesKindToggleHint")}
                onClick={() => { setKind((k) => (k === "action" ? "note" : "action")); inputRef.current?.focus(); }}
                disabled={disabled}
                style={{
                  flexShrink: 0, border: "1px solid var(--hub-field-border)", borderRadius: 6, fontSize: 11,
                  fontWeight: 600, padding: "2px 6px", cursor: "pointer",
                  background: kind === "action" ? "var(--hub-green)" : "transparent",
                  color: kind === "action" ? "#fff" : "var(--hub-text-2)",
                }}
              >
                {t("notesKindAction")}
              </button>
```

- chips: insert `{ id: "actions", label: t("notesFilterActions", { n: counts.actions }) },` after the notes chip; extend the "only" hint at line ~336 with the `actions` case (`t("notesFilterActionsOnly")`) using the same shape as the notes case; extend `emptyMessage` (lines ~247-252) with `filter === "actions" ? t("notesActionsEmpty")`.
- rows: replace the Task 6 stopgap with:

```tsx
              if (item.kind === "note")
                return (
                  <NoteRow key={item.id} note={item.note} stampColumnPx={stampPx} onEdit={onEdit} onDelete={onDelete} onSetKind={onSetKind} />
                );
              if (item.kind === "action")
                return (
                  <ActionRow key={item.id} note={item.note} stampColumnPx={stampPx} onEdit={onEdit} onDelete={onDelete}
                    onSetKind={onSetKind} onUpdateAction={onUpdateAction} />
                );
```

- [ ] **Step 4: Run tests, the locale guards and typecheck**

Run: `npx vitest run src/components/hub/LiveNotesStream.test.tsx src/locales.test.ts src/lib/noFancyDashes.test.ts` then `npx tsc --noEmit -p .`
Expected: PASS, with no `act(...)` failures. `tsc` flags `NotesPopover.tsx` and `NotesPopout.tsx` for the new required props; those are Task 10. Commit only once Task 10 Step 3 makes `tsc` clean, or do Task 10 Step 3's two prop pass-throughs now.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/hub/notesStreamRows.tsx apps/web/src/components/hub/LiveNotesStream.tsx apps/web/src/components/hub/LiveNotesStream.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(web): record actions from the live notes composer"
```
(Include `index.css` if you added tokens.)

---

### Task 10: Wiring and attach-on-upload

**Files:**
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`, `apps/web/src/pages/NotesPopout.tsx:229-232`
- Modify: `apps/web/src/lib/api.ts` (after `createNotes`, line ~805)
- Modify: `apps/web/src/components/Recorder.tsx` (`attachNotes` 712-728, `<NotesPopover>` 1903-1909)
- Test: `apps/web/src/components/Recorder.test.tsx`, `apps/web/src/components/hub/NotesPopover.test.tsx`

**Interfaces:**
- Consumes: everything above.
- Produces: `api.createLiveActions(recordingId: string, actions: { text: string; actor?: string; deadline?: string; capturedAtMs?: number | null }[]): Promise<RecordingAction[]>` (POST `/api/recordings/${recordingId}/actions/live`, body `{ actions }`).

- [ ] **Step 1: Write the failing Recorder tests**

In `Recorder.test.tsx`, add `createLiveActions: vi.fn()` to the `vi.mock("../lib/api", ...)` factory. Then, beside the notes attach test at ~line 1150 (reuse its setup exactly: start recording, open the popover, stop):

```tsx
it("attaches actions to the live-actions endpoint and notes to the notes endpoint", async () => {
  // ...same setup as the "follow up with legal" test up to the composer...
  const box = screen.getByLabelText(/note this moment/i);
  fireEvent.change(box, { target: { value: "a plain note" } });
  fireEvent.keyDown(box, { key: "Enter" });
  fireEvent.click(screen.getByTestId("composer-kind"));
  fireEvent.change(box, { target: { value: "book the room" } });
  fireEvent.keyDown(box, { key: "Enter" });
  fireEvent.click(screen.getByRole("button", { name: /^stop$/i }));

  await waitFor(() =>
    expect(api.createLiveActions).toHaveBeenCalledWith("rec-new", [
      expect.objectContaining({ text: "book the room", capturedAtMs: expect.any(Number) }),
    ]),
  );
  expect(api.createNotes).toHaveBeenCalledWith("rec-new", [expect.objectContaining({ text: "a plain note" })]);
  expect(vi.mocked(api.createNotes).mock.calls[0][1]).toHaveLength(1);
});

it("when the actions attach fails after the notes landed, the retry stash holds only the actions", async () => {
  vi.mocked(api.createLiveActions).mockRejectedValueOnce(new Error("offline"));
  // ...same setup, one note and one action, stop...
  await waitFor(() => expect(savePendingNotes).toHaveBeenCalledWith(
    expect.objectContaining({ recordingId: "rec-new", lines: [expect.objectContaining({ text: "book the room", kind: "action" })] }),
  ));
});
```

The owner default comes from the token. `TOKEN`'s payload is `{ sub: "u1" }` with no name, so the default is `""`. Add one assertion that the attached action's `actor` is `""` rather than a crash or `undefined` being sent: `expect.objectContaining({ text: "book the room", actor: "" })`.

In `NotesPopover.test.tsx`, add `onSetKind`/`onUpdateAction` to its base props and one test that clicking "Make action" on a note row calls `onSetKind`, proving the pass-through.

- [ ] **Step 2: Run and watch them fail**

Run: `npx vitest run src/components/Recorder.test.tsx src/components/hub/NotesPopover.test.tsx`
Expected: FAIL (`createLiveActions` never called; the action went to `createNotes` with 2 lines).

- [ ] **Step 3: Implement**

`api.ts`, after `createNotes`:

```ts
  /// Actions recorded during the meeting. Created pinned, and they do not stop automatic extraction.
  async createLiveActions(
    recordingId: string,
    actions: { text: string; actor?: string; deadline?: string; capturedAtMs?: number | null }[],
  ): Promise<RecordingAction[]> {
    const { data } = await http.post<RecordingAction[]>(`/api/recordings/${recordingId}/actions/live`, { actions });
    return data;
  },
```

`NotesPopover.tsx`: `lines: LiveNoteLine[]`; `onAdd: (text: string, atMs?: number, kind?: LineKind) => void`; add `onSetKind` and `onUpdateAction` props (same types as `LiveNotesStreamProps`), destructure, and pass both to `<LiveNotesStream>`.

`NotesPopout.tsx`:

```tsx
            onAdd={(text, atMs, kind) => client?.add(text, atMs, kind)}
            onSetKind={(id, kind) => client?.setKind(id, kind)}
            onUpdateAction={(id, patch) => client?.updateAction(id, patch)}
```

`Recorder.tsx` `<NotesPopover>`: add `onSetKind={notes.setKind}` and `onUpdateAction={notes.updateAction}`.

`Recorder.tsx` `attachNotes`, replacing the body:

```ts
  async function attachNotes(recordingId: string, fromRetry?: PendingNotes) {
    let remaining: PendingNoteLine[] = fromRetry
      ? fromRetry.lines
      : notes.snapshot().map((l) => ({
          text: l.text, capturedAtMs: l.capturedAtMs, kind: l.kind, actor: l.actor, deadline: l.deadline,
        }));
    if (remaining.length === 0) {
      if (userId) void clearPendingNotes(userId);
      return;
    }
    try {
      // Two calls, and each part leaves `remaining` the moment it lands. A retry after a partial failure must
      // send only what is still missing, or the part that succeeded would be attached twice.
      const noteLines = remaining.filter((l) => l.kind !== "action");
      if (noteLines.length > 0) {
        await api.createNotes(recordingId, noteLines.map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs })));
        remaining = remaining.filter((l) => l.kind === "action");
      }
      if (remaining.length > 0) {
        await api.createLiveActions(
          recordingId,
          remaining.map((l) => ({ text: l.text, actor: l.actor ?? "", deadline: l.deadline ?? "", capturedAtMs: l.capturedAtMs })),
        );
        remaining = [];
      }
      await notes.reset();
      setNotesAttach(null);
    } catch {
      const stash: PendingNotes = { userId: userId ?? "", recordingId, lines: remaining, updatedAt: Date.now() };
      if (userId) await savePendingNotes(stash);
      setNotesAttach(stash);
    }
  }
```
(import `type PendingNoteLine` from `../lib/pendingNotes`.)

After the actions land, refresh any open Actions views. Find how the recorder invalidates queries after upload (Grep `invalidateQueries` in `Recorder.tsx`). If the recorder already invalidates the recordings list after `onUploaded`, also invalidate the actions list key used by the Actions tab (Grep `listAllActions` for its `queryKey`). If the recorder holds no `QueryClient`, skip this: the SignalR status nudge from extraction refetches, and the Actions tab refetches on mount.

- [ ] **Step 4: Run the whole web suite and typecheck**

Run: `npx tsc --noEmit -p . && npx vitest run`
Expected: all PASS, zero `act(...)` failures. If `RecorderPopoutPublish.test.tsx` counts published states and now differs, read why before changing its number. New handler identities must not cause extra publishes.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/api.ts apps/web/src/components/hub/NotesPopover.tsx apps/web/src/components/hub/NotesPopover.test.tsx apps/web/src/pages/NotesPopout.tsx apps/web/src/components/Recorder.tsx apps/web/src/components/Recorder.test.tsx
git commit -m "feat(web): attach live actions as pinned actions after upload"
```

- [ ] **Step 6: Verify in the running app**

Jsdom checks no geometry and no colour. Start the web dev server (`preview_start`; see memory `web-uses-axios-not-fetch` for the local stack and token seeding), start a recording, open the notes popover and check:
1. The Action toggle sits in the composer row without wrapping at the popover's 400px width, in light and dark themes.
2. The action row's owner line and buttons fit without horizontal overflow. Measure with `getBoundingClientRect` via `javascript_tool`: the row's `scrollWidth <= clientWidth`.
3. Alt+A toggles and the placeholder changes.
4. Stop, then confirm with `read_network_requests` that the POST went to `/actions/live` with the action and to `/notes` with the note. The Actions tab lists the action pinned.
Screenshot for the PR.

---

### Task 11: Release checklist, docs, PR

**Files:**
- `version.json` and mirrors: `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`, `apps/web/package-lock.json`, `apps/desktop/package-lock.json`, `integrations/n8n-nodes-diariz/package-lock.json` (two `version` fields each)
- `apps/web/src/lib/releaseNotes/current.ts`
- `apps/web/src/lib/appInfo.ts` (`CAPABILITIES`)
- `README.md` (Features table), `docs/features.md`
- `docs/Data_Schema.md` (`RecordingActions` table section ~line 347 + migration history ~line 131)
- `docs/Overall_Synopsis_of_Platform.md` (new endpoint + extraction merge rule, in the action-items section)
- `apps/web/src/content/help/en/recording-audio.md` ("Taking notes as you go"), `apps/web/src/content/help/en/action-items.md`

- [ ] **Step 1: Bump 0.271.3 to 0.272.0 everywhere.** Use targeted text replacement of `"0.271.3"` / `<Version>0.271.3</Version>`. Never regenerate lock files. Run `npx vitest run src/lib/versionMirrors.test.ts`. Expected: PASS.

- [ ] **Step 2: Release entry.** Confirm the next PR number: `gh pr list --state all --limit 1 --json number` and `gh issue list --state all --limit 1 --json number`, take the max, add 1, and **re-check right before `gh pr create`**. Add at the top of `RECENT`:

```ts
  {
    version: "0.272.0",
    date: "2026-09-16",
    pr: 764,
    headline: "Record actions during the meeting",
    summary:
      "The live notes panel can now record actions as the meeting goes. Switch the composer to Action (or press Alt+A), or turn any note into an action. Actions you record are pinned straight away, so they appear in the Actions tab as soon as the recording uploads, owned by you unless you change it. Automatic extraction still runs afterwards and adds what it finds alongside them, skipping repeats. Re-extracting a meeting's actions now keeps pinned actions and replaces only the rest.",
    added: [
      "Action toggle in the live notes composer (Alt+A), with an Actions filter chip",
      "Make action / Make note on lines in the live notes panel, including the pop-out window",
      "Owner (defaults to you) and due date on actions recorded live",
      "API: POST /api/recordings/{id}/actions/live",
    ],
    changed: [
      "Automatic action extraction adds to actions recorded during the meeting instead of being skipped",
      "Re-extracting actions keeps pinned actions",
    ],
  },
```
Run `npx vitest run src/lib/releaseNotes src/lib/noFancyDashes.test.ts`. Expected: PASS.

- [ ] **Step 3: Inventories in lockstep.** Edit the existing action-items row in `CAPABILITIES`, the README Features table and `docs/features.md` to mention recording actions live from the notes panel, pinned on upload. One concise clause in the tables, a sentence in `features.md`.

- [ ] **Step 4: Schema and architecture docs.** In `Data_Schema.md`, add `Source` (int, NOT NULL, default 0; `ActionSource` Extracted=0/Manual=1/Live=2) and `CapturedAtMs` (bigint, nullable) to `RecordingActions`, add `ActionSource` to the enums list if the doc has one, and add a migration-history row: `AddActionSourceAndCapturedAt` | additive with a column default, so older backups restore and migrate up with every action `Extracted` - **no `CurrentFormat` bump**. In `Overall_Synopsis_of_Platform.md`, where action extraction is described, add the live endpoint and the rule: live actions do not set `ActionsExtractedAt`, and extraction merges with dedupe while re-extract keeps pinned rows.

- [ ] **Step 5: Help articles (ASCII only).** In `recording-audio.md` "Taking notes as you go", add a short paragraph: switch the composer to Action or press Alt+A; any note can be made an action; actions are yours by default and are tracked in Actions once the recording uploads. In `action-items.md`, add a "Recording actions during a meeting" section covering that live actions stay when extraction runs, repeats are skipped, and re-extract keeps pinned actions. Run `npx vitest run src/content/help`. Expected: PASS.

- [ ] **Step 6: Full verification.**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Actions"
```
Then `cd apps/web && npx tsc --noEmit -p . && npx vitest run`, and `cd integrations/n8n-nodes-diariz && npm test`. Every run must be clean with no warnings. Write failing output to a file before filtering it.

- [ ] **Step 7: Commit, push, PR**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json apps/desktop/package-lock.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json integrations/n8n-nodes-diariz/package-lock.json apps/web/src/lib/releaseNotes/current.ts apps/web/src/lib/appInfo.ts README.md docs/features.md docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help/en/recording-audio.md apps/web/src/content/help/en/action-items.md docs/superpowers/specs/2026-09-16-live-actions-in-notes-design.md docs/superpowers/plans/2026-09-16-live-actions-in-notes.md
git commit -m "chore(release): 0.272.0 - record actions during the meeting"
git push -u origin claude/live-actions-in-notes
gh pr create --title "Record actions from the live notes panel" --body-file <body>
```
The PR body says: what changed, the extraction merge and the re-extract behaviour change, the screenshot from Task 10 Step 6, and **"Deployment: server redeploy only - no desktop release (no `apps/desktop/src` changes)."** Confirm the PR number matches `pr:` in `current.ts`, and fix it in a follow-up commit if not.

---

## Self-review notes

- Spec coverage: columns (T1), endpoint (T3), prompt + dedupe (T2), pipeline merge + webhook full list (T4), re-extract keeps pinned + copy (T5), merge copy (T1), `LiveNoteLine` / stream / chip (T6, T9), hook + stash (T7), pop-out (T8), composer toggle, Alt+A, reset-to-note, rows, promote/demote (T9), split attach with partial-failure stash (T10), release/docs (T11).
- `updateAction` undefined-key overwrite is fixed in T8 (where the channel introduces undefined keys) with its own red test.
- The aria-label of the composer stays constant, so existing `getByLabelText(/note this moment/i)` tests keep working.
