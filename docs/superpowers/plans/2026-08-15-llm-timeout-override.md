# Per-User LLM Timeout Override Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the configured LLM timeout the real authority on every LLM path (it is currently defeated by three lower ceilings), give chat streaming a timeout suited to streaming, and let each user override the timeout beside the model it belongs to.

**Architecture:** Three independent ceiling fixes come first, because without them a user's override silently does nothing for chat and formula runs. Then the override itself follows `UserSettings.ChatContextWindow` field for field - a nullable int on `UserSettings`, a `Default*` companion in the GET DTO, `> 0 ? value : null` in the PUT, and a coalesce in **both** settings resolvers. The streaming timeout is deliberately an *inactivity* timeout rather than a total-duration cap.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit (+ Testcontainers for integration), React 19 + TypeScript + Vite, react-i18next (en/de/es/fr), Vitest + @testing-library/react, nginx.

**Spec:** `docs/superpowers/specs/2026-08-15-llm-timeout-override-design.md`

## Global Constraints

- **Branch:** `feat/llm-timeout-override` (already created; the spec is already committed there). `main` is branch-protected - never commit or push to it. Finish by pushing and opening a PR.
- **TDD is mandatory.** Write the failing test, run it, confirm it fails with the expected message, then write the minimal code. This repo's most common historical defect is a test that cannot fail - a test that passes before the implementation exists is a defect unless it is explicitly a regression guard.
- **No `@testing-library/jest-dom`.** Not a dependency; must not become one. Plain assertions only.
- **No mocking library in .NET.** Add a fake to `tests/Diariz.Api.TestSupport` instead.
- **Never `git add -A` or `git add .`** - this repo has hundreds of untracked scratch files. Stage explicit paths.
- **No em/en dashes (`—` / `–`) in user-facing text.** Plain hyphen. Applies to UI strings, all four locale catalogs, release notes, README and any user-visible API error message.
- **Help articles are ASCII only** (`apps/web/src/content/help/**`), enforced by `helpContent.test.ts`.
- The UI says **Folder**, not Section; code/DB/API keep `Section`. Not directly relevant here, but do not regress it.
- **Version:** 0.214.0 -> **0.215.0** (functional enhancement: Minor +1, Build reset).
- **Deployment surface:** server redeploy (API + web/nginx). No desktop release.
- Web tests: from `apps/web`, `npm test -- <path>` (the script is `vitest run`).
- .NET: `dotnet build Diariz.slnx` builds everything; `dotnet test tests/Diariz.Api.Tests` is the fast unit run; `tests/Diariz.Api.IntegrationTests` needs Docker.
- `dotnet test --filter "Name=X"` does **not** work in this repo. Use `--filter "FullyQualifiedName~X"`.

---

### Task 1: Remove the two configuration ceilings

**Files:**
- Modify: `src/Diariz.Api/Program.cs:372`
- Modify: `apps/web/nginx.conf:60-67`
- Test: `tests/Diariz.Api.IntegrationTests/LlmHttpClientTimeoutTests.cs` (create)

**Interfaces:**
- Consumes: `Program.cs:288-291`'s existing `NoHttpTimeout` helper and `AddLlmClient<TClient,TImpl>` at `:299-306`.
- Produces: nothing later tasks import. After this task, `IChatStreamClient`'s HttpClient has no timeout, so `config.TimeoutSeconds` becomes the only cap on chat and formula runs.

- [ ] **Step 1: Write the failing integration test**

Create `tests/Diariz.Api.IntegrationTests/LlmHttpClientTimeoutTests.cs`. `AddHttpClient<TClient,TImpl>` names the client after the **service type**, so `IHttpClientFactory.CreateClient(nameof(IChatStreamClient))` returns one configured exactly as DI configured it. `ChatStreamClient` keeps its `HttpClient` private, so this factory route is the only reachable assertion.

Follow the existing integration conventions: the `"integration"` collection, and `NewFactory()` per class as in `AccessTokenAllowlistIntegrationTests.cs:37`.

```csharp
using Diariz.Api.Services;
using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// Every LLM client must disable HttpClient's own 100s cap so the configured per-request timeout
/// (user -> platform -> server option) is the single authority. IChatStreamClient was once registered
/// without it, which silently capped chat streaming AND formula runs at 100s no matter what was configured.
[Collection("integration")]
public class LlmHttpClientTimeoutTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    [Theory]
    [InlineData(nameof(IChatStreamClient))]
    [InlineData(nameof(ISummarizationClient))]
    [InlineData(nameof(IEmbeddingClient))]
    public void LlmClients_HaveNoHttpClientTimeout(string clientName)
    {
        using var factory = NewFactory();
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, http.Timeout);
    }
}
```

The two already-correct clients are in the theory on purpose: they pin the *rule* rather than one registration, so a future client added without `NoHttpTimeout` is likelier to be caught.

- [ ] **Step 2: Run it and watch the chat case fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmHttpClientTimeoutTests"
```
Expected: the `ISummarizationClient` and `IEmbeddingClient` cases PASS; the **`IChatStreamClient` case FAILS** with `Assert.Equal() Failure` comparing `-00:00:00.0010000` (InfiniteTimeSpan) against `00:01:40` (100 seconds). That 100s in the failure message is the bug, stated numerically.

If the whole class errors instead (e.g. `ContainersFixture` cannot start), Docker is not running - start it; do not rewrite the test to avoid the harness.

- [ ] **Step 3: Fix the registration**

`src/Diariz.Api/Program.cs:372`:

```csharp
// ---- Chat (streaming, reuses the per-user summarisation LLM config) ----
AddLlmClient<IChatStreamClient, ChatStreamClient>(NoHttpTimeout);
```

- [ ] **Step 4: Re-run and confirm all three pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmHttpClientTimeoutTests"
```
Expected: 3/3 PASS.

- [ ] **Step 5: Raise nginx's read timeout on the API location**

`apps/web/nginx.conf`, replace the `location /api/` block (lines 60-67) with:

```nginx
    # REST API - forwarded unchanged to the API container.
    #
    # proxy_read_timeout is explicit because nginx's 60s default sat BELOW the app's own LLM timeout
    # (PlatformSettings.LlmTimeoutSeconds, default 120s, and now overridable per user), so a slow local
    # model was cut off by the proxy before the configured timeout could apply. An hour matches /hubs/
    # and /mcp. Buffering needs no setting here: the chat stream sends X-Accel-Buffering: no per response
    # (ChatController), which nginx honours.
    #
    # An OUTER reverse proxy in front of this container has its own read timeout, which nothing here can
    # lift - raise it there too, or long generations still die at that proxy's default.
    location /api/ {
        proxy_pass http://api:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $client_proto;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
    }
```

There is no test for nginx config; the comment is the documentation. Do not add `proxy_buffering off` - it would disable buffering for every JSON response, and the SSE endpoint already opts out per-response via its header.

- [ ] **Step 6: Verify the config parses**

```bash
docker run --rm -v "/d/Repositories/Diariz/apps/web/nginx.conf:/etc/nginx/conf.d/default.conf:ro" nginx:alpine nginx -t
```
Expected: `syntax is ok` / `test is successful`. If the image cannot pull, note it in your report and move on - the change is three lines of standard directives.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Program.cs apps/web/nginx.conf tests/Diariz.Api.IntegrationTests/LlmHttpClientTimeoutTests.cs
git commit -m "fix(llm): stop capping chat and formula runs at 100s, and nginx at 60s"
```

---

### Task 2: An inactivity timeout for chat streaming

**Files:**
- Modify: `src/Diariz.Api/Services/ChatStreamClient.cs:64-73` and `:100-110`
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs` (add a stalling SSE handler)
- Test: `tests/Diariz.Api.Tests/ChatStreamClientTests.cs`

**Interfaces:**
- Consumes: `SummarizationRequestConfig.TimeoutSeconds`; `ChatStreamException` (declared `ChatStreamClient.cs:10-13`); Task 1's registration fix, without which the HttpClient still aborts at 100s regardless of this token.
- Produces: a new TestSupport fake `SlowSseHttpMessageHandler(IEnumerable<(TimeSpan Delay, string Line)> script)` - Task 3+ do not use it, but keep the name stable.

- [ ] **Step 1: Add the stalling SSE fake to TestSupport**

The existing `FakeHttpMessageHandler` (`tests/Diariz.Api.TestSupport/Fakes.cs:14`) returns a fully-buffered `StringContent`, so it can never exercise a stall. Add alongside it:

```csharp
/// An SSE response whose lines arrive on a script of delays, so a test can exercise the read loop's
/// *inactivity* timeout: the gap between lines is what matters, not the total duration. A line whose
/// delay never elapses (TimeSpan.MaxValue) models an upstream that has gone silent without disconnecting.
public sealed class SlowSseHttpMessageHandler(IEnumerable<(TimeSpan Delay, string Line)> script)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ScriptedStream(script.GetEnumerator()))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
            },
        });

    /// Yields each scripted line after its delay. Honours the read's CancellationToken, which is what the
    /// client's per-line idle token flows into.
    private sealed class ScriptedStream(IEnumerator<(TimeSpan Delay, string Line)> script) : Stream
    {
        private byte[] _pending = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset >= _pending.Length)
            {
                if (!script.MoveNext()) return 0; // end of stream
                var (delay, line) = script.Current;
                await Task.Delay(delay, ct);
                _pending = Encoding.UTF8.GetBytes(line + "\n");
                _offset = 0;
            }
            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

Add whatever `using`s the file lacks (`System.Net`, `System.Net.Http.Headers`, `System.Text`).

**One hazard to check rather than discover:** only the `Memory<byte>` overload of `ReadAsync` is overridden above. `StreamReader.ReadLineAsync` uses that overload in modern .NET, so this should work - but if a test hangs instead of throwing, it is because something routed through the `byte[]` overload, whose base implementation falls back to the synchronous `Read`, which blocks a thread on `Task.Delay(...).GetAwaiter().GetResult()`. If that happens, override `ReadAsync(byte[], int, int, CancellationToken)` as well, delegating to the `Memory` overload. Do not "fix" a hang by shortening the delays.

- [ ] **Step 2: Write the two failing tests**

Add to `tests/Diariz.Api.Tests/ChatStreamClientTests.cs`. Read the file's existing helpers first and reuse how it constructs a `ChatStreamClient` and a `SummarizationRequestConfig`; the sketch below assumes a local `Config(int timeoutSeconds)` helper - write one if the file has no equivalent.

```csharp
    // ---- Inactivity timeout ----
    // The allowance is the gap BETWEEN chunks, not the total call. A slow local model streaming a long
    // answer legitimately runs for minutes; a total-duration cap would abort exactly the case the
    // configured timeout exists to support.

    [Fact]
    public async Task StreamChunks_Throws_WhenTheUpstreamGoesSilent()
    {
        var script = new[]
        {
            (TimeSpan.Zero, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}"),
            (TimeSpan.FromSeconds(30), "data: [DONE]"), // never arrives within the 1s allowance
        };
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });

        var ex = await Assert.ThrowsAsync<ChatStreamException>(async () =>
        {
            await foreach (var _ in client.StreamChunksAsync(Config(1), [], null)) { }
        });

        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public async Task StreamChunks_Completes_WhenChunksAreSlowButKeepArriving()
    {
        // Six 200ms gaps = 1.2s total, longer than the 1s timeout, but no single gap reaches it.
        // This is the test that fails if the idle timer is ever "simplified" into a total-duration cap.
        var script = Enumerable.Range(0, 5)
            .Select(i => (TimeSpan.FromMilliseconds(200),
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{i}\"}}}}]}}"))
            .Append((TimeSpan.FromMilliseconds(200), "data: [DONE]"))
            .ToArray();
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });

        var seen = 0;
        await foreach (var _ in client.StreamChunksAsync(Config(1), [], null)) seen++;

        Assert.Equal(5, seen);
    }

    [Fact]
    public async Task StreamChunks_StaysQuiet_WhenTheCallerCancels()
    {
        // A user pressing Stop must not be reported as a timeout: ChatController catches
        // OperationCanceledException silently, and turning it into a ChatStreamException would show
        // the user a spurious error for their own action.
        var script = new[] { (TimeSpan.FromSeconds(30), "data: [DONE]") };
        var client = new ChatStreamClient(new HttpClient(new SlowSseHttpMessageHandler(script))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamChunksAsync(Config(60), [], null, cts.Token)) { }
        });
    }
```

- [ ] **Step 3: Run them and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatStreamClientTests"
```
Expected: `StreamChunks_Throws_WhenTheUpstreamGoesSilent` FAILS - no `ChatStreamException` is thrown because there is no timeout at all; with `HttpClient.Timeout` set to infinite in the test, the call simply waits out the 30s script. The other two should pass already (they are the guards that the fix does not over-reach).

If the silent-upstream test instead hangs for 30s, that *is* the failure - the absence of any timeout. Note it and proceed.

- [ ] **Step 4: Implement the inactivity timeout**

In `src/Diariz.Api/Services/ChatStreamClient.cs`, replace the read loop in **`StreamChunksAsync`** (lines 102-109):

```csharp
            while (true)
            {
                // The timeout is an INACTIVITY allowance, reset per line, not a cap on the whole call: a
                // slow local model streaming a long answer legitimately runs for minutes. Keep-alive
                // comments and blank separators count as activity - they are proof the upstream is alive.
                string? line;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Must be a ChatStreamException, not the bare cancellation: ChatController catches
                    // OperationCanceledException unconditionally (the Stop button), so a bare one would end
                    // the stream with no `done` and no `error` frame - a silent truncation the browser
                    // renders as a successful short answer. ChatStreamException is the only exception it
                    // turns into a visible error frame.
                    throw new ChatStreamException(
                        $"The model sent nothing for {config.TimeoutSeconds}s, so the response was stopped.");
                }
                if (line is null) break;
                var delta = ParseStreamChunk(line, out var done);
                if (done) yield break;
                if (delta is not null) yield return delta;
            }
```

Note the read and the `yield return` are separated deliberately: C# forbids `yield return` inside a `try` block that has a `catch` clause.

Apply the identical change to **`StreamAsync`**'s loop (lines 66-73), keeping its `ParseStreamLine` / `yield return token!` body:

```csharp
            while (true)
            {
                string? line;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ChatStreamException(
                        $"The model sent nothing for {config.TimeoutSeconds}s, so the response was stopped.");
                }
                if (line is null) break;
                var token = ParseStreamLine(line, out var done);
                if (done) yield break;
                if (!string.IsNullOrEmpty(token)) yield return token!;
            }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatStreamClientTests"
```
Expected: all PASS, and the class completes in a few seconds (the silent-upstream case now aborts after ~1s rather than waiting out 30s).

- [ ] **Step 6: Run the full unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```
Expected: PASS. Chat and formula tests exercise these loops; a `FakeChatStreamClient` (`tests/Diariz.Api.TestSupport/Fakes.cs:349`) is used in most of them so they should be unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/ChatStreamClient.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/ChatStreamClientTests.cs
git commit -m "fix(chat): time out a silent model instead of streaming forever"
```

---

### Task 3: The per-user setting, and both resolvers

**Files:**
- Modify: `src/Diariz.Domain/Entities/UserSettings.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddUserLlmTimeout.cs` (via the EF tool)
- Modify: `src/Diariz.Api/Services/SummarizationSettingsResolver.cs:59-68`
- Modify: `src/Diariz.Api/Services/EmbeddingSettingsResolver.cs:68-78`
- Test: `tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs:102-119`
- Test: `tests/Diariz.Api.Tests/EmbeddingSettingsResolverTests.cs:91-105`

**Interfaces:**
- Consumes: `PlatformSettings.LlmTimeoutSeconds` and `PlatformSettings.SingletonId`.
- Produces: `UserSettings.LlmTimeoutSeconds` as `int?` (null = inherit). Tasks 4 and 5 read and write this property; the resolved precedence is **`user ?? platform ?? server option`**.

- [ ] **Step 1: Write the failing resolver tests**

In `tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs`, replace the two tests at lines 102-119 with three tiers. `Server.TimeoutSeconds` is 90 (line 17).

```csharp
    // ---- Timeout: user override ?? platform-wide admin setting ?? server option ----

    [Fact]
    public async Task Timeout_UsesUserOverride_WhenSet()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 300 });
        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTimeoutSeconds = 600 });
        await db.SaveChangesAsync();

        // The user's own number wins over the admin's: they point at their own endpoint and model.
        Assert.Equal(600, (await Build(db).ResolveAsync(userId)).TimeoutSeconds);
    }

    [Fact]
    public async Task Timeout_UsesPlatformSetting_WhenUserHasNoOverride()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 300 });
        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTimeoutSeconds = null });
        await db.SaveChangesAsync();

        Assert.Equal(300, (await Build(db).ResolveAsync(userId)).TimeoutSeconds);
    }

    [Fact]
    public async Task Timeout_FallsBackToServerOption_WhenNoPlatformRow()
    {
        using var db = TestDb.Create();
        Assert.Equal(90, (await Build(db).ResolveAsync(Guid.NewGuid())).TimeoutSeconds); // Server.TimeoutSeconds
    }
```

In `tests/Diariz.Api.Tests/EmbeddingSettingsResolverTests.cs`, extend the combined test at lines 91-105 with a third tier - embeddings must not silently ignore the user's value:

```csharp
        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTimeoutSeconds = 600 });
        await db.SaveChangesAsync();

        // The user's override beats the admin's, here too: one chain, or embeddings quietly disagree
        // with every other LLM call.
        Assert.Equal(600, (await Build(db, emb, new SummarizationOptions()).ResolveAsync(userId)).TimeoutSeconds);
```
Adjust that test to use a stable `userId` for all three assertions rather than `Guid.NewGuid()` per call.

- [ ] **Step 2: Run them and watch them fail to compile**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SettingsResolverTests"
```
Expected: **compile error** `'UserSettings' does not contain a definition for 'LlmTimeoutSeconds'`. A compile failure is a legitimate red here - the property genuinely does not exist yet.

- [ ] **Step 3: Add the entity property**

In `src/Diariz.Domain/Entities/UserSettings.cs`, next to `ChatContextWindow` (line 21):

```csharp
    /// <summary>Per-request LLM timeout in seconds, overriding the platform-wide admin setting. Null
    /// inherits (PlatformSettings.LlmTimeoutSeconds, then the server option). A user points at their own
    /// endpoint and model, so a slow local model is theirs to accommodate - the value is uncapped.</summary>
    public int? LlmTimeoutSeconds { get; set; }
```

No `DiarizDbContext` configuration is needed - `ChatContextWindow` is a plain nullable int with none.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddUserLlmTimeout --project src/Diariz.Domain --startup-project src/Diariz.Api
```
Open the generated file and confirm it is exactly one additive nullable column on `UserSettings` (`AddColumn<int>(..., nullable: true)`) with a matching `DropColumn` in `Down`. Because it is additive and nullable it is forward-restore-safe, so **do not** bump `MaintenanceController.CurrentFormat`.

If the tool emits anything else (a rename, a default, an unrelated column), stop and report - the model has drifted from the snapshot and that is not this task's problem to absorb.

- [ ] **Step 5: Add the user tier to both resolvers**

`src/Diariz.Api/Services/SummarizationSettingsResolver.cs`, replacing lines 59-68's comment and the `TimeoutSeconds` argument:

```csharp
        // The request timeout is the user's override ?? the platform-wide admin setting ?? the server
        // option. The HTTP clients themselves have no cap, so this is the single authority.
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);

        return new SummarizationRequestConfig(
            ApiBase: Coalesce(s?.SummaryApiBase, _opts.ApiBase),
            ApiKey: Coalesce(_protector.Unprotect(s?.SummaryApiKeyEncrypted), _opts.ApiKey),
            Model: Coalesce(s?.SummaryModel, _opts.Model),
            TimeoutSeconds: s?.LlmTimeoutSeconds ?? ps?.LlmTimeoutSeconds ?? _opts.TimeoutSeconds)
```

`src/Diariz.Api/Services/EmbeddingSettingsResolver.cs:78` - the same three-level chain. Read its surrounding lines to find how it names the user-settings variable and use that name; update its comment to match the new precedence.

- [ ] **Step 6: Run the resolver tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SettingsResolverTests"
```
Expected: PASS.

- [ ] **Step 7: Build everything and run the unit suite**

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
```
Expected: build clean, tests PASS. Build the whole solution - the integration project has second construction sites for several services and a unit-only run misses compile breaks there.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain/Entities/UserSettings.cs src/Diariz.Domain/Migrations src/Diariz.Api/Services/SummarizationSettingsResolver.cs src/Diariz.Api/Services/EmbeddingSettingsResolver.cs tests/Diariz.Api.Tests/SummarizationSettingsResolverTests.cs tests/Diariz.Api.Tests/EmbeddingSettingsResolverTests.cs
git commit -m "feat(settings): let a user override the LLM timeout for their own model"
```

---

### Task 4: The API surface

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`UserSettingsDto` ~line 596, `UpdateUserSettingsRequest` ~line 631)
- Modify: `src/Diariz.Api/Controllers/UserSettingsController.cs` (GET ~line 53, PUT ~line 106)
- Test: `tests/Diariz.Api.Tests/UserSettingsControllerTests.cs`
- Test: `tests/Diariz.Api.IntegrationTests/UserSettingsIntegrationTests.cs`
- Regenerate: `integrations/n8n-nodes-diariz/generated/openapi.snapshot.json`

**Interfaces:**
- Consumes: `UserSettings.LlmTimeoutSeconds` from Task 3.
- Produces: `UserSettingsDto.LlmTimeoutSeconds` (`int?`) and `UserSettingsDto.DefaultLlmTimeoutSeconds` (`int`); `UpdateUserSettingsRequest.LlmTimeoutSeconds` (`int?`). Task 5's TypeScript types mirror these names exactly, camel-cased: `llmTimeoutSeconds`, `defaultLlmTimeoutSeconds`.

- [ ] **Step 1: Write the failing controller tests**

Add to `tests/Diariz.Api.Tests/UserSettingsControllerTests.cs`, following its `Build()` helper (lines 15-27) and the `ContextWindow` tests at 126-146 for shape.

```csharp
    // ---- LLM timeout override ----

    [Fact]
    public async Task Get_ReturnsTimeoutOverrideAndTheInheritedDefault()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTimeoutSeconds = 600 });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 300 });
        await db.SaveChangesAsync();

        var dto = await Build(db, userId).Get();

        Assert.Equal(600, dto.LlmTimeoutSeconds);
        // The companion is what applies when the user clears their own - the dialog shows it as a placeholder.
        Assert.Equal(300, dto.DefaultLlmTimeoutSeconds);
    }

    [Fact]
    public async Task Put_SetsClearsAndLeavesTheTimeoutUnchanged()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var c = Build(db, userId);

        await c.Update(new UpdateUserSettingsRequest(null, null, null, LlmTimeoutSeconds: 600));
        Assert.Equal(600, (await db.UserSettings.FindAsync(userId))!.LlmTimeoutSeconds);

        // Absent means "another tab is saving, do not touch my field".
        await c.Update(new UpdateUserSettingsRequest(null, null, null));
        Assert.Equal(600, (await db.UserSettings.FindAsync(userId))!.LlmTimeoutSeconds);

        // 0 clears the override, falling back to the platform/server value.
        await c.Update(new UpdateUserSettingsRequest(null, null, null, LlmTimeoutSeconds: 0));
        Assert.Null((await db.UserSettings.FindAsync(userId))!.LlmTimeoutSeconds);
    }

    [Fact]
    public async Task Put_RejectsATimeoutBelowTheFloor()
    {
        using var db = TestDb.Create();
        var c = Build(db, Guid.NewGuid());

        // Mirrors the admin field's floor (PlatformSettingsController): 1-4s is not a working timeout,
        // and silently coercing it would hide the mistake.
        var result = await c.Update(new UpdateUserSettingsRequest(null, null, null, LlmTimeoutSeconds: 3));

        Assert.IsType<BadRequestObjectResult>(result);
    }
```

Check `Build`'s real signature before writing these - if it does not take a userId, follow whatever the file's existing tests do to authenticate (`Http.Context(userId)`).

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserSettingsControllerTests"
```
Expected: compile error - `UpdateUserSettingsRequest` has no `LlmTimeoutSeconds`, and `UserSettingsDto` has neither new member.

- [ ] **Step 3: Add the DTO fields**

`src/Diariz.Api/Contracts/ApiDtos.cs`, `UserSettingsDto` - add after the calendar members, as trailing optional parameters so existing positional construction is unaffected:

```csharp
    /// <summary>The user's own per-request LLM timeout in seconds, or null when they inherit.</summary>
    int? LlmTimeoutSeconds = null,
    /// <summary>What applies when they have no override: the platform-wide admin setting, else the server
    /// option. Shown as the field's placeholder.</summary>
    int DefaultLlmTimeoutSeconds = PlatformSettings.DefaultLlmTimeoutSeconds);
```
(Move the existing closing `)` accordingly, and add a `using Diariz.Domain.Entities;` if the file lacks one.)

`UpdateUserSettingsRequest` - add a trailing parameter:

```csharp
    /// <summary>Per-request LLM timeout in seconds. Null leaves it unchanged; 0 clears the override;
    /// a value of 5 or more sets it. 1-4 is rejected rather than coerced.</summary>
    int? LlmTimeoutSeconds = null);
```

Extend the record's summary block above `UpdateUserSettingsRequest` with the same one-line rule - those doc comments are surfaced in the OpenAPI document.

- [ ] **Step 4: Wire the controller**

`src/Diariz.Api/Controllers/UserSettingsController.cs`, in `Get()` - the GET must resolve the inherited default, which means reading the platform singleton:

```csharp
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
```
then add to the `UserSettingsDto` construction:

```csharp
            LlmTimeoutSeconds: s?.LlmTimeoutSeconds,
            DefaultLlmTimeoutSeconds: ps?.LlmTimeoutSeconds ?? _serverDefaults.TimeoutSeconds);
```

In `Update()`, beside the `ContextWindow` block:

```csharp
        // Timeout: null leaves it unchanged; 0 clears the override; >=5 sets it. The floor mirrors the
        // admin field - 1-4s is not a working timeout, and coercing it silently would hide the mistake.
        if (req.LlmTimeoutSeconds is not null)
        {
            if (req.LlmTimeoutSeconds is > 0 and < 5)
                return BadRequest("An LLM timeout must be at least 5 seconds.");
            s.LlmTimeoutSeconds = req.LlmTimeoutSeconds > 0 ? req.LlmTimeoutSeconds : null;
        }
```

Place this **before** any `SaveChangesAsync` in the method, and check whether the method's other early returns come before or after mutations - a `BadRequest` after other fields have already been assigned would half-apply the request. If the method mutates then saves at the end, put this validation at the top of the method instead, before the first assignment.

Also extend the `[EndpointDescription]` blocks (lines 43-52 and 89-105) - they are load-bearing OpenAPI docs.

- [ ] **Step 5: Run the controller tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserSettingsControllerTests"
```
Expected: PASS.

- [ ] **Step 6: Add the integration test for the column default**

In `tests/Diariz.Api.IntegrationTests/UserSettingsIntegrationTests.cs`, following the raw-SQL DB-default pattern already at lines 120-130, assert a freshly-inserted `UserSettings` row has `LlmTimeoutSeconds` NULL - the in-memory provider cannot prove a real column default.

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~UserSettingsIntegrationTests"
```
Expected: PASS (the migration applies on fixture startup).

- [ ] **Step 7: Regenerate BOTH OpenAPI snapshots**

There are two, and CI fails on either drifting.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"
```
This test **rewrites its own snapshot**, so run 1 fails and run 2 passes with no code change. Run it twice and commit the regenerated file.

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```
Then confirm `generated/openapi.snapshot.json` changed and includes the new fields.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserSettingsController.cs tests/Diariz.Api.Tests/UserSettingsControllerTests.cs tests/Diariz.Api.IntegrationTests/UserSettingsIntegrationTests.cs integrations/n8n-nodes-diariz/generated/openapi.snapshot.json
```
Add the .NET snapshot file too - find its path from the `OpenApiSnapshotTests` source and include it explicitly.
```bash
git commit -m "feat(api): expose the per-user LLM timeout on user settings"
```

---

### Task 5: The Model dialog field

**Files:**
- Modify: `apps/web/src/lib/types.ts` (`UserSettings` ~498-533, `UpdateUserSettings` ~942-971)
- Modify: `apps/web/src/components/assistant/ModelDialog.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`
- Test: `apps/web/src/components/assistant/ModelDialog.test.tsx`

**Interfaces:**
- Consumes: the API fields from Task 4, camel-cased by the JSON serialiser: `llmTimeoutSeconds` (`number | null`) and `defaultLlmTimeoutSeconds` (`number`) on the GET; `llmTimeoutSeconds` (`number`) on the PUT.
- Produces: nothing later tasks consume.

- [ ] **Step 1: Write the failing component tests**

Add to `apps/web/src/components/assistant/ModelDialog.test.tsx`, extending its settings fixture (lines 14-32) with `llmTimeoutSeconds: null, defaultLlmTimeoutSeconds: 120`.

```tsx
  it("shows the inherited timeout as a placeholder when the user has no override", () => {
    render(<ModelDialog data={{ ...settings, llmTimeoutSeconds: null }} onClose={() => {}} />);

    const field = screen.getByRole("spinbutton", { name: /timeout/i });
    expect((field as HTMLInputElement).value).toBe("");
    expect(field.getAttribute("placeholder")).toBe("120");
  });

  it("sends the timeout on save", async () => {
    render(<ModelDialog data={settings} onClose={() => {}} />);

    fireEvent.change(screen.getByRole("spinbutton", { name: /timeout/i }), { target: { value: "600" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() =>
      expect(vi.mocked(api.updateUserSettings).mock.calls[0][0].llmTimeoutSeconds).toBe(600),
    );
  });

  it("sends 0 to clear the override when the field is emptied", async () => {
    render(<ModelDialog data={{ ...settings, llmTimeoutSeconds: 600 }} onClose={() => {}} />);

    fireEvent.change(screen.getByRole("spinbutton", { name: /timeout/i }), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    // 0 is the API's "clear" signal - an empty box must not send NaN or leave the old value in place.
    await waitFor(() =>
      expect(vi.mocked(api.updateUserSettings).mock.calls[0][0].llmTimeoutSeconds).toBe(0),
    );
  });
```

Match the file's existing mocking style for `../../lib/api` rather than inventing one; read the top of the file first.

- [ ] **Step 2: Run them and watch them fail**

```bash
cd apps/web && npm test -- src/components/assistant/ModelDialog.test.tsx
```
Expected: FAIL - `Unable to find an accessible element with the role "spinbutton"`. There is no numeric input in this dialog today.

- [ ] **Step 3: Add the TypeScript types**

`apps/web/src/lib/types.ts` - in the `UserSettings` interface:
```ts
  llmTimeoutSeconds: number | null;
  defaultLlmTimeoutSeconds: number;
```
and in `UpdateUserSettings`:
```ts
  llmTimeoutSeconds?: number;
```

- [ ] **Step 4: Add the field to the dialog**

`apps/web/src/components/assistant/ModelDialog.tsx`. Hold it as a **string**, not a number, so the box can be emptied while typing - the same reason `RecordingsSection.tsx` does. Add beside the other state (after line 23):

```tsx
  // Held as a string so the box can be emptied while typing; "" means "inherit" and saves as 0.
  const [timeout, setTimeout] = useState(data.llmTimeoutSeconds?.toString() ?? "");
```

In `onSave`'s `api.updateUserSettings({...})` call, add:
```tsx
        llmTimeoutSeconds: timeout.trim() === "" ? 0 : Number(timeout),
```

Render it inside the same bordered block as the reasoning control, after the closing `</div>` of the reasoning group but before that block's closing `</div>`:

```tsx
            <label className="mt-3 block text-sm">
              <span className="font-medium text-gray-700 dark:text-gray-200">{t("timeoutLabel")}</span>
              <input
                type="number"
                min={5}
                step={1}
                value={timeout}
                placeholder={data.defaultLlmTimeoutSeconds.toString()}
                onChange={(e) => setTimeout(e.target.value)}
                className={field}
              />
              <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("timeoutHint")}</p>
            </label>
```

`field` is the shared class string already defined at line 73. Note `setTimeout` shadows the global - if that trips the linter or reads badly, name the state `timeoutValue` / `setTimeoutValue` and adjust the three references.

- [ ] **Step 5: Add the strings to all four locales**

`apps/web/src/locales/{en,de,es,fr}/account.json`, beside the existing assistant keys (en around lines 266-277). No em/en dashes.

```
en: "timeoutLabel": "Response timeout (seconds)",
    "timeoutHint": "How long to wait for the model between chunks of a reply. Leave blank to use the platform default. Raise it for a large local model.",
de: "timeoutLabel": "Antwort-Zeitlimit (Sekunden)",
    "timeoutHint": "Wie lange zwischen zwei Teilen einer Antwort auf das Modell gewartet wird. Leer lassen, um den Plattformwert zu verwenden. Für ein großes lokales Modell erhöhen.",
es: "timeoutLabel": "Tiempo de espera de respuesta (segundos)",
    "timeoutHint": "Cuánto esperar al modelo entre fragmentos de una respuesta. Déjalo en blanco para usar el valor de la plataforma. Auméntalo para un modelo local grande.",
fr: "timeoutLabel": "Délai de réponse (secondes)",
    "timeoutHint": "Combien de temps attendre le modèle entre deux fragments d'une réponse. Laissez vide pour utiliser la valeur de la plateforme. Augmentez-le pour un grand modèle local.",
```

- [ ] **Step 6: Run the component tests to verify they pass**

```bash
cd apps/web && npm test -- src/components/assistant/ModelDialog.test.tsx
```
Expected: PASS.

- [ ] **Step 7: Run the full web suite and the build**

```bash
cd apps/web && npm test && npm run build
```
Expected: PASS and a clean build. Other tests construct a `UserSettings` fixture and will fail to typecheck without the two new required fields - add them to those fixtures; do not make the fields optional to dodge it.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/assistant/ModelDialog.tsx apps/web/src/components/assistant/ModelDialog.test.tsx apps/web/src/locales
git commit -m "feat(web): a response-timeout field beside the model it applies to"
```
Stage any other test fixtures you had to touch in Step 7 by explicit path.

---

### Task 6: Docs and release

**Files:**
- Modify: `version.json` + 4 mirrors
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/en/ai-model-settings.md`
- Modify: `README.md`, `docs/features.md`

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: nothing.

- [ ] **Step 1: Bump the version in all five places**

`version.json` to `{ "version": "0.215.0" }`, then the same in `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json`. All five must match or `versionMirrors.test.ts` fails.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. Set `pr` to the real number once known - Task 7 corrects it if this is a guess. Do not modify historical entries.

```ts
  {
    version: "0.215.0",
    date: "2026-08-15",
    pr: 529,
    headline: "Set your own model timeout, and three ceilings that ignored it",
    summary:
      "If you run a large local model, generations could be cut off partway with no way to give them more room. There is now a Response timeout on the Change model dialog, so you can set your own allowance for the model you actually run, and it beats the platform default. Fixing it turned up three separate ceilings underneath: chat replies and Formula runs were capped at 100 seconds whatever the platform timeout said, chat streaming had no timeout of its own at all, and the bundled web server cut any API request at 60 seconds. The chat timeout is now an idle timeout - it measures the gap between pieces of a reply rather than the whole reply, so a long answer from a slow model is no longer mistaken for a stuck one.",
    added: [
      "A Response timeout field on Preferences > Assistant > Change model, overriding the platform default for your account. Leave it blank to inherit.",
    ],
    fixed: [
      "Chat replies, chat tool calls and Formula runs were capped at 100 seconds regardless of the configured LLM timeout.",
      "Chat streaming had no timeout of its own, so a model that went silent left the reply hanging.",
      "The bundled web server cut any API request at 60 seconds, below the app's own timeout.",
    ],
  },
```

- [ ] **Step 3: Update `docs/Data_Schema.md`**

Add `LlmTimeoutSeconds` (`integer`, nullable) to the `UserSettings` column table (around line 737+), noting null = inherit, and add the `AddUserLlmTimeout` row to the migration-history table (around line 102) with its real timestamp from the generated filename.

- [ ] **Step 4: Update `docs/Overall_Synopsis_of_Platform.md`**

Two edits:
- Where per-user summarisation config is described, state the timeout's resolution chain: **user `UserSettings.LlmTimeoutSeconds` ?? platform `PlatformSettings.LlmTimeoutSeconds` ?? the server option**, and that every LLM HttpClient disables its own cap so this is the single authority.
- Beside the existing note that an outer reverse proxy must forward `/mcp` with buffering off, add that it must also allow a read timeout at least as long as the configured LLM timeout, or long generations die at that proxy regardless of app settings.

- [ ] **Step 5: Update the help article**

`apps/web/src/content/help/en/ai-model-settings.md` - document the Response timeout field: where it is, that blank inherits the platform value, that it is an idle allowance between pieces of a reply rather than a cap on the whole reply, and that raising it is the fix for a large local model being cut off. **ASCII only.**

- [ ] **Step 6: Update README and features.md in lockstep**

Find the row/bullet describing model settings in `README.md`'s Features table and the matching prose in `docs/features.md`, and add the per-user timeout to both. If neither enumerates model-settings fields at that level of detail, add nothing rather than inventing a row - and say so in your report.

Check whether `CAPABILITIES` in `releases.ts` has a model-settings row that enumerates fields; update it only if it does.

- [ ] **Step 7: Run the release tests and the help test**

```bash
cd apps/web && npm test -- src/lib/releases.test.ts src/lib/versionMirrors.test.ts src/content/help/helpContent.test.ts
```
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help/en/ai-model-settings.md README.md docs/features.md
git commit -m "chore(release): 0.215.0 - per-user LLM timeout override"
```

---

### Task 7: Full verification and PR

**Files:** none, except a possible `pr:` correction in `apps/web/src/lib/releases.ts`.

- [ ] **Step 1: Run every suite**

```bash
cd apps/web && npm test && npm run build
```
```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests && dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: all PASS. The integration run needs Docker.

- [ ] **Step 2: Verify in the running app**

The docker stack is usually already up, with the API on `localhost:8080`. **The `diariz-web-1` container serves a stale build - do not test against it.** Start the Vite dev server via `preview_start {name: "web"}` (`.claude/launch.json` defines it on port 5199); it proxies `/api` to the running API container.

**The API container also runs pre-change code**, so the new endpoint fields will not exist until it is rebuilt: `cd deploy && docker compose up -d --build api` (about 2 minutes; confirm with `curl -s localhost:8080/health`, which reports the version - it should read 0.215.0). Redis is not published to the host, so a local `dotnet run` cannot substitute.

Sign in with `SEED_EMAIL` / `SEED_PASSWORD` from `deploy/.env`. Then verify:
1. Preferences > Assistant > Change model shows a **Response timeout** field, empty, with the platform value as its placeholder.
2. Setting it to 600 and saving persists - reopen the dialog and it still reads 600.
3. Emptying it and saving returns it to the placeholder (the override cleared).
4. Entering 3 and saving shows an error rather than silently coercing.

Screenshots need the Browser pane displayed; if it is not, verify via `read_page` and `javascript_tool` and say so explicitly in your report rather than claiming a visual check you did not make.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin feat/llm-timeout-override
```
Open it with `gh pr create`. The body must state: what changed; that the three ceiling fixes are separable from the feature; **Deployment surface: server redeploy only (API + web/nginx), no desktop release**; that `Data_Schema.md` and `Overall_Synopsis_of_Platform.md` are both updated; that the migration is additive and nullable so `MaintenanceController.CurrentFormat` is deliberately **not** bumped; and the trailer:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

- [ ] **Step 4: Correct the PR number if it was a guess**

Compare the number `gh pr create` returned with the `pr:` field from Task 6 Step 2. If they differ, fix it, commit `chore(release): correct the PR number in the 0.215.0 entry`, and push. No test catches a wrong PR number.

- [ ] **Step 5: Confirm CI is green**

```bash
gh pr checks --watch
```
Expected: all checks pass. `main` has a strict up-to-date policy - rebase onto `main` and push again if the branch falls behind.
