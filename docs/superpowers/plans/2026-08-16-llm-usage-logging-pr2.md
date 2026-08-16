# LLM Usage Logging - PR 2 (streaming capture) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give streamed LLM calls (chat and formula runs) real token counts, a real duration, and a time-to-first-token - closing the one gap PR 1 shipped with.

**Architecture:** Ask the server for usage on streamed responses via `stream_options`, and have `LlmTelemetryHandler` wrap the SSE response body in a pass-through stream that observes the first byte, scans for the final usage chunk, and completes the usage record when the stream ends - without ever buffering it.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-16-llm-usage-logging-design.md` (the "Streaming" section)
**Predecessor:** PR 1 shipped as 0.216.0 + 0.216.1 and is merged.

## What PR 1 left behind

Streamed calls currently record `Streamed = true`, a correct `Kind`/user/target, and **null token counts** with an **understated duration**. The duration is wrong because `ChatStreamClient` sends with `HttpCompletionOption.ResponseHeadersRead`, so `base.SendAsync` returns as soon as the headers arrive - the handler stops its clock there, measuring time-to-headers rather than the call. Live verification observed a chat turn recorded as 20 ms.

## Verified facts - do not re-derive these

These were checked against the running system before this plan was written. Treat them as given.

1. **LM Studio honours `stream_options: {"include_usage": true}`.** A live probe against the configured endpoint (`openai/gpt-oss-20b`) returned, immediately before `data: [DONE]`:
   ```
   data: {"id":"...","object":"chat.completion.chunk","choices":[],
          "usage":{"prompt_tokens":69,"completion_tokens":5,"total_tokens":74,
                   "completion_tokens_details":{"reasoning_tokens":2}}}
   ```
2. **That chunk's shape is exactly what `LlmUsageParser.TryParse` already reads** - a root object with a `usage` property, including `completion_tokens_details.reasoning_tokens` (added in PR 1, Task 3). No new parser is needed; reuse it.
3. **The usage chunk cannot break chat.** Both `ChatStreamClient.ParseStreamLine` and `.ParseStreamChunk` do `choices.EnumerateArray().FirstOrDefault()` and then require `ValueKind == JsonValueKind.Object`. For the empty `choices: []` array that comes with the usage chunk, `FirstOrDefault()` returns `default(JsonElement)` whose kind is `Undefined`, so both return null and ignore it. Verified by reading both methods.
4. **`ChatStreamClient` is the ONLY streaming LLM client.** `IcsCalendarClient` and `UrlFetcher` also use `ResponseHeadersRead` but are not LLM clients and are not registered through `AddLlmClient`, so the telemetry handler never sees them.
5. **`PlatformSettings.LlmStreamUsageEnabled` already exists** (bool, default true), shipped inert in PR 1 with a UI hint saying it takes effect in a future release. This PR makes it live and that hint must be removed.
6. **`SummarizationSettingsResolver` already reads `PlatformSettings`** (for `LlmTimeoutSeconds`), so the toggle reaches the client by the identical path.

## Global Constraints

- **TDD is mandatory.** Failing test first, run it, watch it fail with the expected message, then implement.
- **Every task must include a mutation check** on at least its most load-bearing new test: break the production code, observe the named test fail, paste the failure output, restore. If a test cannot be made to fail, say so plainly rather than moving on. Two genuine defects in PR 1 were caught only this way.
- **NEVER buffer a streaming response.** Buffering SSE would hold every chat token until the model finished, leaving the chat UI silent. The pass-through stream must forward bytes as they arrive and keep only a bounded partial-line buffer. There is an existing test, `DoesNotBufferAStreamingResponse`, guarding this - it must keep passing, and must not be weakened.
- **Telemetry must never break the call it measures.** Any failure to scan, parse or record costs a null, never the call or the stream.
- **Never store prompt or completion content.** Counts and sizes only. The scanner sees content deltas as they pass; it must retain none of them.
- **No em/en dashes in user-facing text** (UI strings, i18n catalogs, release notes). Plain hyphen `-` only.
- **No mocking library.** Fakes go in `tests/Diariz.Api.TestSupport` (namespace `Diariz.Api.Tests.Infrastructure`).
- **Never `git add -A`.** Stage explicit paths - this repo has untracked scratch files that have polluted a PR before.
- **Build `Diariz.slnx` before pushing**, not just the unit test project.
- **`dotnet test --filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- Target version: **`0.217.0`** (functional enhancement: Minor +1, Build reset), assuming nothing else merges first.
- Deployment surface: **server redeploy only** - nothing under `apps/desktop` is touched.

---

## File Structure

**Create:**
- `src/Diariz.Api/Services/SseUsageScanner.cs` - pure incremental scanner for the usage chunk
- `src/Diariz.Api/Services/ObservingStream.cs` - pass-through stream that reports first byte, bytes, and completion
- `tests/Diariz.Api.Tests/SseUsageScannerTests.cs`
- `tests/Diariz.Api.Tests/ObservingStreamTests.cs`

**Modify:**
- `src/Diariz.Api/Services/SummarizationSettingsResolver.cs` - config gains `IncludeStreamUsage`
- `src/Diariz.Api/Services/ChatStreamClient.cs` - send `stream_options` on both request builders
- `src/Diariz.Api/Services/LlmTelemetry.cs` - defer the record for SSE responses; measure to stream end
- `tests/Diariz.Api.Tests/ChatStreamClientTests.cs`, `LlmTelemetryTests.cs`
- `apps/web/src/locales/*/account.json` - drop the "future release" hint clause

---

### Task 1: The toggle reaches the client

**Files:**
- Modify: `src/Diariz.Api/Services/SummarizationSettingsResolver.cs`
- Test: `tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs`

**Interfaces:**
- Consumes: `PlatformSettings.LlmStreamUsageEnabled` (exists).
- Produces: `SummarizationRequestConfig.IncludeStreamUsage` (bool, init, default `true`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs`, matching how the neighbouring tests build the resolver and seed `PlatformSettings`:

```csharp
    [Fact]
    public async Task Resolve_CarriesTheStreamUsageToggle_FromPlatformSettings()
    {
        await using var db = TestDb.Create();
        // ... seed a user and a PlatformSettings row with LlmStreamUsageEnabled = false,
        //     exactly as the neighbouring LlmTimeoutSeconds tests do ...

        var cfg = await resolver.ResolveAsync(userId);

        Assert.False(cfg.IncludeStreamUsage);
    }

    [Fact]
    public async Task Resolve_DefaultsStreamUsageToTrue_WhenThereAreNoPlatformSettings()
    {
        // A hand-built config in a test, or a deployment with no settings row yet, must ask for usage
        // rather than silently losing token counts. True is the shipped default.
        await using var db = TestDb.Create();
        // ... seed a user, no PlatformSettings row ...

        var cfg = await resolver.ResolveAsync(userId);

        Assert.True(cfg.IncludeStreamUsage);
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationSettingsResolverTests"
```

Expected: compile failure, `'SummarizationRequestConfig' does not contain a definition for 'IncludeStreamUsage'`.

- [ ] **Step 3: Add the property and wire it**

In `src/Diariz.Api/Services/SummarizationSettingsResolver.cs`, add to the record (an init property, NOT a positional parameter - positional would break every hand-built config in the test suite):

```csharp
    /// <summary>Whether streamed requests ask the server for token counts via
    /// <c>stream_options.include_usage</c>. A toggle rather than a constant because an OpenAI-compatible
    /// endpoint that rejects the unknown field must be recoverable without a redeploy. Defaults true so a
    /// hand-built config (tests, fakes) still asks for usage.</summary>
    public bool IncludeStreamUsage { get; init; } = true;
```

And in the object initialiser the resolver returns, alongside `ReasoningEffort` and `ContextCharBudget`:

```csharp
            IncludeStreamUsage = ps?.LlmStreamUsageEnabled ?? true,
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SummarizationSettingsResolverTests"
```

- [ ] **Step 5: Mutation check**

Change the wiring to `IncludeStreamUsage = true,` (ignoring settings). Re-run. The first test must FAIL. Paste the output. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/SummarizationSettingsResolver.cs tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs
git commit -m "feat(llm-usage): carry the stream-usage toggle on the request config"
```

---

### Task 2: Ask the server for usage

**Files:**
- Modify: `src/Diariz.Api/Services/ChatStreamClient.cs` (both request builders: `StreamChunksAsync` ~line 100, `SendAsync` ~line 147)
- Test: `tests/Diariz.Api.Tests/ChatStreamClientTests.cs`

**Interfaces:**
- Consumes: `SummarizationRequestConfig.IncludeStreamUsage` (Task 1).
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

You need to observe the request body. Check `ChatStreamClientTests.cs` for how it already stubs the transport and reuse that; if it has no way to capture the request, add a small capturing `HttpMessageHandler` in that test file.

```csharp
    [Fact]
    public async Task StreamChunks_AsksForUsage_WhenTheToggleIsOn()
    {
        var captured = await CaptureRequestBodyAsync(cfg with { IncludeStreamUsage = true }, useTools: true);

        using var doc = JsonDocument.Parse(captured);
        Assert.True(doc.RootElement.TryGetProperty("stream_options", out var so));
        Assert.True(so.GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task StreamChunks_OmitsTheFieldEntirely_WhenTheToggleIsOff()
    {
        // Omitted, not sent as false: the whole point of the toggle is recovering from an endpoint that
        // rejects an unknown field, and a server that chokes on the key will choke on it either way.
        var captured = await CaptureRequestBodyAsync(cfg with { IncludeStreamUsage = false }, useTools: true);

        using var doc = JsonDocument.Parse(captured);
        Assert.False(doc.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task Stream_AsksForUsage_Too()
    {
        // The plain (no-tools) path is a SEPARATE request builder in this client. PR 1 shipped a bug of
        // exactly this shape - a field added to one builder and not its twin.
        var captured = await CaptureRequestBodyAsync(cfg with { IncludeStreamUsage = true }, useTools: false);

        using var doc = JsonDocument.Parse(captured);
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Theory]
    [InlineData("""data: {"id":"x","choices":[],"usage":{"prompt_tokens":69,"completion_tokens":5,"total_tokens":74}}""")]
    public void TheUsageChunk_IsIgnoredByBothParsers_AndNeverEndsTheStreamEarly(string line)
    {
        // Turning stream_options on makes the server emit a chunk with an EMPTY choices array. If either
        // parser mishandled that, enabling this feature would break chat for every user. This test is the
        // guard on that blast radius.
        Assert.Null(ChatStreamClient.ParseStreamLine(line, out var done1));
        Assert.False(done1);
        Assert.Null(ChatStreamClient.ParseStreamChunk(line, out var done2));
        Assert.False(done2);
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatStreamClientTests"
```

Expected: the three `stream_options` tests FAIL (property missing). The parser theory should PASS already - it documents existing behaviour rather than driving a change. If it FAILS, stop and tell the controller: that would mean enabling this feature breaks chat, and the plan needs to change.

- [ ] **Step 3: Send the field**

In BOTH `StreamChunksAsync`'s body (~line 100) and `SendAsync`'s body (~line 147), after the `reasoning_effort` line:

```csharp
        // Ask the server to append a final usage chunk after the content. Omitted entirely when off, so an
        // endpoint that rejects the unknown field can be recovered with a settings change, not a redeploy.
        if (config.IncludeStreamUsage) body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatStreamClientTests"
```

- [ ] **Step 5: Mutation check**

Remove the line from `SendAsync` only (leaving `StreamChunksAsync`). Re-run. `Stream_AsksForUsage_Too` must FAIL while the others pass. Paste the output. Restore. This proves the twin-builder coverage is real.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/ChatStreamClient.cs tests/Diariz.Api.Tests/ChatStreamClientTests.cs
git commit -m "feat(llm-usage): request token counts on streamed completions"
```

---

### Task 3: The SSE usage scanner (pure)

**Files:**
- Create: `src/Diariz.Api/Services/SseUsageScanner.cs`
- Test: `tests/Diariz.Api.Tests/SseUsageScannerTests.cs`

**Interfaces:**
- Consumes: `LlmUsageParser.TryParse` (exists).
- Produces: `sealed class SseUsageScanner` with `void Feed(ReadOnlySpan<byte> bytes)`, `LlmUsage? Usage { get; }`, and `const int MaxLineBytes = 64 * 1024`.

This is the piece that must be separable and pure, so it can be tested without a stream, a handler or a network - the same reasoning that put `_shape_segments` outside the model calls in the Python worker.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SseUsageScannerTests
{
    private static void FeedAll(SseUsageScanner scanner, string text) =>
        scanner.Feed(Encoding.UTF8.GetBytes(text));

    private const string UsageChunk =
        """data: {"id":"x","choices":[],"usage":{"prompt_tokens":69,"completion_tokens":5,"total_tokens":74,"completion_tokens_details":{"reasoning_tokens":2}}}""";

    [Fact]
    public void FindsTheUsageChunk_InAFullStream()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" + UsageChunk + "\n\ndata: [DONE]\n\n");

        Assert.Equal(69, scanner.Usage!.Value.PromptTokens);
        Assert.Equal(5, scanner.Usage!.Value.CompletionTokens);
        Assert.Equal(2, scanner.Usage!.Value.ReasoningTokens);
    }

    [Fact]
    public void FindsIt_WhenTheChunkIsSplitAcrossFeeds()
    {
        // The whole reason this is byte-oriented: a chunk WILL be split across socket reads, and a scanner
        // that only handled whole lines would silently miss usage on exactly the large responses that
        // matter most.
        var scanner = new SseUsageScanner();
        var bytes = Encoding.UTF8.GetBytes(UsageChunk + "\n\n");
        scanner.Feed(bytes.AsSpan(0, 30));
        scanner.Feed(bytes.AsSpan(30));

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void FindsIt_WhenSplitMidMultiByteCharacter()
    {
        // Content deltas carry arbitrary UTF-8. A split inside a multi-byte sequence must not corrupt the
        // buffer or throw - it must simply resolve once the rest arrives.
        var scanner = new SseUsageScanner();
        var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"éèê\"}}]}\n\n" + UsageChunk + "\n\n");
        for (var i = 0; i < bytes.Length; i++) scanner.Feed(bytes.AsSpan(i, 1)); // one byte at a time

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void KeepsTheLastUsage_WhenMoreThanOneAppears()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, """data: {"usage":{"total_tokens":10}}""" + "\n\n" + """data: {"usage":{"total_tokens":99}}""" + "\n\n");

        Assert.Equal(99, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void UsageIsNull_WhenTheStreamNeverReportsAny()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");

        Assert.Null(scanner.Usage);
    }

    [Fact]
    public void AbsurdlyLongLine_DoesNotGrowTheBufferWithoutBound()
    {
        // A hostile or broken endpoint must not be able to make a telemetry scanner exhaust memory. The
        // scanner drops an over-long line and resynchronises at the next newline.
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: " + new string('x', SseUsageScanner.MaxLineBytes * 2) + "\n\n" + UsageChunk + "\n\n");

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens); // resynchronised and still found it
    }

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        var scanner = new SseUsageScanner();
        var ex = Record.Exception(() => FeedAll(scanner, "data: not json\n\n\0\0\0\ndata: {\"usage\":\n"));
        Assert.Null(ex);
    }

    [Fact]
    public void RetainsNoContent()
    {
        // The scanner sees every content delta go past. It must keep none of them - this table is browsed
        // by an administrator and meeting content must never reach it.
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"the secret merger closes friday\"}}]}\n\n" + UsageChunk + "\n\n");

        var serialized = System.Text.Json.JsonSerializer.Serialize(scanner.Usage);
        Assert.DoesNotContain("secret merger", serialized);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SseUsageScannerTests"
```

Expected: `The name 'SseUsageScanner' does not exist`.

- [ ] **Step 3: Implement**

`src/Diariz.Api/Services/SseUsageScanner.cs`:

```csharp
using System.Text;

namespace Diariz.Api.Services;

/// <summary>Incrementally scans an SSE byte stream for the final <c>usage</c> chunk that
/// <c>stream_options.include_usage</c> appends after the content.
///
/// Byte-oriented and incremental because a chunk WILL be split across socket reads - and split inside a
/// multi-byte UTF-8 sequence - so a line-oriented scanner would silently miss usage on exactly the long
/// responses whose cost matters most.
///
/// Pure and separable from the stream and the handler so it can be tested without either.
///
/// Retains NO content: the only state kept between feeds is a bounded partial line and the last parsed
/// token counts.</summary>
public sealed class SseUsageScanner
{
    /// <summary>Longest line retained before the scanner gives up on it and resynchronises at the next
    /// newline. A telemetry scanner must not let a hostile or broken endpoint exhaust memory.</summary>
    public const int MaxLineBytes = 64 * 1024;

    private readonly MemoryStream _line = new();
    private bool _skippingOverlongLine;

    /// <summary>Token counts from the most recent usage chunk seen, or null if none has appeared.</summary>
    public LlmUsage? Usage { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        try
        {
            foreach (var b in bytes)
            {
                if (b == (byte)'\n')
                {
                    if (!_skippingOverlongLine) TryParseLine();
                    _line.SetLength(0);
                    _skippingOverlongLine = false;
                    continue;
                }
                if (_skippingOverlongLine) continue;
                if (_line.Length >= MaxLineBytes)
                {
                    // Drop it and wait for the next newline rather than growing without bound.
                    _line.SetLength(0);
                    _skippingOverlongLine = true;
                    continue;
                }
                _line.WriteByte(b);
            }
        }
        catch (Exception)
        {
            // This runs on the chat hot path. Losing a token count is acceptable; throwing into the middle
            // of someone's streamed answer is not.
            _line.SetLength(0);
            _skippingOverlongLine = false;
        }
    }

    private void TryParseLine()
    {
        if (_line.Length == 0) return;

        var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
        if (!text.StartsWith("data:", StringComparison.Ordinal)) return;

        var data = text["data:".Length..].Trim();
        // Cheap pre-filter: the overwhelming majority of lines are content deltas, and parsing every one
        // of them as JSON would put a real cost on every token of every chat.
        if (data.Length == 0 || data[0] != '{' || !data.Contains("\"usage\"", StringComparison.Ordinal)) return;

        if (LlmUsageParser.TryParse(data, out var usage)) Usage = usage;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SseUsageScannerTests"
```

Expected: 8 passed.

- [ ] **Step 5: Mutation check**

Replace the byte loop's partial-line handling with one that only parses complete lines received in a single `Feed` (i.e. delete `_line` and parse `bytes` directly). Re-run. `FindsIt_WhenTheChunkIsSplitAcrossFeeds` and `FindsIt_WhenSplitMidMultiByteCharacter` must FAIL. Paste the output. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/SseUsageScanner.cs tests/Diariz.Api.Tests/SseUsageScannerTests.cs
git commit -m "feat(llm-usage): scan streamed responses for the final usage chunk"
```

---

### Task 4: The observing pass-through stream

**Files:**
- Create: `src/Diariz.Api/Services/ObservingStream.cs`
- Test: `tests/Diariz.Api.Tests/ObservingStreamTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `sealed class ObservingStream : Stream` with constructor `(Stream inner, Action<ReadOnlyMemory<byte>> onBytes, Action onFirstByte, Action onCompleted)`. `onCompleted` fires **exactly once**, on end-of-stream, dispose, or fault, whichever comes first.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ObservingStreamTests
{
    private static ObservingStream Wrap(
        Stream inner, List<byte> seen, Action? onFirst = null, Action? onDone = null) =>
        new(inner, b => seen.AddRange(b.ToArray()), onFirst ?? (() => { }), onDone ?? (() => { }));

    [Fact]
    public async Task ForwardsEveryByte_Unchanged()
    {
        var payload = Encoding.UTF8.GetBytes("hello streamed world");
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(payload), seen);

        using var output = new MemoryStream();
        await s.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray()); // the caller still gets everything
        Assert.Equal(payload, seen.ToArray());   // and the observer saw everything
    }

    [Fact]
    public async Task ReportsTheFirstByteOnce_NotOncePerRead()
    {
        var firstByteCalls = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abcdef")), seen,
            onFirst: () => firstByteCalls++);

        var buffer = new byte[2];
        while (await s.ReadAsync(buffer) > 0) { }

        Assert.Equal(1, firstByteCalls);
    }

    [Fact]
    public async Task DoesNotReportAFirstByte_ForAnEmptyStream()
    {
        var firstByteCalls = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream([]), seen, onFirst: () => firstByteCalls++);

        Assert.Equal(0, await s.ReadAsync(new byte[4]));
        Assert.Equal(0, firstByteCalls);
    }

    [Fact]
    public async Task CompletesOnEndOfStream()
    {
        var done = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abc")), seen, onDone: () => done++);

        var buffer = new byte[8];
        while (await s.ReadAsync(buffer) > 0) { }

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesOnDispose_WhenTheReaderAbandonsTheStreamEarly()
    {
        // A chat client that stops at [DONE], or a browser that disconnects mid-answer, never reaches EOF.
        // Without this the record for an abandoned turn would never be written at all.
        var done = 0;
        var seen = new List<byte>();
        var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abcdefghij")), seen, onDone: () => done++);

        await s.ReadAsync(new byte[2]);
        await s.DisposeAsync();

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesExactlyOnce_EvenWhenReadToEndAndThenDisposed()
    {
        var done = 0;
        var seen = new List<byte>();
        var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abc")), seen, onDone: () => done++);

        var buffer = new byte[8];
        while (await s.ReadAsync(buffer) > 0) { }
        await s.DisposeAsync();

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesOnFault_AndStillLetsTheExceptionThrough()
    {
        var done = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new ThrowingStream(), seen, onDone: () => done++);

        await Assert.ThrowsAsync<IOException>(async () => await s.ReadAsync(new byte[4]));
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task AnObserverThatThrows_DoesNotBreakTheStream()
    {
        // Telemetry must never break the call it measures - least of all mid-answer.
        var payload = Encoding.UTF8.GetBytes("abcdef");
        await using var s = new ObservingStream(
            new MemoryStream(payload), _ => throw new InvalidOperationException("observer blew up"),
            () => throw new InvalidOperationException("first-byte blew up"),
            () => throw new InvalidOperationException("completion blew up"));

        using var output = new MemoryStream();
        var ex = await Record.ExceptionAsync(() => s.CopyToAsync(output));

        Assert.Null(ex);
        Assert.Equal(payload, output.ToArray());
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ObservingStreamTests"
```

Expected: `The name 'ObservingStream' does not exist`.

- [ ] **Step 3: Implement**

`src/Diariz.Api/Services/ObservingStream.cs`:

```csharp
namespace Diariz.Api.Services;

/// <summary>A read-only pass-through <see cref="Stream"/> that reports what flows through it without
/// buffering any of it.
///
/// WHY THIS EXISTS: a streamed LLM response finishes long after <c>SendAsync</c> returns, because the client
/// reads with <c>ResponseHeadersRead</c>. Anything measured at <c>SendAsync</c> - duration especially - is
/// therefore time-to-headers, not the call. Wrapping the body lets the telemetry record be completed when the
/// stream actually ends.
///
/// It must NEVER buffer: buffering an SSE body would hold every chat token until the model finished, leaving
/// the UI silent. Bytes are forwarded to the caller as they arrive and merely shown to the observer.
///
/// Every observer callback is guarded. Telemetry must not be able to break the answer it is measuring, and a
/// throw from here would land in the middle of someone's streamed reply.</summary>
public sealed class ObservingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<ReadOnlyMemory<byte>> _onBytes;
    private readonly Action _onFirstByte;
    private readonly Action _onCompleted;

    private int _completed;
    private bool _sawFirstByte;

    public ObservingStream(
        Stream inner, Action<ReadOnlyMemory<byte>> onBytes, Action onFirstByte, Action onCompleted)
    {
        _inner = inner;
        _onBytes = onBytes;
        _onFirstByte = onFirstByte;
        _onCompleted = onCompleted;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, ct);
        }
        catch
        {
            Complete();
            throw; // the caller's failure is theirs; we only note that the stream ended
        }

        Observe(buffer, read);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read;
        try
        {
            read = _inner.Read(buffer, offset, count);
        }
        catch
        {
            Complete();
            throw;
        }

        Observe(buffer.AsMemory(offset, count), read);
        return read;
    }

    private void Observe(Memory<byte> buffer, int read)
    {
        if (read <= 0)
        {
            Complete();
            return;
        }

        Guard(() =>
        {
            if (!_sawFirstByte)
            {
                _sawFirstByte = true;
                _onFirstByte();
            }
            _onBytes(buffer[..read]);
        });
    }

    /// <summary>Fires the completion callback at most once, whichever of end-of-stream, dispose or a fault
    /// happens first. A reader that stops at <c>[DONE]</c> never reaches end-of-stream, so dispose has to
    /// count - otherwise an abandoned turn would never be recorded at all.</summary>
    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Guard(_onCompleted);
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Deliberately swallowed. See the class comment: this runs mid-answer.
        }
    }

    protected override void Dispose(bool disposing)
    {
        Complete();
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Complete();
        await _inner.DisposeAsync();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ObservingStreamTests"
```

Expected: 8 passed.

- [ ] **Step 5: Mutation check**

Delete the `Complete()` call from `DisposeAsync`. Re-run. `CompletesOnDispose_WhenTheReaderAbandonsTheStreamEarly` must FAIL. Paste the output. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/ObservingStream.cs tests/Diariz.Api.Tests/ObservingStreamTests.cs
git commit -m "feat(llm-usage): add a non-buffering observing stream"
```

---

### Task 5: The handler completes streamed records at stream end

**Files:**
- Modify: `src/Diariz.Api/Services/LlmTelemetry.cs`
- Test: `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`

**Interfaces:**
- Consumes: `SseUsageScanner` (Task 3), `ObservingStream` (Task 4).
- Produces: nothing new externally; `LlmCall.TimeToFirstTokenMs` starts being populated, and streamed rows gain token counts and a true duration.

This is the task that fixes the time-to-headers bug.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Diariz.Api.Tests/LlmTelemetryTests.cs`, reusing the file's existing stub handler and `FakeLlmUsageSink`:

```csharp
public class LlmTelemetryStreamingTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
            },
        };

    private const string StreamBody =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":69,\"completion_tokens\":5,\"total_tokens\":74}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task DoesNotRecordAtSendAsync_ButAfterTheStreamIsRead()
    {
        // The whole point: a streamed record completed at SendAsync would carry time-to-headers and no
        // tokens, which is exactly the bug PR 1 shipped with.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Sse(StreamBody));

        var resp = await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        Assert.Empty(sink.Calls); // nothing yet

        await resp.Content.ReadAsStringAsync();
        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task RecordsTheTokenCounts_FromTheFinalUsageChunk()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Sse(StreamBody));

        var resp = await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        await resp.Content.ReadAsStringAsync();

        var call = Assert.Single(sink.Calls);
        Assert.True(call.Streamed);
        Assert.Equal(69, call.PromptTokens);
        Assert.Equal(5, call.CompletionTokens);
        Assert.Equal(74, call.TotalTokens);
        Assert.NotNull(call.TimeToFirstTokenMs);
    }

    [Fact]
    public async Task RecordsTheCall_EvenWhenTheReaderAbandonsTheStream()
    {
        // A browser disconnecting mid-answer must still produce a row - that is a real cost the
        // administrator needs to see, and it is the case most likely to be expensive.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Sse(StreamBody));

        var resp = await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        var stream = await resp.Content.ReadAsStreamAsync();
        await stream.ReadAsync(new byte[4]);
        await stream.DisposeAsync();

        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task RecordsExactlyOneRow_PerStreamedCall()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Sse(StreamBody));

        var resp = await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        await resp.Content.ReadAsStringAsync();
        resp.Dispose(); // read to the end AND disposed

        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task StoresNoContent_FromTheStream()
    {
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.ChatMessage);
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"the secret merger closes friday\"}}]}\n\n"
                   + "data: {\"choices\":[],\"usage\":{\"total_tokens\":9}}\n\ndata: [DONE]\n\n";
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink), Sse(body));

        var resp = await http.PostAsync("/v1/chat/completions", new StringContent("{}"));
        await resp.Content.ReadAsStringAsync();

        var serialized = System.Text.Json.JsonSerializer.Serialize(Assert.Single(sink.Calls));
        Assert.DoesNotContain("secret merger", serialized);
    }

    [Fact]
    public async Task ANonStreamingResponse_StillRecordsImmediately()
    {
        // Guard against the deferral leaking onto the buffered path, which would silently stop recording
        // summaries, tags, actions and embeddings.
        var sink = new FakeLlmUsageSink();
        using var _ = LlmCallScope.Push(LlmCallKind.Summarize);
        var http = Client(new LlmTelemetryHandler(new FakeLlmTrace(), sink),
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"usage":{"total_tokens":5}}""", Encoding.UTF8, "application/json"),
            });

        await http.PostAsync("/v1/chat/completions", new StringContent("{}"));

        Assert.Single(sink.Calls);
        Assert.False(Assert.Single(sink.Calls).Streamed);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmTelemetryStreamingTests"
```

Expected: `DoesNotRecordAtSendAsync_ButAfterTheStreamIsRead` fails because a row already exists, and the token assertions fail on nulls.

- [ ] **Step 3: Implement**

In `SendAsync`, after the existing `streamed` determination: when the response is `text/event-stream`, do NOT call `Record(...)` on the normal path. Instead:

1. Create an `SseUsageScanner`.
2. Take the response's existing content stream (`await response.Content.ReadAsStreamAsync(ct)`).
3. Wrap it in an `ObservingStream` whose `onBytes` feeds the scanner, whose `onFirstByte` captures `clock.Elapsed` into a `ttft` local, and whose `onCompleted` stops the clock and calls the SAME `Record(...)` helper with the scanner's `Usage`, the TTFT, and `streamed: true`.
4. Replace `response.Content` with a `StreamContent` over the wrapper, **copying the original content headers across** (content type especially - `ChatStreamClient` and its callers rely on them).
5. Return the response.

Keep the existing `using var span` Sentry behaviour and the buffered path exactly as they are.

**Do not read the stream yourself anywhere in this path.** The wrapper only observes what the caller reads. If your implementation calls `ReadAsStringAsync`, `LoadIntoBufferAsync`, or `CopyTo` on a streaming response, it is wrong - that is the failure mode `DoesNotBufferAStreamingResponse` exists to catch.

`Record(...)` already takes everything it needs; extend its parameters for `timeToFirstTokenMs` and pass the streamed flag through rather than writing a second construction site. **There must remain exactly one place where an `LlmCall` is constructed** - PR 1's final review verified that property, and it is what makes the privacy invariant checkable.

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmTelemetry"
```

Expected: all pass, including the pre-existing `DoesNotBufferAStreamingResponse`.

- [ ] **Step 5: Mutation checks (two, both mandatory)**

1. Make the streamed path also call `Record(...)` immediately at `SendAsync` (as well as at stream end). Re-run: `RecordsExactlyOneRow_PerStreamedCall` must FAIL.
2. In the wrapper setup, call `await response.Content.ReadAsStringAsync(ct)` before wrapping. Re-run: `DoesNotBufferAStreamingResponse` must FAIL.

Paste both outputs. Restore after each. The second is the important one - it proves the non-buffering guard is live.

- [ ] **Step 6: Build the solution**

```bash
dotnet build Diariz.slnx
```

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/LlmTelemetry.cs tests/Diariz.Api.Tests/LlmTelemetryTests.cs
git commit -m "feat(llm-usage): measure streamed calls to stream end, with tokens and TTFT"
```

---

### Task 6: Live verification

**Files:** none changed unless a defect is found.

The server side is already proven (see Verified facts). This task proves the **client** side end to end.

- [ ] **Step 1: Full suite**

```bash
dotnet build Diariz.slnx && dotnet test
```

Expected: green, no warnings.

- [ ] **Step 2: Rebuild only the api service**

```bash
cd deploy && docker compose up -d --build api
```

The stack is the user's live local environment. Rebuild **only** `api`. Do not touch postgres, redis, minio, worker, web, or any `glitchtip*` / `hawser` / `portainer*` container. Do not run `docker compose down` or remove volumes. Do not delete any recording or row you did not create.

- [ ] **Step 3: Send a chat message, then read the row**

```bash
docker exec diariz-postgres-1 psql -U diariz -d diariz -c 'SELECT "Kind","Model","DurationMs","TimeToFirstTokenMs","PromptTokens","CompletionTokens","ReasoningTokens","TotalTokens","Sequence","Streamed" FROM "LlmCalls" WHERE "Streamed" ORDER BY "StartedAt" DESC LIMIT 10;'
```

Confirm, and report each explicitly:
- token counts are **non-null** (this is the whole point of the PR),
- `ReasoningTokens` is populated if the model reports it (`gpt-oss-20b` did in the probe),
- `DurationMs` is now a realistic answer time, **not** the ~20 ms time-to-headers PR 1 recorded,
- `TimeToFirstTokenMs` is populated and is less than `DurationMs`,
- a multi-round-trip turn (force a tool call) still shares ONE `OperationId` with `Sequence` 1, 2, ...

- [ ] **Step 4: Confirm the toggle works**

Turn `LlmStreamUsageEnabled` off via psql (note the current values first), send another chat message, and confirm the new row has NULL tokens but still a real duration and TTFT. Restore the setting. Report the SQL both ways.

- [ ] **Step 5: Confirm chat still feels streamed**

Watch the chat UI while a reply generates. Tokens must appear progressively. If the answer arrives all at once, the wrapper is buffering - stop and report, that is a release blocker.

---

### Task 7: Docs, version bump and release notes

**Files:**
- Modify: `version.json` + the four mirrors, `apps/web/src/lib/releases.ts`, `docs/Overall_Synopsis_of_Platform.md`, `apps/web/src/locales/*/account.json`

- [ ] **Step 1: Bump to 0.217.0 in all five places**

`version.json` is canonical; the mirrors are `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`. `versionMirrors.test.ts` fails the build if any drifts.

- [ ] **Step 2: Remove the "future release" clause from the toggle's hint**

PR 1 shipped `LlmStreamUsageEnabled` with a hint saying it takes effect in a future release. It takes effect now. Update the hint in **every** locale file under `apps/web/src/locales/*/account.json`, each in its own language. Plain hyphens only.

- [ ] **Step 3: Update `docs/Overall_Synopsis_of_Platform.md`**

Amend the capture-contract section: streamed responses are wrapped in a non-buffering observing stream, their record is completed at stream end rather than at `SendAsync`, and they carry a time-to-first-token. Note that `stream_options.include_usage` is sent when the platform setting is on, and that the setting exists so an endpoint rejecting the field is recoverable without a redeploy.

- [ ] **Step 4: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`: `version: "0.217.0"`, today's date, the PR number, a headline, a prose `summary`, and `added`/`fixed` lists. Say plainly that chat and formula runs now report token counts and a real duration, and that the previously recorded duration for a streamed call measured only the time to the first response header. **Plain hyphens only.**

The `pr` field needs a number that does not exist until the PR is opened, and guessing "last + 1" fails because Dependabot PRs and issues share the sequence. Push the branch, run `gh pr create`, read the real number, amend, push again.

`docs/Data_Schema.md` needs **no** change - no schema change in this PR (`TimeToFirstTokenMs` already exists, unpopulated). README Features / `docs/features.md` / `CAPABILITIES` need no change either: no new capability, and the admin control they already describe simply starts working. State both of these explicitly in the PR body so a reviewer knows they were considered rather than forgotten.

- [ ] **Step 5: Verify**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts && npm run build
```

- [ ] **Step 6: Commit and open the PR**

The PR body must state: **server redeploy only**; no schema change; that `stream_options` support was verified live against LM Studio before implementation; and the observed before/after durations for a streamed call from Task 6.

---

## Self-Review

**Spec coverage:** the spec's "Streaming" section calls for `stream_options` behind the platform toggle (Tasks 1-2), a pass-through wrapper observing first byte and scanning for usage, completing on end/dispose/fault (Tasks 3-5), and the time-to-headers correction (Task 5). All covered.

**Deliberate non-goals, restated:** distinguishing caller cancellation from a per-call timeout is NOT in this PR. It needs each client to stamp which token fired, and this PR touches only `ChatStreamClient`, so it would be scope creep with no natural home here. It stays on the deferred list.

**Type consistency:** `SummarizationRequestConfig.IncludeStreamUsage`, `SseUsageScanner.Feed`/`.Usage`/`.MaxLineBytes`, and `ObservingStream(inner, onBytes, onFirstByte, onCompleted)` are each defined once and used consistently. `LlmUsage` and `LlmUsageParser.TryParse` are reused unchanged from PR 1.

**Known adaptation point:** Task 2's tests need a way to capture the outgoing request body; the plan says to reuse whatever `ChatStreamClientTests.cs` already stubs rather than inventing a shared helper.
