# Chat Microphone Dictation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a microphone toggle to the chat input row that dictates the user's speech into the chat textarea near-real-time, using the browser `SpeechRecognition` API where available and an OpenAI-compatible server speech-to-text endpoint as a fallback.

**Architecture:** A pure, unit-tested `dictation.ts` module (engine selection + text merge) drives two thin impure engine adapters in `dictationEngine.ts` (`SpeechRecognition` and a server-chunking engine). The server engine reuses the recorder's existing mic-capture (`audioSource.ts`) and silence-detection (`audioLevel.ts`) code, chunks audio on each pause, and POSTs each utterance to a new `POST /api/chat/transcribe` endpoint. That endpoint forwards the audio to a server-configured OpenAI-compatible `/audio/transcriptions` endpoint via a new `DictationClient`, mirroring the existing `SummarizationClient` pattern. Config is server-level only (a new `Dictation` options section); no DB migration.

**Tech Stack:** ASP.NET Core (.NET 10) + typed `HttpClient`; React 19 + TypeScript + Vite; vitest (web) and xUnit (API) for tests.

---

## File Structure

**Server (create):**
- `src/Diariz.Api/Services/DictationClient.cs` — `IDictationClient` + `DictationClient`: multipart POST to `{ApiBase}/audio/transcriptions`, parse `{ "text": ... }`.
- `tests/Diariz.Api.Tests/DictationClientTests.cs` — client unit tests (fake HTTP handler).
- `tests/Diariz.Api.Tests/ChatTranscribeEndpointTests.cs` — endpoint unit tests.

**Server (modify):**
- `src/Diariz.Api/Configuration/AppOptions.cs` — add `DictationOptions`.
- `src/Diariz.Api/Program.cs` — register options + typed `HttpClient`.
- `src/Diariz.Api/Controllers/ChatController.cs` — add `POST transcribe` action + inject `IDictationClient` and `IOptions<DictationOptions>`.
- `src/Diariz.Api/Controllers/UserSettingsController.cs` — expose `DictationServerAvailable`.
- `src/Diariz.Api/Contracts/ApiDtos.cs` — add `ChatTranscriptionDto`; extend `UserSettingsDto`.

**Web (create):**
- `apps/web/src/lib/dictation.ts` — pure: `pickDictationEngine`, `appendTranscript`.
- `apps/web/src/lib/dictation.test.ts` — vitest.
- `apps/web/src/lib/dictationEngine.ts` — impure adapters + capability probe.

**Web (modify):**
- `apps/web/src/lib/api.ts` — add `transcribeChat(blob)`.
- `apps/web/src/lib/types.ts` — add `dictationServerAvailable` to `UserSettings`.
- `apps/web/src/components/ChatPanel.tsx` — mic button, send icon, dictation wiring.
- `apps/web/src/locales/{en,de,es,fr}/chat.json` — new keys.

**Version + docs (modify):** `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `apps/web/src/lib/releases.ts`, `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`.

---

## Task 1: `DictationOptions` config section

**Files:**
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs` (append at end of file)
- Modify: `src/Diariz.Api/Program.cs:28` area (options registration)

- [ ] **Step 1: Add the options class**

Append to `src/Diariz.Api/Configuration/AppOptions.cs` (after `IdentificationOptions`, before the final closing of the file):

```csharp
/// <summary>OpenAI-compatible speech-to-text endpoint used for chat voice dictation - the server-fallback
/// path for environments where the browser SpeechRecognition API is unavailable (the Electron desktop
/// shell, Safari, Firefox). Empty <see cref="ApiBase"/> disables the server path; the browser API is still
/// used where present. This is deliberately server-level only (dictation is infrastructure, not a per-user
/// bring-your-own-key concern like summarisation).</summary>
public class DictationOptions
{
    public const string Section = "Dictation";
    /// <summary>Base URL of the OpenAI-compatible API, e.g. http://whisper:8000/v1. Empty disables the server path.</summary>
    public string ApiBase { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "whisper-1";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>True when an STT endpoint is configured; otherwise the transcribe endpoint returns 400.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
}
```

- [ ] **Step 2: Register the options**

In `src/Diariz.Api/Program.cs`, directly below the `ChatOptions` registration (`builder.Services.Configure<ChatOptions>(...)` at line ~35), add:

```csharp
builder.Services.Configure<DictationOptions>(builder.Configuration.GetSection(DictationOptions.Section));
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Diariz.Api/Configuration/AppOptions.cs src/Diariz.Api/Program.cs
git commit -m "feat: add server-level Dictation options section"
```

---

## Task 2: `DictationClient` (forward audio to OpenAI-compatible STT)

**Files:**
- Create: `src/Diariz.Api/Services/DictationClient.cs`
- Create: `tests/Diariz.Api.Tests/DictationClientTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/DictationClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class DictationClientTests
{
    private static Stream Wav() => new MemoryStream(Encoding.ASCII.GetBytes("RIFFfake-wav-bytes"));

    private static string TranscriptionResponse(string text) =>
        JsonSerializer.Serialize(new { text });

    [Fact]
    public async Task TranscribeAsync_PostsMultipartToAudioTranscriptions_WithBearerAndModel_AndParsesText()
    {
        var handler = new FakeHttpMessageHandler(TranscriptionResponse("Hello world."));
        var client = new DictationClient(new HttpClient(handler));
        var config = new DictationRequestConfig("http://stt.test/v1", "sk-secret", "whisper-1", 30);

        var text = await client.TranscribeAsync(config, Wav(), "audio/webm", "utterance.webm");

        Assert.Equal("Hello world.", text);
        Assert.Equal("http://stt.test/v1/audio/transcriptions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.LastRequest.Headers.Authorization!.ToString());
        // Multipart body carries the model field and the file part.
        Assert.Contains("whisper-1", handler.LastRequestBody);
        Assert.Contains("utterance.webm", handler.LastRequestBody);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsAuthorization_WhenNoKeyConfigured()
    {
        var handler = new FakeHttpMessageHandler(TranscriptionResponse("x"));
        var client = new DictationClient(new HttpClient(handler));
        var config = new DictationRequestConfig("http://stt.test/v1", "", "whisper-1", 30);

        await client.TranscribeAsync(config, Wav(), "audio/webm", "utterance.webm");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~DictationClientTests"`
Expected: FAIL - `DictationClient` / `DictationRequestConfig` do not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Diariz.Api/Services/DictationClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>Effective dictation config for one request (server-level; no per-user override).</summary>
public record DictationRequestConfig(string ApiBase, string ApiKey, string Model, int TimeoutSeconds);

public interface IDictationClient
{
    /// <summary>Transcribe one short audio utterance via an OpenAI-compatible /audio/transcriptions
    /// endpoint. Returns the recognised text (may be empty for silence).</summary>
    Task<string> TranscribeAsync(
        DictationRequestConfig config, Stream audio, string contentType, string fileName,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /audio/transcriptions endpoint with a multipart body.</summary>
public class DictationClient : IDictationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public DictationClient(HttpClient http) => _http = http;

    public async Task<string> TranscribeAsync(
        DictationRequestConfig config, Stream audio, string contentType, string fileName,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(config.Model), "model");

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/audio/transcriptions") { Content = form };
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~DictationClientTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/DictationClient.cs tests/Diariz.Api.Tests/DictationClientTests.cs
git commit -m "feat: add DictationClient forwarding audio to OpenAI-compatible STT"
```

---

## Task 3: Register the `DictationClient` HTTP client

**Files:**
- Modify: `src/Diariz.Api/Program.cs:237` area (typed HttpClient registrations)

- [ ] **Step 1: Register the typed client**

In `src/Diariz.Api/Program.cs`, directly below `builder.Services.AddHttpClient<ISummarizationClient, SummarizationClient>(NoHttpTimeout);` (line ~237), add:

```csharp
builder.Services.AddHttpClient<IDictationClient, DictationClient>(NoHttpTimeout);
```

(The `NoHttpTimeout` helper disables the HttpClient-level timeout; `DictationClient` applies its own per-request timeout via `TimeoutSeconds`, matching `SummarizationClient`.)

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Diariz.Api/Program.cs
git commit -m "chore: register DictationClient typed HttpClient"
```

---

## Task 4: `POST /api/chat/transcribe` endpoint

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (add `ChatTranscriptionDto`)
- Modify: `src/Diariz.Api/Controllers/ChatController.cs` (inject deps + add action)
- Create: `tests/Diariz.Api.Tests/ChatTranscribeEndpointTests.cs`

- [ ] **Step 1: Add the response DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, near the other chat DTOs (e.g. below `ChatAttachmentDto`), add:

```csharp
/// <summary>The recognised text for one dictation utterance.</summary>
public record ChatTranscriptionDto(string Text);
```

- [ ] **Step 2: Write the failing endpoint test**

Create `tests/Diariz.Api.Tests/ChatTranscribeEndpointTests.cs`. This constructs `ChatController` directly with a fake `IDictationClient`. Note `ChatController`'s constructor has 10 dependencies; only `IDictationClient` and `IOptions<DictationOptions>` matter for these tests, so pass `null!` for the unused ones (the transcribe action does not touch them).

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatTranscribeEndpointTests
{
    private sealed class FakeDictationClient : IDictationClient
    {
        private readonly string _text;
        public FakeDictationClient(string text) => _text = text;
        public Task<string> TranscribeAsync(
            DictationRequestConfig config, Stream audio, string contentType, string fileName,
            CancellationToken ct = default) => Task.FromResult(_text);
    }

    private static ChatController BuildController(IDictationClient dictation, DictationOptions opts, Guid userId)
    {
        var controller = new ChatController(
            db: null!, chat: null!, settings: null!, contextResolver: null!, extractor: null!,
            storage: null!, urlFetcher: null!, toolSettings: null!, orchestrator: null!, rooms: null!,
            dictation: dictation, dictationOptions: Options.Create(opts));
        controller.ControllerContext = Http.Context(userId);
        return controller;
    }

    private static IFormFile WebmFile()
    {
        var bytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3 }; // EBML/webm-ish header + payload
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "utterance.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };
    }

    [Fact]
    public async Task Transcribe_ReturnsText_WhenConfigured()
    {
        var controller = BuildController(
            new FakeDictationClient("Hello world."),
            new DictationOptions { ApiBase = "http://stt.test/v1", Model = "whisper-1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        var dto = Assert.IsType<ChatTranscriptionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Hello world.", dto.Text);
    }

    [Fact]
    public async Task Transcribe_Returns400_WhenServerPathNotConfigured()
    {
        var controller = BuildController(
            new FakeDictationClient("ignored"),
            new DictationOptions { ApiBase = "" }, // disabled
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Transcribe_Returns400_WhenNoFile()
    {
        var controller = BuildController(
            new FakeDictationClient("ignored"),
            new DictationOptions { ApiBase = "http://stt.test/v1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatTranscribeEndpointTests"`
Expected: FAIL - `ChatController` has no `Transcribe` method / constructor lacks the two new params (compile error).

- [ ] **Step 4: Wire the new dependencies into `ChatController`**

In `src/Diariz.Api/Controllers/ChatController.cs`:

a) Add two fields alongside the existing ones (after `private readonly IRoomScope _rooms;`):

```csharp
    private readonly IDictationClient _dictation;
    private readonly DictationOptions _dictationOptions;
```

b) Extend the constructor signature and body. Change the constructor to:

```csharp
    public ChatController(
        DiarizDbContext db, IChatStreamClient chat, ISummarizationSettingsResolver settings,
        IChatContextResolver contextResolver, IAttachmentExtractor extractor,
        IAudioStorage storage, IUrlFetcher urlFetcher,
        IChatToolSettingsResolver toolSettings, IChatToolOrchestrator orchestrator, IRoomScope rooms,
        IDictationClient dictation, IOptions<DictationOptions> dictationOptions)
    {
        _db = db;
        _chat = chat;
        _settings = settings;
        _contextResolver = contextResolver;
        _extractor = extractor;
        _storage = storage;
        _urlFetcher = urlFetcher;
        _toolSettings = toolSettings;
        _orchestrator = orchestrator;
        _rooms = rooms;
        _dictation = dictation;
        _dictationOptions = dictationOptions.Value;
    }
```

c) Add `using Diariz.Api.Configuration;` and `using Microsoft.Extensions.Options;` to the top of the file if not already present (`Microsoft.AspNetCore.Http` is already imported for `IFormFile`).

d) Add the action (place it near the existing `Attachment` action):

```csharp
    // ---- Voice dictation (server fallback path) ----

    /// <summary>Transcribe one short audio utterance for chat voice dictation. Server-level STT config
    /// (<see cref="DictationOptions"/>); returns 400 when no STT endpoint is configured. Persists nothing.</summary>
    [HttpPost("transcribe")]
    [RequestSizeLimit(10L * 1024 * 1024)] // 10 MiB - a single dictation utterance is small
    public async Task<ActionResult<ChatTranscriptionDto>> Transcribe(
        [FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("An audio file is required.");
        if (!_dictationOptions.Enabled)
            return BadRequest("Voice dictation is not configured on this server.");

        var config = new DictationRequestConfig(
            _dictationOptions.ApiBase, _dictationOptions.ApiKey, _dictationOptions.Model,
            _dictationOptions.TimeoutSeconds);

        await using var stream = file.OpenReadStream();
        var text = await _dictation.TranscribeAsync(config, stream, file.ContentType, file.FileName, ct);
        return Ok(new ChatTranscriptionDto(text));
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ChatTranscribeEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Build the whole solution (the ChatController constructor has a second construction site in the integration tests)**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded. If `RbacIntegrationTests.cs` or any other file constructs `ChatController` directly, add `null!, Options.Create(new DictationOptions())` (or a fake) to that call. Search first:

Run: `grep -rn "new ChatController(" tests/ src/`
Fix any other construction site to pass the two new arguments.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ChatController.cs tests/Diariz.Api.Tests/ChatTranscribeEndpointTests.cs
git commit -m "feat: add POST /api/chat/transcribe dictation endpoint"
```

---

## Task 5: Expose `dictationServerAvailable` to the web

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`UserSettingsDto`)
- Modify: `src/Diariz.Api/Controllers/UserSettingsController.cs`
- Modify: `apps/web/src/lib/types.ts` (`UserSettings`)

- [ ] **Step 1: Extend `UserSettingsDto`**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, change the `UserSettingsDto` record (currently ending `RecordingPlacementMode PlacementMode, Guid? PlacementSectionId);`) to append one field:

```csharp
public record UserSettingsDto(
    string? ApiBase, string? Model, bool HasApiKey,
    string? DefaultApiBase, string? DefaultModel, bool ServerHasApiKey,
    int? ContextWindow, int DefaultContextWindow,
    bool ToolsEnabled, bool DefaultToolsEnabled, IReadOnlyList<ChatToolDto> Tools,
    bool ReasoningEnabled, string ReasoningEffort, bool DefaultReasoningEnabled, string DefaultReasoningEffort,
    RecordingPlacementMode PlacementMode, Guid? PlacementSectionId,
    // True when the server has an STT endpoint configured (the dictation server-fallback path is available).
    bool DictationServerAvailable);
```

- [ ] **Step 2: Populate it in the controller**

In `src/Diariz.Api/Controllers/UserSettingsController.cs`:

a) Add a field + constructor param for the options. Change the constructor to also take `IOptions<DictationOptions> dictationDefaults` and store `_dictationDefaults = dictationDefaults.Value;` (add `private readonly DictationOptions _dictationDefaults;`).

```csharp
    private readonly DictationOptions _dictationDefaults;

    public UserSettingsController(
        DiarizDbContext db, IApiKeyProtector protector, IOptions<SummarizationOptions> serverDefaults,
        IOptions<ChatOptions> chatDefaults, IChatToolSettingsResolver toolSettings,
        IOptions<DictationOptions> dictationDefaults)
    {
        _db = db;
        _protector = protector;
        _serverDefaults = serverDefaults.Value;
        _chatDefaults = chatDefaults.Value;
        _toolSettings = toolSettings;
        _dictationDefaults = dictationDefaults.Value;
    }
```

b) In `Get()`, append the new argument to the `UserSettingsDto` construction (currently ends `PlacementSectionId: s?.RecordingPlacementSectionId);`):

```csharp
            PlacementMode: s?.RecordingPlacementMode ?? RecordingPlacementMode.SelectedFolder,
            PlacementSectionId: s?.RecordingPlacementSectionId,
            DictationServerAvailable: _dictationDefaults.Enabled);
```

- [ ] **Step 3: Add the field to the web type**

In `apps/web/src/lib/types.ts`, inside the `UserSettings` interface, add (near the placement fields at the end of the interface):

```typescript
  /// True when the server has an STT endpoint configured (dictation server-fallback path is available).
  dictationServerAvailable: boolean;
```

- [ ] **Step 4: Build both sides**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded.

Run: `cd apps/web && npm run build`
Expected: tsc + vite build succeed. (If the DTO is asserted anywhere in .NET tests by positional construction, the build in Task 4/Step 6 would have caught it; re-run `dotnet build Diariz.slnx` if unsure.)

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserSettingsController.cs apps/web/src/lib/types.ts
git commit -m "feat: expose dictationServerAvailable in user settings"
```

---

## Task 6: Pure `dictation.ts` module (engine selection + text merge)

**Files:**
- Create: `apps/web/src/lib/dictation.ts`
- Create: `apps/web/src/lib/dictation.test.ts`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/dictation.test.ts`:

```typescript
import { describe, it, expect } from "vitest";
import { pickDictationEngine, appendTranscript } from "./dictation";

describe("pickDictationEngine", () => {
  it("prefers the browser SpeechRecognition engine when available", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: true, hasServerStt: true })).toBe("speech");
    expect(pickDictationEngine({ hasSpeechRecognition: true, hasServerStt: false })).toBe("speech");
  });

  it("falls back to the server engine when only the server STT endpoint exists", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: false, hasServerStt: true })).toBe("server");
  });

  it("returns none when neither is available", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: false, hasServerStt: false })).toBe("none");
  });
});

describe("appendTranscript", () => {
  it("returns the fragment when the box is empty", () => {
    expect(appendTranscript("", "hello")).toBe("hello");
  });

  it("joins with a single space when the box has no trailing whitespace", () => {
    expect(appendTranscript("hello", "world")).toBe("hello world");
  });

  it("does not add a second space when the box already ends in whitespace", () => {
    expect(appendTranscript("hello ", "world")).toBe("hello world");
    expect(appendTranscript("hello\n", "world")).toBe("hello\nworld");
  });

  it("trims the incoming fragment and ignores blank fragments", () => {
    expect(appendTranscript("hello", "  world  ")).toBe("hello world");
    expect(appendTranscript("hello", "   ")).toBe("hello");
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/lib/dictation.test.ts`
Expected: FAIL - cannot resolve `./dictation`.

- [ ] **Step 3: Write the implementation**

Create `apps/web/src/lib/dictation.ts`:

```typescript
// Pure helpers for chat voice dictation. No browser APIs here (mirrors audioDevices.ts / audioLevel.ts)
// so engine selection and text merging are unit-testable; dictationEngine.ts owns the impure adapters.

export type DictationStatus = "idle" | "starting" | "listening" | "error";
export type DictationEngineKind = "speech" | "server" | "none";

export interface DictationCapabilities {
  /** The browser exposes SpeechRecognition / webkitSpeechRecognition. */
  hasSpeechRecognition: boolean;
  /** The server has an OpenAI-compatible STT endpoint configured (the fallback path). */
  hasServerStt: boolean;
}

/** Choose the dictation engine: the instant browser API wins; else the server path; else nothing. */
export function pickDictationEngine(caps: DictationCapabilities): DictationEngineKind {
  if (caps.hasSpeechRecognition) return "speech";
  if (caps.hasServerStt) return "server";
  return "none";
}

/** Append a finalized transcript fragment to the current textarea value, joining with a single space
 * (unless the box is empty or already ends in whitespace). Blank fragments are ignored. */
export function appendTranscript(current: string, fragment: string): string {
  const frag = fragment.trim();
  if (!frag) return current;
  if (!current) return frag;
  return /\s$/.test(current) ? current + frag : current + " " + frag;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd apps/web && npx vitest run src/lib/dictation.test.ts`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/dictation.ts apps/web/src/lib/dictation.test.ts
git commit -m "feat: add pure dictation engine-selection and text-merge helpers"
```

---

## Task 7: Impure engine adapters (`dictationEngine.ts`)

**Files:**
- Create: `apps/web/src/lib/dictationEngine.ts`

These adapters use browser APIs (`SpeechRecognition`, `MediaRecorder`, Web Audio) and are verified live in the browser preview (this repo has no React/DOM component-testing library wired). Keep them thin: all decision logic that CAN be pure already lives in `dictation.ts` / `audioLevel.ts`.

- [ ] **Step 1: Write the adapters**

Create `apps/web/src/lib/dictationEngine.ts`:

```typescript
// Impure dictation engine adapters. Two implementations share one callback shape; ChatPanel picks between
// them via pickDictationEngine (dictation.ts). Verified live in the browser (no component-test lib here).

import { getStream, listInputDevices } from "./audioSource";
import { resolvePersistedSource, DEFAULT_CONSTRAINTS } from "./audioDevices";
import type { AudioConstraints, PersistedSource, SourceSelection } from "./audioDevices";
import { rms, normalizeLevel, nextSilenceMs } from "./audioLevel";

export interface DictationCallbacks {
  /** Live, not-yet-final text (browser path only) - shown as a preview, replaced on the next final/interim. */
  onInterim(text: string): void;
  /** A finalized utterance - committed into the input box. */
  onFinal(text: string): void;
  /** A fatal error; the engine has stopped. */
  onError(message: string): void;
}

export interface DictationEngine {
  start(cb: DictationCallbacks): Promise<void>;
  stop(): void;
}

/** Whether the browser exposes the SpeechRecognition API. */
export function hasSpeechRecognition(win: Window = window): boolean {
  const w = win as unknown as { SpeechRecognition?: unknown; webkitSpeechRecognition?: unknown };
  return Boolean(w.SpeechRecognition || w.webkitSpeechRecognition);
}

// ---- Browser SpeechRecognition engine ----

/* eslint-disable @typescript-eslint/no-explicit-any */
export function createSpeechEngine(win: Window = window): DictationEngine {
  const Ctor: any =
    (win as any).SpeechRecognition ?? (win as any).webkitSpeechRecognition;
  let rec: any = null;
  let stopped = false;

  return {
    async start(cb) {
      stopped = false;
      rec = new Ctor();
      rec.continuous = true;
      rec.interimResults = true;
      rec.onresult = (e: any) => {
        let interim = "";
        for (let i = e.resultIndex; i < e.results.length; i++) {
          const r = e.results[i];
          if (r.isFinal) cb.onFinal(r[0].transcript);
          else interim += r[0].transcript;
        }
        cb.onInterim(interim);
      };
      rec.onerror = (e: any) => {
        // "no-speech"/"aborted" are transient; surface anything else.
        if (e.error && e.error !== "no-speech" && e.error !== "aborted") cb.onError(String(e.error));
      };
      rec.onend = () => {
        // The API stops itself after a pause; keep listening until the user stops.
        if (!stopped) {
          try { rec.start(); } catch { /* already starting */ }
        }
      };
      rec.start();
    },
    stop() {
      stopped = true;
      try { rec?.stop(); } catch { /* not running */ }
      rec = null;
    },
  };
}
/* eslint-enable @typescript-eslint/no-explicit-any */

// ---- Server-chunking engine ----

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";
// A speech->silence gap this long marks an utterance boundary (much shorter than the recorder's 15s hint).
const PAUSE_MS = 800;
// Ignore chunks with less than this much cumulative speech (avoids sending pure noise/clicks).
const MIN_SPEECH_MS = 300;

function loadSavedSource(): PersistedSource | null {
  try {
    const raw = localStorage.getItem(SOURCE_KEY);
    return raw ? (JSON.parse(raw) as PersistedSource) : null;
  } catch {
    return null;
  }
}

function loadSavedConstraints(): AudioConstraints {
  try {
    const raw = localStorage.getItem(CONSTRAINTS_KEY);
    return raw ? { ...DEFAULT_CONSTRAINTS, ...(JSON.parse(raw) as Partial<AudioConstraints>) } : DEFAULT_CONSTRAINTS;
  } catch {
    return DEFAULT_CONSTRAINTS;
  }
}

/** Resolve the recorder's saved mic selection against the currently-available devices. */
async function resolveSavedSelection(): Promise<SourceSelection> {
  const saved = loadSavedSource();
  const { devices } = await listInputDevices().catch(() => ({ devices: [], hasLabels: false }));
  // "none" (system-only) makes no sense for dictation; fall back to the default mic.
  const sel = resolvePersistedSource(saved, devices);
  return sel.kind === "none" ? { kind: "default" } : sel;
}

/**
 * Server dictation: capture the recorder's saved mic, watch the input level, and each time speech is
 * followed by a >=PAUSE_MS silence, finalize the current MediaRecorder into a self-contained webm and
 * POST it via `transcribe`. Restarting the recorder per utterance keeps each blob independently decodable.
 */
export function createServerEngine(
  transcribe: (blob: Blob) => Promise<string>,
): DictationEngine {
  let session: { stream: MediaStream; stop: () => void } | null = null;
  let ctx: AudioContext | null = null;
  let raf = 0;
  let recorder: MediaRecorder | null = null;
  let chunks: Blob[] = [];
  let stopped = false;
  let sending = false;

  function flushAndSend(cb: DictationCallbacks) {
    const r = recorder;
    if (!r || r.state === "inactive") return;
    // onstop assembles the blob and POSTs it, then restarts for the next utterance.
    r.stop();
  }

  return {
    async start(cb) {
      stopped = false;
      try {
        const selection = await resolveSavedSelection();
        session = await getStream(selection, loadSavedConstraints());
      } catch (e) {
        cb.onError((e as { message?: string })?.message ?? "Could not access the microphone.");
        return;
      }

      const stream = session.stream;
      const Ctx = (window as unknown as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext })
        .AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
      if (!Ctx) { cb.onError("Web Audio is unavailable."); return; }

      ctx = new Ctx();
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 256;
      ctx.createMediaStreamSource(stream).connect(analyser);
      const buf = new Uint8Array(analyser.fftSize);

      let last: number | null = null;
      let silentMs = 0;
      let spokeMs = 0;

      const startRecorder = () => {
        chunks = [];
        recorder = new MediaRecorder(stream, { mimeType: "audio/webm" });
        recorder.ondataavailable = (e) => e.data.size > 0 && chunks.push(e.data);
        recorder.onstop = () => {
          const blob = new Blob(chunks, { type: "audio/webm" });
          if (!stopped) startRecorder(); // ready for the next utterance immediately
          if (spokeMs >= MIN_SPEECH_MS && blob.size > 0 && !sending) {
            sending = true;
            transcribe(blob)
              .then((text) => { if (text.trim()) cb.onFinal(text); })
              .catch((e) => cb.onError((e as { message?: string })?.message ?? "Transcription failed."))
              .finally(() => { sending = false; });
          }
          spokeMs = 0;
        };
        recorder.start(250);
      };
      startRecorder();

      const tick = (now: number) => {
        analyser.getByteTimeDomainData(buf);
        const level = normalizeLevel(rms(buf));
        const dt = last == null ? 0 : now - last;
        last = now;
        if (level >= 0.05) { spokeMs += dt; silentMs = 0; }
        else { silentMs = nextSilenceMs(silentMs, level, dt); }
        // Boundary: we have real speech AND a sustained pause AND we're not mid-send.
        if (spokeMs >= MIN_SPEECH_MS && silentMs >= PAUSE_MS && !sending) {
          silentMs = 0;
          flushAndSend(cb);
        }
        raf = requestAnimationFrame(tick);
      };
      raf = requestAnimationFrame(tick);
    },
    stop() {
      stopped = true;
      cancelAnimationFrame(raf);
      try { recorder?.stop(); } catch { /* inactive */ }
      recorder = null;
      session?.stop();
      session = null;
      if (ctx) { void ctx.close(); ctx = null; }
    },
  };
}
```

- [ ] **Step 2: Typecheck**

Run: `cd apps/web && npm run build`
Expected: tsc + vite build succeed. (`createServerEngine` is not imported anywhere yet; that is fine - the module typechecks on its own.)

- [ ] **Step 3: Commit**

```bash
git add apps/web/src/lib/dictationEngine.ts
git commit -m "feat: add SpeechRecognition and server-chunking dictation engines"
```

---

## Task 8: `api.transcribeChat` method

**Files:**
- Modify: `apps/web/src/lib/api.ts` (add method near `uploadChatAttachment`)

- [ ] **Step 1: Add the API method**

In `apps/web/src/lib/api.ts`, directly below the `uploadChatAttachment` method (around line 1058), add:

```typescript
  async transcribeChat(blob: Blob): Promise<string> {
    const form = new FormData();
    form.append("file", blob, "utterance.webm");
    const { data } = await http.post<{ text: string }>("/api/chat/transcribe", form);
    return data.text;
  },
```

- [ ] **Step 2: Typecheck**

Run: `cd apps/web && npm run build`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add apps/web/src/lib/api.ts
git commit -m "feat: add transcribeChat API method"
```

---

## Task 9: ChatPanel UI - mic button, send icon, dictation wiring

**Files:**
- Modify: `apps/web/src/components/ChatPanel.tsx`
- Modify: `apps/web/src/locales/en/chat.json` (+ de/es/fr)

- [ ] **Step 1: Add i18n keys (en)**

In `apps/web/src/locales/en/chat.json`, add these keys (anywhere in the object; keep valid JSON):

```json
  "dictateStart": "Dictate message",
  "dictateStop": "Stop dictation",
  "dictateHearing": "Listening…",
  "dictateFailed": "Voice dictation failed.",
```

Add the same keys to `de/chat.json`, `es/chat.json`, `fr/chat.json` with translations (no em/en dashes - use a plain ellipsis character or three dots consistent with the existing files; note the existing files use the `…` ellipsis, which is allowed - only em/en dashes are banned):
- de: `"Nachricht diktieren"`, `"Diktat beenden"`, `"Höre zu…"`, `"Sprachdiktat fehlgeschlagen."`
- es: `"Dictar mensaje"`, `"Detener dictado"`, `"Escuchando…"`, `"El dictado de voz falló."`
- fr: `"Dicter le message"`, `"Arrêter la dictée"`, `"Écoute…"`, `"Échec de la dictée vocale."`

- [ ] **Step 2: Add imports + icons to ChatPanel**

In `apps/web/src/components/ChatPanel.tsx`, add near the other lib imports (after the `chatCommands` import block):

```typescript
import { pickDictationEngine, appendTranscript } from "../lib/dictation";
import {
  hasSpeechRecognition,
  createSpeechEngine,
  createServerEngine,
  type DictationEngine,
} from "../lib/dictationEngine";
```

Add two icon components next to the existing icon definitions at the bottom of the file (after `TrashIcon`):

```tsx
const MicIcon = () => (
  <svg {...iconProps}>
    <rect x="9" y="2" width="6" height="11" rx="3" />
    <path d="M5 10a7 7 0 0 0 14 0M12 17v4M8 21h8" />
  </svg>
);
const StopSquareIcon = () => (
  <svg {...iconProps}>
    <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" stroke="none" />
  </svg>
);
const SendIcon = () => (
  <svg {...iconProps}>
    <path d="M22 2 11 13M22 2l-7 20-4-9-9-4z" />
  </svg>
);
```

- [ ] **Step 3: Add dictation state + wiring inside the component**

In the `ChatPanel` component body, add state and a controller near the other `useState`/`useRef` declarations (e.g. after `const [uploading, setUploading] = useState(false);`):

```typescript
  // Voice dictation: the mic button toggles listening; finalized speech is appended to `input`, interim
  // speech shows as a live preview above the box. Engine is chosen once from capabilities.
  const [dictating, setDictating] = useState(false);
  const [interim, setInterim] = useState("");
  const dictationRef = useRef<DictationEngine | null>(null);

  const dictationEngineKind = pickDictationEngine({
    hasSpeechRecognition: hasSpeechRecognition(),
    hasServerStt: settings?.dictationServerAvailable ?? false,
  });

  function stopDictation() {
    dictationRef.current?.stop();
    dictationRef.current = null;
    setDictating(false);
    setInterim("");
  }

  async function startDictation() {
    if (dictationEngineKind === "none") return;
    const engine =
      dictationEngineKind === "speech"
        ? createSpeechEngine()
        : createServerEngine((blob) => api.transcribeChat(blob));
    dictationRef.current = engine;
    setInterim("");
    setDictating(true);
    setError(null);
    await engine.start({
      onInterim: (text) => setInterim(text),
      onFinal: (text) => {
        setInterim("");
        setInput((cur) => appendTranscript(cur, text));
      },
      onError: (message) => {
        setError(message || t("dictateFailed"));
        stopDictation();
      },
    });
  }

  function toggleDictation() {
    if (dictating) stopDictation();
    else void startDictation();
  }

  // Stop dictation if the component unmounts while listening (releases the mic + AudioContext).
  useEffect(() => () => dictationRef.current?.stop(), []);
```

- [ ] **Step 4: Replace the input row (mic button + send icon)**

In `ChatPanel.tsx`, find the input row block that begins with `{/* Input */}` and `<div className="mt-2 flex items-end gap-2">`. Replace the whole block (the textarea + the streaming/send button) with:

```tsx
        {/* Live dictation preview (interim words before they finalize). */}
        {dictating && interim && (
          <p className="mt-2 px-1 text-xs italic text-gray-400 dark:text-gray-500">{interim}</p>
        )}

        {/* Input */}
        <div className="mt-2 flex items-end gap-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onFocus={() => setFrozenCurrent(inferredCurrent)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                if (commandMatches.length > 0 && !parseChatCommand(input)) runSlash(commandMatches[0].cmd);
                else send();
              }
            }}
            rows={2}
            placeholder={t("askPlaceholder")}
            aria-label={t("messageAria")}
            className="min-h-0 flex-1 resize-none rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />

          {/* Mic (dictation) toggle - hidden when no engine is available, disabled while a reply streams. */}
          {dictationEngineKind !== "none" && (
            <button
              type="button"
              onClick={toggleDictation}
              disabled={streaming}
              aria-label={dictating ? t("dictateStop") : t("dictateStart")}
              title={dictating ? t("dictateStop") : t("dictateStart")}
              aria-pressed={dictating}
              className={
                dictating
                  ? "flex items-center justify-center rounded bg-red-600 p-2 text-white disabled:opacity-50"
                  : "flex items-center justify-center rounded border p-2 text-gray-600 hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-300 dark:hover:bg-gray-800"
              }
            >
              {dictating ? <StopSquareIcon /> : <MicIcon />}
            </button>
          )}

          {streaming ? (
            <button
              type="button"
              onClick={stop}
              className="rounded bg-gray-200 px-3 py-1.5 text-sm dark:bg-gray-700 dark:text-gray-100"
            >
              {t("stop")}
            </button>
          ) : (
            <button
              type="button"
              onClick={send}
              disabled={!input.trim()}
              aria-label={t("send")}
              title={t("send")}
              className="flex items-center justify-center rounded bg-blue-600 p-2 text-white disabled:opacity-50"
            >
              <SendIcon />
            </button>
          )}
        </div>
```

Note: the streaming "Stop" button is left exactly as before (text). It and the send button are mutually
exclusive (only one shows at a time) and the textarea is `flex-1`, so it absorbs any button-width
difference - the panel size stays stable. Only the send button changes to an icon, per the spec.

- [ ] **Step 5: Typecheck + run the existing chat tests**

Run: `cd apps/web && npm run build`
Expected: build succeeds.

Run: `cd apps/web && npx vitest run src/components/ChatPanel.test.tsx src/lib/dictation.test.ts`
Expected: PASS. If `ChatPanel.test.tsx` asserted the send button's text (`"Send"`) it will now fail because the button is icon-only - update that assertion to query by `aria-label`/`title` (`t("send")`) instead of text. Inspect and fix if needed.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/ChatPanel.tsx apps/web/src/locales/en/chat.json apps/web/src/locales/de/chat.json apps/web/src/locales/es/chat.json apps/web/src/locales/fr/chat.json
git commit -m "feat: add voice dictation mic button and send icon to chat panel"
```

---

## Task 10: Live verification in the browser preview

**Files:** none (verification only)

- [ ] **Step 1: Start the dev server**

Use the preview tooling: `preview_start` with `{ name: "web" }` (create `.claude/launch.json` with an `npm run dev` config on port 5173 if it does not exist). The API must be running too (`dotnet run --project src/Diariz.Api` with Postgres/Redis/MinIO reachable), or point the dev proxy at an existing API.

- [ ] **Step 2: Verify the UI**

- Sign in, open the chat panel. Confirm the input row shows `[textarea] [mic] [send-icon]` and the panel width is unchanged vs. before (send is now an icon).
- Confirm the mic button renders (SpeechRecognition is present in the preview's Chromium). Click it: the icon switches to a filled red stop square, `aria-pressed` becomes true.
- Speak (or, if no mic in the sandbox, confirm via `read_console_messages` there are no errors and via `read_page` the button toggled). On a real mic, interim text appears in the preview line and finalized text lands in the textarea.
- Click stop: button returns to the mic glyph, preview clears.
- While a reply is streaming, confirm the mic button is disabled.

- [ ] **Step 3: Screenshot proof**

Take a `computer {action: "screenshot"}` of the chat panel idle and (if possible) mid-dictation, to share with the user.

- [ ] **Step 4: No commit** (verification only). Fix any issue found by editing the relevant source file and re-running the affected task's tests.

---

## Task 11: Version bump + release notes + docs

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the version to 0.126.0 in all four mirrors**

- `version.json`: `{ "version": "0.126.0" }`
- `apps/web/package.json`: `"version": "0.126.0"`
- `apps/desktop/package.json`: `"version": "0.126.0"`
- `src/Diariz.Api/Diariz.Api.csproj`: `<Version>0.126.0</Version>`

(Functional enhancement: Minor +1, Build -> 0, from 0.125.1.)

- [ ] **Step 2: Add the release entry**

In `apps/web/src/lib/releases.ts`, add a new object at the TOP of the `RELEASES` array (before the current `0.125.1` entry). Use the PR number once known - if not yet opened, use the next PR number or leave a placeholder to fill at PR time. Match the existing entry shape exactly:

```typescript
  {
    version: "0.126.0",
    date: "2026-07-12",
    pr: <PR_NUMBER>,
    headline: "Dictate chat messages with your voice",
    summary:
      "The chat box now has a microphone button. Click it to dictate your question by voice and it is " +
      "transcribed into the input box near-real-time - review and edit before you send. In Chrome and Edge " +
      "tabs it uses the browser's built-in speech recognition; elsewhere (the desktop app, Safari, Firefox) " +
      "it uses your server's speech-to-text endpoint when one is configured. The send button is now a compact " +
      "icon so the panel stays the same size.",
    added: [
      "Voice dictation in the chat box: a mic button that toggles to a stop icon while listening and " +
        "transcribes speech into the input on each pause.",
    ],
    changed: [
      "The chat send button is now an icon (paper plane) instead of text.",
    ],
  },
```

Confirm `RELEASES[0].version` equals `version.json` (asserted by `releases.test.ts`).

- [ ] **Step 3: Update the About-box `CAPABILITIES` table**

In `apps/web/src/lib/releases.ts`, find the `CAPABILITIES` markdown table and add one row (keep it one concise line, two columns, no em/en dashes):

```
| Voice dictation | Speak your chat questions - transcribed into the chat box in Chrome/Edge or via a server speech-to-text endpoint. |
```

- [ ] **Step 4: Update README Features table + docs/features.md**

In `README.md`, add a row to the Features table:

```
| Voice dictation in chat | Dictate chat questions by voice - browser speech recognition, or a server STT endpoint on the desktop app. |
```

In `docs/features.md`, add the matching full-prose bullet under the appropriate (chat) section, e.g.:

```
- **Voice dictation in chat.** The chat input has a microphone button that transcribes your speech into the
  box near-real-time (transcribing on each pause), so you can dictate a question and edit it before sending.
  In Chrome/Edge browser tabs it uses the built-in Web Speech API; in the desktop app and other browsers it
  falls back to an OpenAI-compatible speech-to-text endpoint configured on the server (`Dictation` settings).
```

- [ ] **Step 5: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, add the new endpoint + dependency where the chat/summarisation endpoints and external LLM dependencies are described:

- A line for `POST /api/chat/transcribe` (JWT; forwards one audio utterance to an OpenAI-compatible `/audio/transcriptions` endpoint; persists nothing).
- A line for the `Dictation` server config section (`ApiBase`/`ApiKey`/`Model`/`TimeoutSeconds`) - server-level, optional; empty disables the server-fallback dictation path.

(No `Data_Schema.md` change: dictation persists nothing, no schema/storage change. No `MaintenanceController.CurrentFormat` bump: no migration.)

- [ ] **Step 6: Run the release-notes test + typecheck**

Run: `cd apps/web && npx vitest run src/lib/releases.test.ts && npm run build`
Expected: PASS + build succeeds.

- [ ] **Step 7: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md
git commit -m "chore: release 0.126.0 - chat voice dictation"
```

---

## Task 12: Full test sweep

**Files:** none

- [ ] **Step 1: Run the .NET unit tests**

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS (includes the new `DictationClientTests` + `ChatTranscribeEndpointTests`). Output pristine (no warnings).

- [ ] **Step 2: Build the full solution (catches integration-test compile breaks from the ChatController ctor change)**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded.

- [ ] **Step 3: Run the web tests**

Run: `cd apps/web && npm test`
Expected: PASS.

- [ ] **Step 4: Final build**

Run: `cd apps/web && npm run build`
Expected: succeeds.

- [ ] **Step 5: (Optional, if Docker available) integration tests**

Run: `dotnet test tests/Diariz.Api.IntegrationTests`
Expected: PASS. This confirms the `ChatController` constructor change didn't break its integration construction site.

---

## Deployment surface

**Server redeploy only.** No desktop-shell files touched (`apps/desktop/src/**`, `build/**`, `electron-builder.config.js`, desktop deps are unchanged - the version bump to `apps/desktop/package.json` is a lockstep mirror only). The desktop app picks up the web/API changes automatically. Operators who want the server-fallback path (for the desktop app / Safari / Firefox) must configure a `Dictation:ApiBase` pointing at an OpenAI-compatible `/audio/transcriptions` server; without it the browser `SpeechRecognition` path still works in Chrome/Edge and the mic button simply hides where neither is available.
