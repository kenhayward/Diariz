# LLM Usage Logging - PR 1 (capture + storage) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist one row per outbound non-streaming LLM call, attributed to its caller and purpose, with nightly retention - no viewer yet.

**Architecture:** An `AsyncLocal` scope pushed at each job's entry point carries who/why. The existing `LlmTelemetryHandler` (already attached to every LLM client) reads that scope, measures the call, and drops a record on a bounded in-memory channel. A `BackgroundService` drains the channel and batch-inserts, opening its own DI scope per batch.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit, React 19 + TS (settings toggles only).

**Spec:** `docs/superpowers/specs/2026-08-16-llm-usage-logging-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail with the expected message, then implement. No production code without a preceding failing test.
- **Never store prompt or completion content.** Counts and sizes only. A test asserting this is part of Task 5.
- **Enum values are append-only.** Never renumber `LlmCallKind` - they are ints in Postgres.
- **All `DateTimeOffset` values get `.ToUniversalTime()` before saving.** Npgsql throws at `SaveChanges` on a non-zero offset bound to `timestamptz`, and the in-memory provider will not catch it.
- **No em/en dashes in user-facing text** (UI strings, i18n catalogs, release notes). Plain hyphen `-` only. Code and comments are unaffected.
- **No mocking library.** Add fakes to `tests/Diariz.Api.TestSupport` (namespace `Diariz.Api.Tests.Infrastructure`).
- **Never `git add -A`.** Stage explicit paths - this repo has untracked agent scratch files that have polluted a PR before.
- **Build `Diariz.slnx` before pushing**, not just the unit test project. Unit-only runs miss integration-project compile breaks.
- **`dotnet test --filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- Target version for this PR: **`0.216.0`** (functional enhancement: minor +1, build reset).
- Deployment surface: **server redeploy only** - nothing under `apps/desktop` is touched.

---

## File Structure

**Create:**
- `src/Diariz.Domain/Entities/LlmCallKind.cs` - the call-type enum
- `src/Diariz.Domain/Entities/LlmCall.cs` - the log row
- `src/Diariz.Api/Services/LlmCallScope.cs` - AsyncLocal attribution scope
- `src/Diariz.Api/Services/LlmUsageSink.cs` - `ILlmUsageSink` + bounded-channel implementation
- `src/Diariz.Api/Services/LlmUsageWriter.cs` - draining `BackgroundService` + pure batch helper
- `src/Diariz.Api/Services/LlmUsageRetention.cs` - pure sweep + nightly worker
- `tests/Diariz.Api.Tests/LlmCallScopeTests.cs`
- `tests/Diariz.Api.Tests/LlmUsageSinkTests.cs`
- `tests/Diariz.Api.Tests/LlmUsageRetentionTests.cs`
- `tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs`

**Modify:**
- `src/Diariz.Api/Services/LlmTelemetry.cs` - parser gains reasoning tokens; handler gains the sink
- `src/Diariz.Domain/DiarizDbContext.cs` - `DbSet` + model config
- `src/Diariz.Domain/Entities/PlatformSettings.cs` - three new fields
- `src/Diariz.Api/Program.cs` - DI for sink, writer, retention worker
- The twelve scope push sites (Tasks 8 and 9)
- `src/Diariz.Api/Controllers/PlatformSettingsController.cs` + `apps/web/src/components/SettingsModal.tsx` - expose the settings

---

### Task 1: `LlmCall` entity and migration

**Files:**
- Create: `src/Diariz.Domain/Entities/LlmCallKind.cs`, `src/Diariz.Domain/Entities/LlmCall.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LlmCallKind` enum; `LlmCall` entity with the properties listed below; `DiarizDbContext.LlmCalls`.

- [ ] **Step 1: Write the failing integration test**

This must be an integration test, not a unit test. The in-memory provider enforces no foreign keys and does not round-trip `timestamptz`, so a unit test here would pass without proving anything.

Create `tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs`:

```csharp
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class LlmUsageIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task LlmCall_RoundTripsThroughPostgres()
    {
        await using var db = fx.CreateDbContext();
        var started = DateTimeOffset.UtcNow.AddSeconds(-3);

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Summarize,
            UserEmail = "owner@example.com",
            Model = "qwen3-30b",
            Endpoint = "http://lmstudio:1234/v1/chat/completions",
            StartedAt = started,
            CompletedAt = started.AddSeconds(3),
            DurationMs = 3000,
            PromptTokens = 1200,
            CompletionTokens = 340,
            TotalTokens = 1540,
            Streamed = false,
            Success = true,
            StatusCode = 200,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Equal(LlmCallKind.Summarize, found.Kind);
        Assert.Equal(1540, found.TotalTokens);
        Assert.Equal(TimeSpan.Zero, found.StartedAt.Offset); // stored as UTC
    }

    [Fact]
    public async Task DeletingTheUser_NullsTheLink_ButKeepsTheRowAndTheEmailSnapshot()
    {
        // The log is an audit trail: a user's history must survive their deletion, which is why
        // UserEmail is a snapshot rather than a join. Bulk delete is the erasure escape hatch.
        await using var db = fx.CreateDbContext();
        var user = Users.Seed(db, email: "leaver@example.com");
        await db.SaveChangesAsync();

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Tags,
            UserId = user.Id,
            UserEmail = user.Email!,
            Model = "m",
            Endpoint = "http://x/v1",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 10,
            Success = true,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Null(found.UserId);
        Assert.Equal("leaver@example.com", found.UserEmail);
    }
}
```

Check `tests/Diariz.Api.IntegrationTests/Infrastructure` for the exact `ContainersFixture` constructor-injection style and the `Users.Seed` helper signature in `tests/Diariz.Api.TestSupport/Users.cs`, and match them - do not invent a seeding helper.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmUsageIntegrationTests"
```

Expected: compile failure, `The type or namespace name 'LlmCall' could not be found`.

- [ ] **Step 3: Create the enum**

`src/Diariz.Domain/Entities/LlmCallKind.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>What an LLM call was for. Stored as an int, so this is APPEND ONLY - never renumber or
/// reorder, exactly like <see cref="RecordingSource"/>.</summary>
public enum LlmCallKind
{
    Unknown = 0,
    Summarize = 1,
    SectionSummary = 2,
    MeetingMinutes = 3,
    SectionMinutes = 4,
    MeetingTypeMinutes = 5,
    ExtractActions = 6,
    Tags = 7,
    Translation = 8,
    Dictation = 9,
    Embedding = 10,
    SearchQuery = 11,
    ChatMessage = 12,
    FormulaRun = 13,
}
```

- [ ] **Step 4: Create the entity**

`src/Diariz.Domain/Entities/LlmCall.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>One outbound call to a model endpoint. Written by LlmTelemetryHandler via a bounded channel.
///
/// NEVER stores prompt or completion content - counts and sizes only. Meeting content staying out of
/// telemetry is the same rule SentryScrubber enforces, and this table is no exception.
///
/// The user/recording/section links are ON DELETE SET NULL and are paired with a denormalised snapshot
/// (email, title, name) so a row stays readable after its subject is deleted. That is deliberate for an
/// audit trail; the admin's filtered bulk delete is the erasure path.</summary>
public class LlmCall
{
    public Guid Id { get; set; }

    /// <summary>Groups every call made by one user-facing operation. Turns = MAX(Sequence) per operation.</summary>
    public Guid OperationId { get; set; }

    /// <summary>1-based index of this call within its operation.</summary>
    public int Sequence { get; set; }

    public LlmCallKind Kind { get; set; }

    public Guid? UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;

    public Guid? RecordingId { get; set; }
    public string? RecordingTitle { get; set; }

    public Guid? SectionId { get; set; }
    public string? SectionName { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>Scheme, host and path only. The query string is dropped outright rather than scrubbed -
    /// the same rule the handler already applies to span descriptions, because a SignalR JWT reached a
    /// transaction name that way once already.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Stored rather than derived from the two timestamps, so ordering and SUM are trivial.</summary>
    public int DurationMs { get; set; }

    /// <summary>Streaming calls only; null otherwise. Populated in PR 2.</summary>
    public int? TimeToFirstTokenMs { get; set; }

    // All nullable: plenty of OpenAI-compatible servers report no usage at all, and a missing count is
    // not a zero. Aggregates must ignore nulls rather than treat them as 0.
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>Serialized request body length. Shows when LlmContextBudget truncation is biting.</summary>
    public int? PromptChars { get; set; }

    public bool Streamed { get; set; }
    public bool Success { get; set; }
    public int? StatusCode { get; set; }

    /// <summary>A class, never a message body: Timeout, Canceled, Transport, Http500.</summary>
    public string? ErrorKind { get; set; }
}
```

- [ ] **Step 5: Register the DbSet and model config**

In `src/Diariz.Domain/DiarizDbContext.cs`, add alongside the other `DbSet` properties (near `Feedback` at the end of the list):

```csharp
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
```

In `OnModelCreating`, add (this config is provider-agnostic, so it goes outside any `IsNpgsql()` guard):

```csharp
        builder.Entity<LlmCall>(e =>
        {
            e.HasIndex(c => c.StartedAt).IsDescending();
            e.HasIndex(c => new { c.UserId, c.StartedAt }).IsDescending(false, true);
            e.HasIndex(c => c.OperationId);

            // SET NULL, not CASCADE: deleting a user or a recording must not erase the usage history.
            // The denormalised snapshot columns are what keep the orphaned row readable.
            e.HasOne<ApplicationUser>().WithMany()
                .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Recording>().WithMany()
                .HasForeignKey(c => c.RecordingId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Section>().WithMany()
                .HasForeignKey(c => c.SectionId).OnDelete(DeleteBehavior.SetNull);
        });
```

Confirm the user entity's type name in this file before writing `ApplicationUser` - match whatever the existing relationships use.

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add AddLlmCalls --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Read the generated file and confirm: the three indexes exist, all three FKs are `ReferentialAction.SetNull`, and the timestamps are `timestamp with time zone`. The migration is purely additive, so `MaintenanceController.CurrentFormat` is **not** bumped - an older backup restores onto this schema fine.

- [ ] **Step 7: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmUsageIntegrationTests"
```

Expected: 2 passed. (Docker must be running.)

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/LlmCall.cs src/Diariz.Domain/Entities/LlmCallKind.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs
git commit -m "feat(llm-usage): add LlmCall entity and migration"
```

---

### Task 2: `LlmCallScope` - AsyncLocal attribution

**Files:**
- Create: `src/Diariz.Api/Services/LlmCallScope.cs`
- Test: `tests/Diariz.Api.Tests/LlmCallScopeTests.cs`

**Interfaces:**
- Consumes: `LlmCallKind` (Task 1).
- Produces: `LlmCallScope.Push(kind, userId, userEmail, recordingId, recordingTitle, sectionId, sectionName)` returning `IDisposable`; `LlmCallScope.Active` (nullable); instance method `int NextSequence()`; readable properties `Kind`, `OperationId`, `UserId`, `UserEmail`, `RecordingId`, `RecordingTitle`, `SectionId`, `SectionName`.

- [ ] **Step 1: Write the failing tests**

`tests/Diariz.Api.Tests/LlmCallScopeTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The scope is how a call deep inside HttpClient learns who asked for it and why. Without it
/// every row would read Unknown, which is the whole reason the handler alone was not enough.</summary>
public class LlmCallScopeTests
{
    [Fact]
    public void NoScope_MeansNoAmbientContext()
    {
        Assert.Null(LlmCallScope.Active);
    }

    [Fact]
    public void Push_MakesTheContextAmbient_AndDisposeRestoresIt()
    {
        var userId = Guid.NewGuid();
        using (LlmCallScope.Push(LlmCallKind.Summarize, userId: userId, userEmail: "a@b.c"))
        {
            Assert.Equal(LlmCallKind.Summarize, LlmCallScope.Active!.Kind);
            Assert.Equal(userId, LlmCallScope.Active.UserId);
            Assert.Equal("a@b.c", LlmCallScope.Active.UserEmail);
        }

        Assert.Null(LlmCallScope.Active);
    }

    [Fact]
    public void SequenceIncrementsPerCall_WhichIsHowTurnsAreCounted()
    {
        // ChatToolOrchestrator loops without pushing its own scope, so each model round-trip inside one
        // user turn lands here. MAX(Sequence) per operation IS the turn count.
        using var scope = LlmCallScope.Push(LlmCallKind.ChatMessage);
        Assert.Equal(1, scope.NextSequence());
        Assert.Equal(2, scope.NextSequence());
        Assert.Equal(3, scope.NextSequence());
    }

    [Fact]
    public void EachPush_GetsItsOwnOperationId()
    {
        Guid first, second;
        using (var a = LlmCallScope.Push(LlmCallKind.Tags)) first = a.OperationId;
        using (var b = LlmCallScope.Push(LlmCallKind.Tags)) second = b.OperationId;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NestedPush_RestoresTheOuterScope_NotNull()
    {
        using var outer = LlmCallScope.Push(LlmCallKind.MeetingTypeMinutes);
        using (LlmCallScope.Push(LlmCallKind.Summarize))
            Assert.Equal(LlmCallKind.Summarize, LlmCallScope.Active!.Kind);

        Assert.Equal(LlmCallKind.MeetingTypeMinutes, LlmCallScope.Active!.Kind);
    }

    [Fact]
    public async Task TheScope_FlowsAcrossAwait()
    {
        // Every push site is async and the call it attributes happens after several awaits. If this
        // failed, attribution would silently vanish in exactly the code paths that matter.
        using var scope = LlmCallScope.Push(LlmCallKind.FormulaRun);
        await Task.Yield();
        await Task.Delay(1);
        Assert.Equal(LlmCallKind.FormulaRun, LlmCallScope.Active!.Kind);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmCallScopeTests"
```

Expected: compile failure, `The name 'LlmCallScope' does not exist`.

- [ ] **Step 3: Implement the scope**

`src/Diariz.Api/Services/LlmCallScope.cs`:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Ambient "who asked for this call, and why", read by <see cref="LlmTelemetryHandler"/>.
///
/// WHY AMBIENT: the handler lives inside HttpClient, several layers below the code that knows the user
/// and the recording. Threading a context parameter through every client interface instead would touch
/// eight interfaces and every call site, and a new client could still forget to pass it on. A scope is
/// pushed once per job at the top; everything below it is attributed for free.
///
/// A call made with no scope active is still logged, as Kind = Unknown. That is deliberate: an
/// unattributed row is visible and fixable, whereas a dropped row is not.</summary>
public sealed class LlmCallScope : IDisposable
{
    private static readonly AsyncLocal<LlmCallScope?> CurrentScope = new();

    /// <summary>The innermost active scope, or null when the call is unattributed.</summary>
    public static LlmCallScope? Active => CurrentScope.Value;

    private readonly LlmCallScope? _parent;
    private int _calls;

    public LlmCallKind Kind { get; }
    public Guid OperationId { get; }
    public Guid? UserId { get; }
    public string UserEmail { get; }
    public Guid? RecordingId { get; }
    public string? RecordingTitle { get; }
    public Guid? SectionId { get; }
    public string? SectionName { get; }

    private LlmCallScope(
        LlmCallKind kind, Guid? userId, string? userEmail, Guid? recordingId, string? recordingTitle,
        Guid? sectionId, string? sectionName, LlmCallScope? parent)
    {
        Kind = kind;
        OperationId = Guid.NewGuid();
        UserId = userId;
        UserEmail = userEmail ?? string.Empty;
        RecordingId = recordingId;
        RecordingTitle = recordingTitle;
        SectionId = sectionId;
        SectionName = sectionName;
        _parent = parent;
    }

    /// <summary>Starts a new operation. Dispose restores whatever was active before.</summary>
    public static LlmCallScope Push(
        LlmCallKind kind, Guid? userId = null, string? userEmail = null, Guid? recordingId = null,
        string? recordingTitle = null, Guid? sectionId = null, string? sectionName = null)
    {
        var scope = new LlmCallScope(
            kind, userId, userEmail, recordingId, recordingTitle, sectionId, sectionName, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The 1-based index of the next call in this operation. Interlocked because a single
    /// operation can fan out concurrently (per-section minutes).</summary>
    public int NextSequence() => Interlocked.Increment(ref _calls);

    public void Dispose() => CurrentScope.Value = _parent;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmCallScopeTests"
```

Expected: 6 passed.

- [ ] **Step 5: Mutation-verify one test**

Temporarily change `NextSequence()` to `return 1;`. Re-run. `SequenceIncrementsPerCall` must FAIL. Restore. This step exists because tautological tests are the dominant defect class in this repo - a test that cannot fail is worse than no test.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/LlmCallScope.cs tests/Diariz.Api.Tests/LlmCallScopeTests.cs
git commit -m "feat(llm-usage): add LlmCallScope for call attribution"
```

---

### Task 3: Parse reasoning tokens

**Files:**
- Modify: `src/Diariz.Api/Services/LlmTelemetry.cs:7` (the `LlmUsage` record), `:15` (`TryParse`)
- Test: `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LlmUsage` gains a fourth member - the record becomes `LlmUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens, int? ReasoningTokens)`. **Reasoning is appended last** so the existing positional construction in tests and `NullLlmSpan` keeps compiling.

- [ ] **Step 1: Write the failing tests**

Append to the existing `LlmUsageParserTests` class in `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`:

```csharp
    [Fact]
    public void TryParse_ReadsReasoningTokens_FromCompletionTokenDetails()
    {
        var json = """
        {"usage":{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,
                  "completion_tokens_details":{"reasoning_tokens":40}}}
        """;

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Equal(40, usage.ReasoningTokens);
    }

    [Fact]
    public void TryParse_LeavesReasoningNull_WhenTheServerDoesNotReportIt()
    {
        // Most local endpoints report no details block at all. Null must stay null: a reasoning count
        // of 0 would be a claim the model did no reasoning, which is not what silence means.
        var json = """{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

        Assert.True(LlmUsageParser.TryParse(json, out var usage));
        Assert.Null(usage.ReasoningTokens);
    }

    [Fact]
    public void TryParse_NeverThrows_WhenDetailsIsTheWrongShape()
    {
        var json = """{"usage":{"prompt_tokens":10,"completion_tokens_details":"nonsense"}}""";
        var ex = Record.Exception(() => LlmUsageParser.TryParse(json, out _));
        Assert.Null(ex);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageParserTests"
```

Expected: compile failure, `'LlmUsage' does not contain a definition for 'ReasoningTokens'`.

- [ ] **Step 3: Extend the record and the parser**

In `src/Diariz.Api/Services/LlmTelemetry.cs`, change the record to:

```csharp
public readonly record struct LlmUsage(
    int? PromptTokens, int? CompletionTokens, int? TotalTokens, int? ReasoningTokens = null);
```

In `TryParse`, after the existing `total` computation and before the `if (prompt is null && ...)` guard, add:

```csharp
            // Reported by reasoning models under completion_tokens_details. Absent almost everywhere
            // else, and absent must stay null rather than becoming 0.
            int? reasoning = null;
            if (u.TryGetProperty("completion_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
                reasoning = ReadInt(details, "reasoning_tokens");
```

and change the construction to:

```csharp
            usage = new LlmUsage(prompt, completion, total, reasoning);
```

- [ ] **Step 4: Run the full unit suite to verify nothing else broke**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmTelemetry"
```

Expected: all pass, including the pre-existing parser tests.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/LlmTelemetry.cs tests/Diariz.Api.Tests/LlmTelemetryTests.cs
git commit -m "feat(llm-usage): parse reasoning_tokens from completion_tokens_details"
```

---

### Task 4: The bounded-channel sink

**Files:**
- Create: `src/Diariz.Api/Services/LlmUsageSink.cs`
- Test: `tests/Diariz.Api.Tests/LlmUsageSinkTests.cs`

**Interfaces:**
- Consumes: `LlmCall` (Task 1).
- Produces: `interface ILlmUsageSink { void Record(LlmCall call); }`; `sealed class ChannelLlmUsageSink : ILlmUsageSink` with `ChannelReader<LlmCall> Reader`, `long Dropped`, and `const int Capacity = 10_000`.

- [ ] **Step 1: Write the failing tests**

`tests/Diariz.Api.Tests/LlmUsageSinkTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class LlmUsageSinkTests
{
    private static LlmCall Call(int seq = 1) => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = seq,
        Kind = LlmCallKind.Summarize, Model = "m", Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Success = true,
    };

    [Fact]
    public void Record_MakesTheCallReadable()
    {
        var sink = new ChannelLlmUsageSink();
        var call = Call();

        sink.Record(call);

        Assert.True(sink.Reader.TryRead(out var read));
        Assert.Equal(call.Id, read!.Id);
    }

    [Fact]
    public void Record_NeverBlocks_AndDropsOldestWhenFull()
    {
        // The sink sits on the LLM call path. It must never block or throw: a monitoring feature that
        // can stall a summary is worse than a monitoring feature with gaps.
        var sink = new ChannelLlmUsageSink();
        for (var i = 0; i < ChannelLlmUsageSink.Capacity + 50; i++) sink.Record(Call(i + 1));

        Assert.Equal(50, sink.Dropped);

        var drained = 0;
        while (sink.Reader.TryRead(out _)) drained++;
        Assert.Equal(ChannelLlmUsageSink.Capacity, drained);
    }

    [Fact]
    public void Dropped_StartsAtZero()
    {
        Assert.Equal(0, new ChannelLlmUsageSink().Dropped);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageSinkTests"
```

Expected: compile failure, `The name 'ChannelLlmUsageSink' does not exist`.

- [ ] **Step 3: Implement the sink**

`src/Diariz.Api/Services/LlmUsageSink.cs`:

```csharp
using System.Threading.Channels;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Where a finished LLM call goes. An interface so the handler can be unit-tested without a
/// database - see FakeLlmUsageSink.</summary>
public interface ILlmUsageSink
{
    void Record(LlmCall call);
}

/// <summary>Hands the record to a background writer through a bounded in-memory channel.
///
/// WHY NOT WRITE DIRECTLY: the handler runs on the LLM call path. An awaited insert there would add a
/// round-trip to every summary and would let a database problem degrade transcription and chat - turning
/// a monitoring feature into an availability risk.
///
/// The trade is that records still buffered during a hard crash are lost, and that a sustained burst
/// past Capacity drops the oldest rows. Both are acceptable for a usage log and neither can affect the
/// call being measured.</summary>
public sealed class ChannelLlmUsageSink : ILlmUsageSink
{
    public const int Capacity = 10_000;

    private long _dropped;
    private readonly Channel<LlmCall> _channel;

    public ChannelLlmUsageSink()
    {
        _channel = Channel.CreateBounded<LlmCall>(
            new BoundedChannelOptions(Capacity)
            {
                // Drop rather than block: TryWrite must always return immediately on the call path.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            // The channel is built in the constructor, not a field initialiser, because the
            // itemDropped callback closes over _dropped.
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<LlmCall> Reader => _channel.Reader;

    /// <summary>How many records have been dropped because the buffer was full. Surfaced so a persistent
    /// backlog is visible rather than silent.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(LlmCall call) => _channel.Writer.TryWrite(call);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageSinkTests"
```

Expected: 3 passed.

- [ ] **Step 5: Add the fake for later tasks**

Append to `tests/Diariz.Api.TestSupport/Fakes.cs`:

```csharp
/// <summary>Records what the telemetry handler decided to log, so the handler can be tested without a
/// database or a channel.</summary>
public sealed class FakeLlmUsageSink : ILlmUsageSink
{
    public List<LlmCall> Calls { get; } = new();
    public void Record(LlmCall call) => Calls.Add(call);
}
```

Add `using Diariz.Domain.Entities;` to that file if it is not already present.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/LlmUsageSink.cs tests/Diariz.Api.Tests/LlmUsageSinkTests.cs tests/Diariz.Api.TestSupport/Fakes.cs
git commit -m "feat(llm-usage): add bounded-channel usage sink"
```

---

### Task 5: The handler records each call

**Files:**
- Modify: `src/Diariz.Api/Services/LlmTelemetry.cs:109-169` (`LlmTelemetryHandler`)
- Test: `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`

**Interfaces:**
- Consumes: `ILlmUsageSink` (Task 4), `LlmCallScope` (Task 2), `LlmUsage.ReasoningTokens` (Task 3).
- Produces: `LlmTelemetryHandler(ILlmTrace trace, ILlmUsageSink sink)` - a **second constructor parameter**, which is a breaking change to every existing construction site. Search for `new LlmTelemetryHandler(` and update all of them.

- [ ] **Step 1: Write the failing tests**

Add a new class to `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`. Reuse whatever stub `HttpMessageHandler` the existing handler tests in this file already use; if there is none, add a small one.

```csharp
public class LlmTelemetryHandlerUsageTests
{
    private static HttpClient Client(LlmTelemetryHandler handler, HttpResponseMessage response)
    {
        handler.InnerHandler = new StubHandler(response);
        return new HttpClient(handler) { BaseAddress = new Uri("http://lmstudio:1234/") };
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task RecordsTheCall_WithTheAmbientScopesAttribution()
    {
        var sink = new FakeLlmUsageSink();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        using var scope = LlmCallScope.Push(
            LlmCallKind.Summarize, userId, "owner@example.com", recordingId, "Standup");

        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            Json("""{"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}"""));
        await http.PostAsync("/v1/chat/completions", new StringContent("""{"model":"qwen"}"""));

        var call = Assert.Single(sink.Calls);
        Assert.Equal(LlmCallKind.Summarize, call.Kind);
        Assert.Equal(userId, call.UserId);
        Assert.Equal("owner@example.com", call.UserEmail);
        Assert.Equal(recordingId, call.RecordingId);
        Assert.Equal("Standup", call.RecordingTitle);
        Assert.Equal(scope.OperationId, call.OperationId);
        Assert.Equal(1, call.Sequence);
        Assert.Equal(100, call.PromptTokens);
        Assert.Equal(20, call.CompletionTokens);
        Assert.True(call.Success);
        Assert.Equal(200, call.StatusCode);
    }

    [Fact]
    public async Task RecordsAnUnattributedCall_AsUnknown_RatherThanDroppingIt()
    {
        // A client registered later without a scope must show up as a visible gap, not vanish. This is
        // the failure mode AddLlmClient exists to prevent, and silence would reintroduce it.
        var sink = new FakeLlmUsageSink();
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("""{"usage":{"total_tokens":5}}"""));

        await http.PostAsync("/v1/embeddings", new StringContent("{}"));

        Assert.Equal(LlmCallKind.Unknown, Assert.Single(sink.Calls).Kind);
    }

    [Fact]
    public async Task SequenceIncrementsAcrossCallsInOneScope()
    {
        var sink = new FakeLlmUsageSink();
        using var scope = LlmCallScope.Push(LlmCallKind.ChatMessage);

        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        Assert.Equal([1, 2], sink.Calls.Select(c => c.Sequence));
        Assert.Single(sink.Calls.Select(c => c.OperationId).Distinct());
    }

    [Fact]
    public async Task RecordsAFailedCall_WithItsStatusAndErrorKind()
    {
        // A log of only successful calls hides the expensive problem: the call that burned the whole
        // timeout and produced nothing.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Tags);
        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });

        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        var call = Assert.Single(sink.Calls);
        Assert.False(call.Success);
        Assert.Equal(500, call.StatusCode);
        Assert.Equal("Http500", call.ErrorKind);
    }

    [Fact]
    public async Task StoresNoPromptOrCompletionContent()
    {
        // The hard line: counts and sizes only. If this ever fails, meeting content is leaking into a
        // table an administrator browses.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(
            new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            Json("""{"choices":[{"message":{"content":"the secret merger closes friday"}}],"usage":{"total_tokens":9}}"""));

        await http.PostAsync("/v1/chat/completions", new StringContent("""{"messages":[{"content":"confidential transcript"}]}"""));

        var serialized = System.Text.Json.JsonSerializer.Serialize(Assert.Single(sink.Calls));
        Assert.DoesNotContain("secret merger", serialized);
        Assert.DoesNotContain("confidential transcript", serialized);
    }

    [Fact]
    public async Task RecordsTheEndpointWithoutItsQueryString()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Embedding);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));

        await http.PostAsync("/v1/embeddings?api_key=supersecret", new StringContent("{}"));

        Assert.Equal("http://lmstudio:1234/v1/embeddings", Assert.Single(sink.Calls).Endpoint);
    }

    [Fact]
    public async Task RecordsPromptChars_FromTheRequestBodyLength()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Json("{}"));
        var body = """{"model":"qwen"}""";

        await http.PostAsync("/v1/chat/completions", new StringContent(body));

        Assert.Equal(body.Length, Assert.Single(sink.Calls).PromptChars);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmTelemetryHandlerUsageTests"
```

Expected: compile failure on the two-argument `LlmTelemetryHandler` constructor.

- [ ] **Step 3: Implement**

In `src/Diariz.Api/Services/LlmTelemetry.cs`, change the handler's field and constructor:

```csharp
    private readonly ILlmTrace _trace;
    private readonly ILlmUsageSink _sink;

    public LlmTelemetryHandler(ILlmTrace trace, ILlmUsageSink sink)
    {
        _trace = trace;
        _sink = sink;
    }
```

Replace the body of `SendAsync` with:

```csharp
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        var target = uri is null ? "(no uri)" : uri.GetLeftPart(UriPartial.Path);

        using var span = _trace.StartSpan(Op, $"{request.Method} {target}");

        var scope = LlmCallScope.Active;
        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var promptChars = await PromptCharsAsync(request, ct);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            // A transport failure or a timeout is exactly the expensive case an administrator needs to
            // see, so it is recorded before the exception continues on to the caller unchanged.
            clock.Stop();
            Record(scope, target, request, startedAt, clock, promptChars, null, default, ErrorKindOf(ex));
            throw;
        }

        span.SetStatusCode((int)response.StatusCode);

        var usage = default(LlmUsage);
        if (IsJson(response) && LlmUsageParser.TryParse(await ReadForUsageAsync(response, ct), out var parsed))
        {
            usage = parsed;
            span.SetUsage(parsed);
        }

        clock.Stop();
        var status = (int)response.StatusCode;
        Record(
            scope, target, request, startedAt, clock, promptChars, status, usage,
            response.IsSuccessStatusCode ? null : $"Http{status}");

        return response;
    }

    /// <summary>Length of the serialized request body, used as a proxy for prompt size so no call site has
    /// to report it. Best-effort: a body that cannot be read costs a null, never the call.</summary>
    private static async Task<int?> PromptCharsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return null;
        try
        {
            return (await request.Content.ReadAsStringAsync(ct)).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ErrorKindOf(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "Timeout",
        HttpRequestException => "Transport",
        _ => ex.GetType().Name,
    };

    /// <summary>Builds the row and hands it to the sink. Content is never included - only counts, sizes
    /// and identifiers.</summary>
    private void Record(
        LlmCallScope? scope, string target, HttpRequestMessage request, DateTimeOffset startedAt,
        System.Diagnostics.Stopwatch clock, int? promptChars, int? statusCode, LlmUsage usage,
        string? errorKind)
    {
        _sink.Record(new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = scope?.OperationId ?? Guid.NewGuid(),
            Sequence = scope?.NextSequence() ?? 1,
            Kind = scope?.Kind ?? LlmCallKind.Unknown,
            UserId = scope?.UserId,
            UserEmail = scope?.UserEmail ?? string.Empty,
            RecordingId = scope?.RecordingId,
            RecordingTitle = scope?.RecordingTitle,
            SectionId = scope?.SectionId,
            SectionName = scope?.SectionName,
            Model = ModelOf(request),
            Endpoint = target,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMilliseconds(clock.Elapsed.TotalMilliseconds),
            DurationMs = (int)clock.ElapsedMilliseconds,
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            ReasoningTokens = usage.ReasoningTokens,
            TotalTokens = usage.TotalTokens,
            PromptChars = promptChars,
            Streamed = false,
            Success = errorKind is null,
            StatusCode = statusCode,
            ErrorKind = errorKind,
        });
    }
```

Add a `ModelOf(HttpRequestMessage)` helper that reads the `model` property from the buffered request body, returning `string.Empty` when absent or unparseable - it must never throw. Add `using Diariz.Domain.Entities;` at the top of the file.

**Do not** log the request body itself anywhere. `PromptCharsAsync` takes a length and discards the string.

- [ ] **Step 4: Update the DI registration**

In `src/Diariz.Api/Program.cs`, beside `builder.Services.AddTransient<LlmTelemetryHandler>();` (line ~300) add:

```csharp
builder.Services.AddSingleton<ChannelLlmUsageSink>();
builder.Services.AddSingleton<ILlmUsageSink>(sp => sp.GetRequiredService<ChannelLlmUsageSink>());
```

The sink is a **singleton** and holds no `DbContext`. The handler must never take a scoped dependency: `HttpClientFactory` pools handler instances for roughly two minutes, so a scoped `DbContext` injected here would be captive and would eventually be used after disposal.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmTelemetry"
```

Expected: all pass, including the pre-existing span tests.

- [ ] **Step 6: Mutation-verify the privacy test**

Temporarily add `ErrorKind = await request.Content!.ReadAsStringAsync(ct)` inside `Record`. Re-run `StoresNoPromptOrCompletionContent`. It must FAIL. Restore.

- [ ] **Step 7: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

Expected: no errors. This catches any other construction site of `LlmTelemetryHandler` in the integration project.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Services/LlmTelemetry.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/LlmTelemetryTests.cs
git commit -m "feat(llm-usage): record every non-streaming LLM call to the sink"
```

---

### Task 6: Platform settings

**Files:**
- Modify: `src/Diariz.Domain/Entities/PlatformSettings.cs`, `src/Diariz.Api/Controllers/PlatformSettingsController.cs`, `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/lib/types.ts`, `apps/web/src/locales/en/*.json` (and the other locale files)
- Test: `tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs`, `apps/web/src/components/SettingsModal.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: `PlatformSettings.LlmUsageLoggingEnabled` (bool, default `true`), `.LlmUsageRetentionDays` (int, default `90`), `.LlmStreamUsageEnabled` (bool, default `true`). All three appear on the platform settings GET/PUT payloads.

- [ ] **Step 1: Write the failing controller test**

Add to `tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs`, matching the file's existing arrangement style:

```csharp
    [Fact]
    public async Task Update_PersistsTheLlmUsageLoggingSettings()
    {
        await using var db = TestDb.Create();
        // ... arrange the controller exactly as the neighbouring tests in this file do ...

        await controller.Update(new PlatformSettingsDto
        {
            // ... the other required fields, copied from a neighbouring test ...
            LlmUsageLoggingEnabled = false,
            LlmUsageRetentionDays = 30,
            LlmStreamUsageEnabled = false,
        });

        var saved = await db.PlatformSettings.SingleAsync();
        Assert.False(saved.LlmUsageLoggingEnabled);
        Assert.Equal(30, saved.LlmUsageRetentionDays);
        Assert.False(saved.LlmStreamUsageEnabled);
    }

    [Fact]
    public void Defaults_KeepLoggingOn_AndRetainNinetyDays()
    {
        // Logging on by default is the point of the feature; retention on by default is what stops the
        // largest table in the database growing without bound.
        var settings = new PlatformSettings();
        Assert.True(settings.LlmUsageLoggingEnabled);
        Assert.Equal(90, settings.LlmUsageRetentionDays);
        Assert.True(settings.LlmStreamUsageEnabled);
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PlatformSettingsControllerTests"
```

Expected: compile failure on `LlmUsageLoggingEnabled`.

- [ ] **Step 3: Add the entity fields**

In `src/Diariz.Domain/Entities/PlatformSettings.cs`, next to the other LLM settings:

```csharp
    public const int DefaultLlmUsageRetentionDays = 90;

    /// <summary>Master switch for the LLM usage log. On by default - the log is the feature. Enforced by
    /// LlmUsageWriter, not the handler, so the call path never pays for a settings lookup.</summary>
    public bool LlmUsageLoggingEnabled { get; set; } = true;

    /// <summary>Usage rows older than this many days are deleted by the nightly sweep. 0 = keep forever.
    /// This table gets a row per call, and embeddings write one per chunk, so a bound matters.</summary>
    public int LlmUsageRetentionDays { get; set; } = DefaultLlmUsageRetentionDays;

    /// <summary>Whether streaming requests ask for token counts via stream_options.include_usage.
    /// A toggle rather than a constant because an OpenAI-compatible endpoint that rejects the unknown
    /// field must be recoverable without a redeploy. Used from PR 2.</summary>
    public bool LlmStreamUsageEnabled { get; set; } = true;
```

- [ ] **Step 4: Add the three fields to the DTO and the update path**

In `PlatformSettingsController.cs`, add them to the settings DTO and to whatever mapping the `Update` action performs, following exactly how `LlmTimeoutSeconds` is handled. Clamp `LlmUsageRetentionDays` to `>= 0`.

- [ ] **Step 5: Generate the migration**

```bash
dotnet ef migrations add AddLlmUsageSettings --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Confirm the generated columns carry the defaults (`true`, `90`, `true`) so existing rows get sensible values rather than `false`/`0`.

- [ ] **Step 6: Run to verify the tests pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PlatformSettingsControllerTests"
```

Expected: all pass.

- [ ] **Step 7: Write the failing web test**

In `apps/web/src/components/SettingsModal.test.tsx`, following the existing patterns in that file (`vi.mock` of `../lib/api`, render inside `MemoryRouter` + `QueryClientProvider`, **plain assertions - no jest-dom matchers, nothing in apps/web uses them**):

```tsx
it("saves the LLM usage logging settings from the AI tab", async () => {
  // ... render the modal on the "ai" tab as neighbouring tests do ...
  await userEvent.clear(screen.getByLabelText(/keep usage log for/i));
  await userEvent.type(screen.getByLabelText(/keep usage log for/i), "30");
  await userEvent.click(screen.getByRole("button", { name: /save/i }));

  expect(api.updatePlatformSettings).toHaveBeenCalledWith(
    expect.objectContaining({ llmUsageRetentionDays: 30 }),
  );
});
```

- [ ] **Step 8: Run to verify it fails, then add the controls**

```bash
cd apps/web && npx vitest run src/components/SettingsModal.test.tsx
```

Expected: FAIL, the label is not found. Then add to the `ai` tab of `SettingsModal.tsx`: a "Log LLM usage" checkbox, a "Keep usage log for (days)" number input with the hint that 0 keeps everything, and a "Request token counts on streaming calls" checkbox. Add the strings to `apps/web/src/locales/en/` and every other locale file. **Plain hyphens only - no em or en dashes.** Add the three fields to the settings type in `apps/web/src/lib/types.ts`.

- [ ] **Step 9: Run the web tests and the typecheck**

```bash
cd apps/web && npx vitest run src/components/SettingsModal.test.tsx && npm run build
```

Expected: tests pass, build clean.

- [ ] **Step 10: Commit**

```bash
git add src/Diariz.Domain/Entities/PlatformSettings.cs src/Diariz.Domain/Migrations src/Diariz.Api/Controllers/PlatformSettingsController.cs tests/Diariz.Api.Tests/PlatformSettingsControllerTests.cs apps/web/src/components/SettingsModal.tsx apps/web/src/components/SettingsModal.test.tsx apps/web/src/lib/types.ts apps/web/src/locales
git commit -m "feat(llm-usage): add platform settings for logging, retention and stream usage"
```

---

### Task 7: The background writer

**Files:**
- Create: `src/Diariz.Api/Services/LlmUsageWriter.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/LlmUsageSinkTests.cs` (batch helper), `tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs` (persistence)

**Interfaces:**
- Consumes: `ChannelLlmUsageSink` (Task 4), `IPlatformSettingsService` (existing), `DiarizDbContext`.
- Produces: `static class LlmUsageBatch { static Task<List<LlmCall>> DrainAsync(ChannelReader<LlmCall> reader, int max, CancellationToken ct) }`; `class LlmUsageWriter : BackgroundService`.

- [ ] **Step 1: Write the failing batch test**

Append to `tests/Diariz.Api.Tests/LlmUsageSinkTests.cs`:

```csharp
public class LlmUsageBatchTests
{
    private static LlmCall Call() => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1,
        Kind = LlmCallKind.Tags, Model = "m", Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, Success = true,
    };

    [Fact]
    public async Task DrainAsync_TakesAtMostMax_LeavingTheRestBuffered()
    {
        var sink = new ChannelLlmUsageSink();
        for (var i = 0; i < 5; i++) sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 3, CancellationToken.None);

        Assert.Equal(3, batch.Count);
        Assert.True(sink.Reader.TryRead(out _)); // the remainder is still there
    }

    [Fact]
    public async Task DrainAsync_ReturnsWhatIsAvailable_WithoutWaitingForMax()
    {
        // The writer flushes on a timer as well as on volume. If this blocked until `max` arrived, a
        // quiet system would never persist anything.
        var sink = new ChannelLlmUsageSink();
        sink.Record(Call());

        var batch = await LlmUsageBatch.DrainAsync(sink.Reader, max: 200, CancellationToken.None);

        Assert.Single(batch);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageBatchTests"
```

Expected: `The name 'LlmUsageBatch' does not exist`.

- [ ] **Step 3: Implement the writer**

`src/Diariz.Api/Services/LlmUsageWriter.cs`:

```csharp
using System.Threading.Channels;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Pure draining helper, separated from the hosted service so batching can be tested without a
/// host or a database - the same separation as pipeline._shape_segments in the worker.</summary>
public static class LlmUsageBatch
{
    /// <summary>Waits for at least one record, then takes up to <paramref name="max"/> without waiting
    /// for more. Returns an empty list when the channel completes.</summary>
    public static async Task<List<LlmCall>> DrainAsync(
        ChannelReader<LlmCall> reader, int max, CancellationToken ct)
    {
        var batch = new List<LlmCall>();
        if (!await reader.WaitToReadAsync(ct)) return batch;

        while (batch.Count < max && reader.TryRead(out var call)) batch.Add(call);
        return batch;
    }
}

/// <summary>Persists recorded LLM calls off the call path.
///
/// Opens its OWN DI scope per batch. The handler cannot hold a DbContext: it is registered transient but
/// HttpClientFactory pools handler instances for about two minutes, so an injected scoped dependency
/// would be captive and eventually used after disposal.
///
/// The LlmUsageLoggingEnabled switch is enforced HERE rather than in the handler, so the call path never
/// pays for a settings lookup. Records made while logging is off are drained and discarded.</summary>
public class LlmUsageWriter(
    ChannelLlmUsageSink sink, IServiceProvider services, ILogger<LlmUsageWriter> logger)
    : BackgroundService
{
    private const int MaxBatch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<LlmCall> batch;
            try
            {
                batch = await LlmUsageBatch.DrainAsync(sink.Reader, MaxBatch, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // host shutting down
            }

            if (batch.Count == 0) continue;

            try
            {
                using var scope = services.CreateScope();
                var settings = await scope.ServiceProvider
                    .GetRequiredService<IPlatformSettingsService>().GetAsync(stoppingToken);
                if (!settings.LlmUsageLoggingEnabled) continue; // drained and discarded

                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                foreach (var call in batch)
                {
                    // Npgsql rejects a non-zero offset bound to timestamptz, and the in-memory provider
                    // will not catch it - so normalise here rather than trusting every producer.
                    call.StartedAt = call.StartedAt.ToUniversalTime();
                    call.CompletedAt = call.CompletedAt.ToUniversalTime();
                }
                db.LlmCalls.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception e)
            {
                // A failed write must never take the writer down: the next batch should still get a
                // chance, and nothing here may affect the calls being measured.
                logger.LogWarning(e, "LLM usage: could not persist {Count} record(s).", batch.Count);
            }
        }
    }
}
```

- [ ] **Step 4: Register it**

In `src/Diariz.Api/Program.cs`, after the sink registrations from Task 5:

```csharp
builder.Services.AddHostedService<LlmUsageWriter>();
```

- [ ] **Step 5: Run to verify the batch tests pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageBatchTests"
```

Expected: 2 passed. `PlatformSettings.LlmUsageLoggingEnabled` already exists - it was added in Task 6, which is why settings come before the writer.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/LlmUsageWriter.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/LlmUsageSinkTests.cs
git commit -m "feat(llm-usage): persist recorded calls from a background writer"
```

---

### Task 8: Push scopes in the background processors

**Files:**
- Modify: `SummarizationProcessor.cs`, `SectionSummaryProcessor.cs`, `MeetingMinutesProcessor.cs`, `SectionMinutesProcessor.cs`, `ActionsProcessor.cs`, `TagsProcessor.cs`, `EmbeddingProcessor.cs`, `FormulaRunProcessor.cs` (all in `src/Diariz.Api/Services/`)
- Test: `tests/Diariz.Api.Tests/SummarizationProcessorTests.cs` and the equivalent existing test file per processor

**Interfaces:**
- Consumes: `LlmCallScope.Push` (Task 2).
- Produces: nothing new; each processor's LLM calls become attributed.

- [ ] **Step 1: Write the failing test for the summarisation processor**

Add to `tests/Diariz.Api.Tests/SummarizationProcessorTests.cs`. The assertion reads the ambient scope **from inside the fake client**, which is the only place that proves the scope is active at the moment the call is made:

```csharp
    [Fact]
    public async Task ProcessAsync_AttributesTheCall_ToTheRecordingAndItsOwner()
    {
        await using var db = TestDb.Create();
        // ... arrange a recording + transcription + segments exactly as the neighbouring tests do ...

        LlmCallKind? observedKind = null;
        Guid? observedRecording = null;
        Guid? observedUser = null;
        var client = new FakeSummarizationClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedRecording = LlmCallScope.Active?.RecordingId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await SummarizationProcessor.ProcessAsync(/* ... as the neighbouring tests ... */);

        Assert.Equal(LlmCallKind.Summarize, observedKind);
        Assert.Equal(rec.Id, observedRecording);
        Assert.Equal(rec.UserId, observedUser);
    }
```

Extend the existing fake summarisation client in `tests/Diariz.Api.TestSupport/Fakes.cs` with an optional `Action? onCall` invoked at the top of `SummarizeAsync`, rather than creating a second fake.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationProcessorTests"
```

Expected: FAIL - `observedKind` is null because no scope is pushed.

- [ ] **Step 3: Push the scope**

In `SummarizationProcessor.ProcessAsync`, immediately after the `rec is null` guard (line ~27), before the `try`:

```csharp
        // Attribute every model call this job makes. Pushed once here rather than at the client, so the
        // handler deep inside HttpClient can record who asked and why.
        using var llm = LlmCallScope.Push(
            LlmCallKind.Summarize, rec.UserId, await OwnerEmailAsync(db, rec.UserId, ct),
            rec.Id, rec.Name ?? rec.Title);
```

Add a small private helper that reads the owner's email once:

```csharp
    private static Task<string?> OwnerEmailAsync(DiarizDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationProcessorTests"
```

Expected: all pass.

- [ ] **Step 5: Commit, then repeat Steps 1-4 for each remaining processor**

Same shape each time, with these values. Put the `using var llm = ...` line as early as the needed identifiers are available, and **never inside a loop**:

| Processor | Kind | Target |
|---|---|---|
| `SectionSummaryProcessor` | `SectionSummary` | `sectionId` + section name |
| `SectionMinutesProcessor` | `SectionMinutes` | `sectionId` + section name |
| `MeetingMinutesProcessor` | `MeetingMinutes` | `recordingId` + `Name ?? Title` |
| `ActionsProcessor` | `ExtractActions` | `recordingId` + `Name ?? Title` |
| `TagsProcessor` | `Tags` | `recordingId` + `Name ?? Title` |
| `EmbeddingProcessor` | `Embedding` | `recordingId` + `Name ?? Title` |
| `FormulaRunProcessor` | `FormulaRun` | `recordingId` + `Name ?? Title` |

`MeetingTypeMinutesGenerator` and its two strategies get **no** push - their calls belong to the enclosing `MeetingMinutes` operation, and that is exactly how the per-section fan-out gets counted as turns. Add a one-line comment there saying so, so a future reader does not "fix" it.

Commit after each processor:

```bash
git add src/Diariz.Api/Services/<Processor>.cs tests/Diariz.Api.Tests/<Processor>Tests.cs
git commit -m "feat(llm-usage): attribute <kind> calls"
```

---

### Task 9: Push scopes in the controllers and search

**Files:**
- Modify: `src/Diariz.Api/Controllers/ChatController.cs`, `src/Diariz.Api/Controllers/RecordingTranslationController.cs`, `src/Diariz.Api/Controllers/RecordingActionsController.cs`, `src/Diariz.Api/Services/TranscriptSearch.cs:184`
- Test: the existing test file for each

**Interfaces:**
- Consumes: `LlmCallScope.Push` (Task 2).
- Produces: nothing new.

- [ ] **Step 1: Write the failing chat test**

In the existing chat controller test file, assert the ambient scope from inside the fake chat stream client:

```csharp
    [Fact]
    public async Task Stream_AttributesEveryModelRoundTrip_ToOneChatOperation()
    {
        // ChatToolOrchestrator loops without pushing its own scope, so all of a turn's round-trips share
        // this operation - which is what makes MAX(Sequence) the turn count.
        var seenOperations = new List<Guid>();
        var seenKinds = new List<LlmCallKind>();
        var client = new FakeChatStreamClient(onCall: () =>
        {
            seenOperations.Add(LlmCallScope.Active!.OperationId);
            seenKinds.Add(LlmCallScope.Active!.Kind);
        });

        // ... invoke the streaming chat action as the neighbouring tests do, with a tool call forcing
        // two round-trips ...

        Assert.Equal(2, seenOperations.Count);              // the tool call forced a second round-trip
        Assert.Single(seenOperations.Distinct());           // both belong to ONE operation
        Assert.All(seenKinds, k => Assert.Equal(LlmCallKind.ChatMessage, k));
    }
```

Adapt the assertion to the existing fake's shape; the load-bearing part is that both round-trips report the **same** `OperationId`.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatController"
```

Expected: FAIL - `LlmCallScope.Active` is null.

- [ ] **Step 3: Push the scopes**

- `ChatController`, streaming chat action: `LlmCallScope.Push(LlmCallKind.ChatMessage, userId, userEmail, recordingId, recordingTitle)` where a recording is in context, else without. It must wrap the **whole** orchestrator enumeration, not just its first call - place the `using` before the loop that consumes the async enumerable.
- `ChatController`, dictation action: `LlmCallScope.Push(LlmCallKind.Dictation, userId, userEmail)`.
- `RecordingTranslationController`: `LlmCallScope.Push(LlmCallKind.Translation, userId, userEmail, rec.Id, rec.Name ?? rec.Title)`.
- `RecordingActionsController` (synchronous re-extract): `LlmCallScope.Push(LlmCallKind.ExtractActions, userId, userEmail, rec.Id, rec.Name ?? rec.Title)`.
- `TranscriptSearch` around line 184, wrapping the `EmbedAsync` call: `LlmCallScope.Push(LlmCallKind.SearchQuery, userId, userEmail)`. Separate from `Embedding` because search is user-interactive and high-frequency; folding it into indexing volume would hide both.

The controllers already read the user id from the JWT `NameIdentifier` claim - reuse that, do not re-derive it.

- [ ] **Step 4: Run to verify each passes**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: whole unit suite green, no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/ChatController.cs src/Diariz.Api/Controllers/RecordingTranslationController.cs src/Diariz.Api/Controllers/RecordingActionsController.cs src/Diariz.Api/Services/TranscriptSearch.cs tests/Diariz.Api.Tests
git commit -m "feat(llm-usage): attribute chat, dictation, translation and search calls"
```

---

### Task 10: Nightly retention sweep

**Files:**
- Create: `src/Diariz.Api/Services/LlmUsageRetention.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/LlmUsageRetentionTests.cs`, `tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs`

**Interfaces:**
- Consumes: `PlatformSettings.LlmUsageRetentionDays` (Task 6), `AudioRetentionSchedule.NextRun` (existing, reused).
- Produces: `static class LlmUsageRetentionSweep { static Task<int> RunAsync(DiarizDbContext db, DateTimeOffset nowUtc, int retentionDays, ILogger logger, CancellationToken ct) }`; `class LlmUsageRetentionWorker : BackgroundService`.

- [ ] **Step 1: Write the failing integration test**

The sweep uses `ExecuteDeleteAsync`, which the in-memory provider does not translate, so this belongs in the integration project. Append to `LlmUsageIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task RetentionSweep_DeletesOnlyRowsOlderThanTheWindow()
    {
        await using var db = fx.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var marker = Guid.NewGuid();

        db.LlmCalls.AddRange(
            Row(marker, now.AddDays(-100)),
            Row(marker, now.AddDays(-91)),
            Row(marker, now.AddDays(-89)),
            Row(marker, now));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, now, retentionDays: 90, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(2, await db.LlmCalls.CountAsync(c => c.OperationId == marker));
    }

    [Fact]
    public async Task RetentionSweep_DeletesNothing_WhenRetentionIsZero()
    {
        // 0 means keep forever. Treating it as "delete everything older than today" would silently
        // destroy the whole log the first night after an admin typed 0 meaning "no limit".
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker, DateTimeOffset.UtcNow.AddYears(-5)));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, DateTimeOffset.UtcNow, retentionDays: 0, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await db.LlmCalls.CountAsync(c => c.OperationId == marker));
    }

    private static LlmCall Row(Guid operationId, DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid(), OperationId = operationId, Sequence = 1, Kind = LlmCallKind.Tags,
        Model = "m", Endpoint = "http://x/v1", StartedAt = startedAt, CompletedAt = startedAt,
        DurationMs = 1, Success = true,
    };
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmUsageIntegrationTests"
```

Expected: `The name 'LlmUsageRetentionSweep' does not exist`.

- [ ] **Step 3: Implement**

`src/Diariz.Api/Services/LlmUsageRetention.cs`:

```csharp
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Deletes usage rows past the retention window. Set-based (ExecuteDeleteAsync) because this
/// table gets a row per LLM call and loading a month of them to delete them would be absurd.</summary>
public static class LlmUsageRetentionSweep
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, DateTimeOffset nowUtc, int retentionDays, ILogger logger,
        CancellationToken ct = default)
    {
        // 0 means keep forever. Without this guard it would mean "delete everything older than now",
        // which destroys the log the first night after an admin types 0 meaning "no limit".
        if (retentionDays <= 0) return 0;

        var cutoff = nowUtc.AddDays(-retentionDays).ToUniversalTime();
        var deleted = await db.LlmCalls.Where(c => c.StartedAt < cutoff).ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation("LLM usage retention: deleted {Deleted} row(s) older than {Days}d.",
                deleted, retentionDays);
        return deleted;
    }
}

/// <summary>Runs the sweep once a day at the same server-local time as the audio-retention job, reusing
/// its schedule helper. Opens its own DI scope per run because the host is a singleton.</summary>
public class LlmUsageRetentionWorker(IServiceProvider services, ILogger<LlmUsageRetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeOnly timeOfDay;
            try
            {
                using var scope = services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>();
                timeOfDay = (await settings.GetAsync(stoppingToken)).AudioDeletionTimeOfDay;
            }
            catch (Exception e)
            {
                logger.LogError(e, "LLM usage retention: could not read settings; retrying in 1h.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            var next = AudioRetentionSchedule.NextRun(DateTimeOffset.Now, timeOfDay);
            try
            {
                await Task.Delay(next - DateTimeOffset.Now, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            try
            {
                using var scope = services.CreateScope();
                var settings = await scope.ServiceProvider
                    .GetRequiredService<IPlatformSettingsService>().GetAsync(stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                await LlmUsageRetentionSweep.RunAsync(
                    db, DateTimeOffset.UtcNow, settings.LlmUsageRetentionDays, logger, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "LLM usage retention sweep failed.");
            }
        }
    }
}
```

Register in `Program.cs` beside the other hosted services:

```csharp
builder.Services.AddHostedService<LlmUsageRetentionWorker>();
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmUsageIntegrationTests"
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/LlmUsageRetention.cs src/Diariz.Api/Program.cs tests/Diariz.Api.IntegrationTests/LlmUsageIntegrationTests.cs
git commit -m "feat(llm-usage): sweep usage rows past the retention window nightly"
```

---

### Task 11: End-to-end verification in the running stack

**Files:** none changed unless a defect is found.

- [ ] **Step 1: Run the whole suite**

```bash
dotnet build Diariz.slnx && dotnet test
```

Expected: green, with **no warnings** - a passing run in this repo has pristine output.

- [ ] **Step 2: Bring up the stack and produce a summary**

```bash
cd deploy && docker compose up --build -d
```

Sign in, upload a short recording, let it summarise. Note: the web app uses **axios/XHR**, not `fetch`.

- [ ] **Step 3: Confirm rows landed with real attribution**

```bash
docker compose exec postgres psql -U diariz -d diariz -c "SELECT \"Kind\", \"UserEmail\", \"Model\", \"DurationMs\", \"PromptTokens\", \"CompletionTokens\", \"Sequence\" FROM \"LlmCalls\" ORDER BY \"StartedAt\" DESC LIMIT 20;"
```

Expected: rows with `Kind` = 1 (`Summarize`), 7 (`Tags`), 6 (`ExtractActions`), 10 (`Embedding`) as applicable - the real owner email, a real model name, plausible durations. **Any row with `Kind` = 0 (`Unknown`) means a push site was missed** - find it and fix it before moving on.

- [ ] **Step 4: Confirm chat rows appear with null tokens**

Send a chat message, then re-run the query. Expect `Kind` = 12 with null token columns and a **short** duration: streaming is not measured properly until PR 2, and this is the known gap that PR 2 closes. Record the observed duration in the PR description so the improvement in PR 2 is demonstrable.

- [ ] **Step 5: Confirm the master switch works**

Turn "Log LLM usage" off in the platform AI settings, trigger a summary, and confirm no new row appears. Turn it back on.

---

### Task 12: Docs, version bump and release notes

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`, `apps/web/src/lib/releases.ts`, `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the version to 0.216.0 in all five places**

`version.json` is canonical; the other four are mirrors. `apps/web/src/lib/versionMirrors.test.ts` fails the build if any drifts - it exists because the n8n node silently sat at `0.1.0` for ~70 releases, and an npm version cannot be corrected once published.

- [ ] **Step 2: Update `docs/Data_Schema.md`**

Add the `LlmCalls` table with every column, the three indexes, the three `SET NULL` foreign keys, and the note that user/recording/section links are paired with denormalised snapshots so rows survive deletion. Add the three new `PlatformSettings` columns. Add `AddLlmCalls` and `AddLlmUsageSettings` to the migration-history table, noting both are additive and forward-restore-safe (so `CurrentFormat` is unchanged).

- [ ] **Step 3: Update `docs/Overall_Synopsis_of_Platform.md`**

Document the capture contract: `LlmCallScope` (AsyncLocal, pushed per job) -> `LlmTelemetryHandler` -> bounded channel -> `LlmUsageWriter` -> `LlmCalls`, plus the nightly retention worker. State explicitly that no prompt or completion content is stored.

- [ ] **Step 4: Add the release-notes entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version: "0.216.0"`, today's date, the PR number, a headline, a prose `summary`, and `added` bullets. **Plain hyphens only.**

The `pr` field needs a number that does not exist until the PR is opened, and guessing "last + 1" fails because Dependabot PRs and issues share the sequence. So: push the branch, run `gh pr create`, read the real number, then amend this entry and push again.

`CAPABILITIES`, the README Features table and `docs/features.md` are **not** touched in this PR - nothing is user-visible yet. They land in PR 3, with the viewer. Say so in the PR description.

- [ ] **Step 5: Verify the version tests pass**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: pass.

- [ ] **Step 6: Commit and open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md
git commit -m "chore(release): 0.216.0 - LLM usage capture and storage"
git push -u origin feat/llm-usage-logging
gh pr create --title "feat(llm-usage): capture every LLM call to the database" --body "..."
```

The PR body must state: **deployment surface is a server redeploy only** (nothing under `apps/desktop` is touched), that the migrations are additive and forward-restore-safe, and that streaming calls (chat, formula runs) still have null token counts until PR 2.

Then amend `releases.ts` with the real PR number and push again.

---

## Self-Review

**Spec coverage:** Entity and migration (Task 1); scope (2); reasoning tokens (3); channel sink (4); handler capture including failures, privacy and endpoint scrubbing (5); the three platform settings (6); background writer with its own DI scope (7); all twelve push sites (8, 9); retention (10); live verification (11); docs, version, release notes (12). Streaming capture, the API and the viewer are explicitly PRs 2 and 3.

**Deviation from the spec, deliberate:** the spec says the handler skips the channel when `LlmUsageLoggingEnabled` is off. This plan enforces the switch in the **writer** instead, so the LLM call path never pays for a settings lookup. Records made while logging is off are drained and discarded. The spec has been amended to match.

**Type consistency:** `LlmCallKind`, `LlmCall`, `LlmCallScope.Push`/`.Active`/`.NextSequence()`, `ILlmUsageSink.Record`, `ChannelLlmUsageSink.Reader`/`.Dropped`/`.Capacity`, `LlmUsageBatch.DrainAsync`, `LlmUsageRetentionSweep.RunAsync` are each defined once and used consistently. `LlmUsage` gains `ReasoningTokens` as the **last** positional member so existing three-argument construction still compiles.

**Known adaptation points** (the plan says so at each site rather than inventing an API): the `ContainersFixture` injection style and `Users.Seed` signature in Task 1; the existing stub `HttpMessageHandler` in Task 5; each processor's own test arrangement in Task 8; the fake chat stream client's shape in Task 9.
