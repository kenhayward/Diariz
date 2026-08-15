# Per-user LLM timeout override, and the three ceilings under it

**Date:** 2026-08-15
**Status:** approved, ready to plan
**Deployment surface:** server redeploy (API + web/nginx). No desktop release.
**Version:** 0.214.0 -> 0.215.0 (functional enhancement: Minor +1, Build reset)

## Problem

A user switched to a larger, slower local model and LLM calls began timing out at 120 seconds. They
asked for a per-model timeout override on the Preferences -> Assistant "change model" dialog.

Investigation found the request is sound but sits on top of three defects that would silently defeat it:

1. **The 120s is already a setting.** `PlatformSettings.LlmTimeoutSeconds` is a DB-backed,
   admin-editable value (default 120, currently unchanged in the live database) surfaced at
   Settings -> Model Settings. Every non-chat LLM client applies it via a linked `CancellationTokenSource`,
   and every one of those clients is registered with `NoHttpTimeout` so the setting is the single authority.
2. **`Program.cs:372` misses that lambda.** `AddLlmClient<IChatStreamClient, ChatStreamClient>()` is the
   only LLM client registered without `NoHttpTimeout`, so it keeps .NET's default **100s** cap. That
   governs chat streaming, chat tool-calling rounds, chat title generation **and formula runs**.
   `FormulaRunProcessor.cs:362-363` builds a correct `CancelAfter(TimeoutSeconds)` token that can never
   fire, because the HTTP client aborts first. Raising the admin setting above 100 does nothing for any of
   these paths.
3. **Chat streaming has no request timeout at all.** Neither `ChatController` nor `ChatToolOrchestrator`
   contains a `CancellationTokenSource`, and `ChatStreamClient` never reads `config.TimeoutSeconds`. The
   path relies entirely on the accidental 100s from defect 2.
4. **nginx `location /api/` sets no `proxy_read_timeout`** (`apps/web/nginx.conf:61-67`), so it inherits
   nginx's 60s default - below both other limits. `/api/maintenance/`, `/hubs/` and `/mcp` all set long
   explicit timeouts; the general API block does not.

## Goals

1. Make the configured timeout the real authority on every LLM path, including chat and formulas.
2. Give chat streaming a timeout with semantics that suit streaming.
3. Add a per-user override of the timeout, editable beside the model it belongs to.

## Non-goals

- Changing the admin `PlatformSettings.LlmTimeoutSeconds` field, its validation, or its UI.
- Any change to `Dictation:TimeoutSeconds` (30s, a different concern - speech-to-text, not text generation).
- Configuring the operator's **outer** reverse proxy. No code change here can lift a timeout imposed in
  front of the web container; this is called out in the deployment docs instead.
- Client-side idle detection in the browser's SSE reader.

## Design

### Fix 1 - the missing registration

`src/Diariz.Api/Program.cs:372`:

```csharp
AddLlmClient<IChatStreamClient, ChatStreamClient>(NoHttpTimeout);
```

This is required **in addition to** Fix 2, not instead of it: `HttpClient.Timeout` governs the entire
response read in .NET even under `HttpCompletionOption.ResponseHeadersRead`, so without it a stream is
aborted at 100s no matter what token the client passes.

**Test:** `tests/Diariz.Api.IntegrationTests` already boots the real `Program.cs` through
`Infrastructure/DiarizWebAppFactory.cs` (`WebApplicationFactory<Program>`), and existing tests resolve
services from `factory.Services`. `ChatStreamClient` holds its `HttpClient` privately, so the reachable
assertion is `IHttpClientFactory.CreateClient(nameof(IChatStreamClient)).Timeout` -
`AddHttpClient<TClient,TImpl>` names the client after the service type. Assert it equals
`Timeout.InfiniteTimeSpan`, and assert the same for one already-correct client so the test pins the rule
rather than one registration.

### Fix 2 - an inactivity timeout for streaming

Every other client caps **total** call duration. For a streamed chat that is the wrong shape: a slow local
model producing a long answer legitimately streams for minutes, and a total cap would abort exactly the
case this work exists to support. The streaming path therefore applies `config.TimeoutSeconds` as an
**inactivity timeout** - the allowance is between chunks, reset on every line received.

Implemented in `src/Diariz.Api/Services/ChatStreamClient.cs`, in both `StreamAsync` (read loop at 64-73)
and `StreamChunksAsync` (read loop at 100-110). The single blocking await between chunks is
`reader.ReadLineAsync(ct)`, which becomes:

```csharp
string? line;
try
{
    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
    idle.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
    line = await reader.ReadLineAsync(idle.Token);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    throw new ChatStreamException($"The model sent nothing for {config.TimeoutSeconds}s, so the response was stopped.");
}
if (line is null) break;
```

Three things make this shape necessary rather than incidental:

- **It must throw `ChatStreamException`, not a bare `OperationCanceledException`.**
  `ChatController.cs:204-207` catches `OperationCanceledException` **unconditionally** to handle the Stop
  button, so a bare cancellation would end the stream with no `done` frame and no `error` frame. The
  browser's reader (`apps/web/src/lib/api.ts:1337-1339`) only surfaces an `error` frame, so the turn would
  end in silent truncation - a worse failure than the timeout. `ChatStreamException` is the one exception
  type `ChatController.cs:208-211` converts into a client-visible `error` frame.
- **The `when (!ct.IsCancellationRequested)` filter distinguishes a timeout from a client disconnect.**
  This is the codebase's established idiom (`ChatStreamClient.cs:141`, documented at
  `FormulaRunner.cs:98-100`). A user pressing Stop must stay quiet.
- **The read and the `yield return` are separated.** C# forbids `yield return` inside a `try` block that
  has a `catch` clause, so the line is read inside the guarded block and yielded after it.

Because the timer lives inside `StreamChunksAsync`, it covers only time spent waiting on the model. Tool
execution happens between stream rounds in `ChatToolOrchestrator.cs:98` and is deliberately not counted.

Keep-alive comment lines and blank SSE separators count as activity, which is correct - they are proof the
upstream is alive.

### Fix 3 - nginx

`apps/web/nginx.conf`, the `location /api/` block, gains an explicit read/send timeout in the style the
neighbouring blocks already use, with a comment explaining why. Buffering needs **no** change:
`ChatController.cs:163-166` already sends `X-Accel-Buffering: no` and calls `DisableBuffering()`, and nginx
honours that header per-response, so the SSE stream is not buffered today. The only real gap is the 60s
default read timeout.

The value should be comfortably above any plausible per-request LLM timeout without being unbounded; `1h`
matches `/hubs/` and `/mcp`.

### The per-user override

`UserSettings.ChatContextWindow` is the exact structural precedent - a nullable int override with a
server-side default companion - and is followed field for field.

| Layer | Change |
|---|---|
| Entity | `UserSettings.LlmTimeoutSeconds` as `int?` (null = inherit) |
| Migration | `AddUserLlmTimeout`. Additive nullable column, so forward-restore-safe: **no `MaintenanceController.CurrentFormat` bump** |
| Resolvers | `SummarizationSettingsResolver` and `EmbeddingSettingsResolver` both become `user ?? platform ?? server option` |
| GET DTO | `UserSettingsDto` gains `int? LlmTimeoutSeconds` and `int DefaultLlmTimeoutSeconds` (the resolved platform-or-server value, so the dialog can show what is inherited) |
| PUT DTO | `UpdateUserSettingsRequest` gains `int? LlmTimeoutSeconds` |
| Controller | Tri-state, mirroring the admin field's floor: absent = unchanged, `0` = clear, `>= 5` = set, `1-4` = 400 |
| UI | A number input in `ModelDialog.tsx` after the reasoning block, placeholder showing the inherited default |

**Both resolvers matter.** `EmbeddingSettingsResolver.cs:78` duplicates the platform-singleton read; changing
only the summarisation resolver would leave embeddings silently ignoring the user's value.

**Precedence is user-wins, uncapped** - the same shape as every other per-user LLM setting here. This is a
self-hosted platform where users point at their own endpoints and models, so an admin ceiling would
reintroduce the exact failure mode this work removes: a setting that silently does not apply.

## Testing

TDD throughout - failing test first.

- `SummarizationSettingsResolverTests` - the two existing timeout tests (lines 102-119) become three tiers:
  user value wins over platform; platform wins when the user's is null; server option when neither exists.
- `EmbeddingSettingsResolverTests` - the single combined timeout test (lines 91-105) gains the user tier.
- `UserSettingsControllerTests` - GET returns the user value and the default companion; PUT sets, clears
  with `0`, leaves unchanged when absent, and rejects `1-4` with 400.
- `UserSettingsIntegrationTests` - the column persists as NULL by default (the file already has a
  raw-SQL DB-default pattern to follow).
- **New integration test** for Fix 1, per the Fix 1 section above.
- **New unit test** for Fix 2: `ChatStreamClientTests` already drives a real `ChatStreamClient` over canned
  SSE via `FakeHttpMessageHandler`. Extend it with a handler that stalls mid-body, and assert (a) a stall
  longer than the timeout throws `ChatStreamException`, (b) a stream that keeps producing lines slower than
  the total timeout but faster than the idle allowance completes normally - this second case is what proves
  the timeout is an inactivity timer and not a total-duration cap, and it is the test that would fail if
  someone later "simplified" it to `CancelAfter` on the whole call.
- `ModelDialog.test.tsx` - the field renders, shows the inherited default, and is sent on save.
- Nginx config has no test; the change is comment-documented.

## Release checklist

1. `version.json` -> **0.215.0** plus all four mirrors.
2. `RELEASES[0]` entry.
3. `CAPABILITIES` - the Model Settings row, if it enumerates the fields.
4. README Features row and `docs/features.md` bullet, in lockstep, where they describe model settings.
5. `docs/Data_Schema.md` - the `UserSettings` column table and the migration-history table.
6. `docs/Overall_Synopsis_of_Platform.md` - the timeout resolution chain (user -> platform -> server) and
   the nginx/outer-proxy timeout guidance, alongside the existing note that an outer proxy must forward
   `/mcp` with buffering off.
7. Help article `apps/web/src/content/help/en/ai-model-settings.md` - the new field and what it inherits.
8. Regenerate `integrations/n8n-nodes-diariz/generated/openapi.snapshot.json` (`npm run generate`); the DTO
   change drifts it and CI fails on drift.
9. New UI strings in all four locales (`apps/web/src/locales/{en,de,es,fr}/account.json`).

No em/en dashes in user-facing text; help articles ASCII only.
