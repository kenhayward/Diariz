# Drag an attachment into the chat composer Implementation Plan

> **Shipped in v0.248.0 (PR #602).** Merged retrospectively on 2026-08-27 as a design record; the checkboxes below are preserved as written and are not outstanding work.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user drag an existing attachment - from a recording's Attachments tab, from a folder's aggregated list, or from a folder's own attachments - onto the chat composer, and have its text join the context pill, clickable to read, exactly as OCR-extracted text does today.

**Architecture:** The per-attachment half of `ChatController.LoadAttachmentDocumentsAsync` becomes a reusable `IAttachmentTextResolver` (file -> object storage + extractor; URL -> the SSRF-guarded fetcher). A new `POST /api/chat/attachment/library` resolves one attachment by id, gated by the same access rule its owning controller uses. On the client, a shared drag-handle component puts one payload type on the drag, and the composer's existing drop zone branches on the MIME type and merges the fetched text into the single context pill using the OCR accumulation shape.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core; React 19 + TypeScript + Vite + Tailwind v4; xUnit with the `TestSupport` fakes; vitest + @testing-library/react.

Source spec: [docs/superpowers/specs/2026-08-24-chat-panel-three-changes-design.md](../specs/2026-08-24-chat-panel-three-changes-design.md)

## Global Constraints

- **TDD is required.** Write the failing test, run it and see it fail, then write the minimal code.
- **A passing run has no errors or warnings.**
- **No em dashes or en dashes in user-facing text.** Plain hyphen `-`.
- **Every new i18n key goes in all four locales:** `apps/web/src/locales/{en,de,es,fr}/chat.json` (and `workspace.json` where noted). `apps/web/src/locales.test.ts` fails the build if key sets differ or a value is empty.
- **`apps/web` has no `jest-dom`.** Use plain truthiness assertions, never `toBeInTheDocument()`.
- **`fireEvent` fires handlers the browser would refuse** (it will click a disabled input). `@testing-library/user-event` **is** installed - use it where a real gesture matters. Drag/drop has no `user-event` equivalent, so the existing `fireEvent.drop` + stub `dataTransfer` pattern in `ChatPanel.test.tsx` is correct there.
- **This is an enhancement, not a fix, so it needs no GitHub issue.**
- **`main` is branch-protected.** Branch, push, open a PR. Never commit to `main`, never merge locally.
- **Version bump:** functional enhancement, so Minor +1 and Build reset. Read `version.json` and apply the rule to what is there.
- **Do not use `git add -A`.** Stage explicit paths.
- **Deployment surface:** server redeploy only.
- **`--filter "Name=X"` does not work in this repo.** Use `--filter "FullyQualifiedName~X"`.

---

## File Structure

**Server**

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/AttachmentTextResolver.cs` (create) | `AttachmentRef`, `IAttachmentTextResolver`, `AttachmentTextResolver`. The single place that turns one attachment into text. |
| `src/Diariz.Api/Program.cs` (modify) | Register the resolver as **scoped** (it depends on the scoped `IUrlFetcher`). |
| `src/Diariz.Api/Contracts/ApiDtos.cs` (modify) | `ChatLibraryAttachmentRequest`. |
| `src/Diariz.Api/Controllers/ChatController.cs` (modify) | New `POST attachment/library`; `LoadAttachmentDocumentsAsync` delegates to the resolver. |

**Web**

| File | Responsibility |
|---|---|
| `apps/web/src/lib/dragTypes.ts` (modify) | `ATTACHMENT_DRAG_TYPE` and the payload type. |
| `apps/web/src/components/AttachmentDragHandle.tsx` (create) | One grip control that sets the drag payload. Used by all three lists. |
| `apps/web/src/components/AttachmentsManager.tsx` (modify) | Grip in each row, recording scope. |
| `apps/web/src/components/FolderAttachmentsList.tsx` (modify) | Grip in each row, recording scope (per-row `recordingId`). |
| `apps/web/src/components/FolderAttachmentsManager.tsx` (modify) | Grip in each row, section scope. |
| `apps/web/src/lib/api.ts` (modify) | `chatAttachmentFromLibrary`. |
| `apps/web/src/components/ChatPanel.tsx` (modify) | Branch the drop; the generalised accumulation rule. |
| `apps/web/src/locales/{en,de,es,fr}/{chat,workspace}.json` (modify) | New keys. |

---

## Task 1: `IAttachmentTextResolver`

**Files:**
- Create: `src/Diariz.Api/Services/AttachmentTextResolver.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/AttachmentTextResolverTests.cs`

**Interfaces:**
- Consumes: `IAttachmentExtractor` (`bool IsSupported(string fileName, string? contentType)`, `AttachmentText Extract(string fileName, string? contentType, byte[] bytes)`, and the record `AttachmentText(string Name, string Text, int Chars)`); `IAudioStorage.OpenReadAsync(string blobKey, CancellationToken)`; `IUrlFetcher.FetchTextAsync(string url, CancellationToken)`.
- Produces:
  - `public readonly record struct AttachmentRef(AttachmentKind Kind, string Name, string? BlobKey, string? ContentType, string? Url);`
  - `public interface IAttachmentTextResolver { Task<AttachmentText?> ResolveAsync(AttachmentRef attachment, CancellationToken ct = default); }`

  Returns null for: a URL that fetches nothing, a file whose type is unsupported, an extraction that yields only whitespace, or any exception. Never throws.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/AttachmentTextResolverTests.cs`. Both fakes already exist in `tests/Diariz.Api.TestSupport/Fakes.cs`: `FakeAudioStorage` exposes a `Dictionary<string, byte[]> Objects` and its `OpenReadAsync` **throws `KeyNotFoundException`** for an unknown key; `FakeUrlFetcher` exposes a `Dictionary<string, string?> Texts` and returns null for an unknown URL. This repo uses hand-rolled fakes in `TestSupport`, **never** a mocking library.

```csharp
using System.Text;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Turning ONE attachment into text. Extracted out of ChatController so a single dropped attachment
/// and the bulk "include attachments" path cannot drift apart.</summary>
public class AttachmentTextResolverTests
{
    private static AttachmentTextResolver Make(FakeAudioStorage storage, FakeUrlFetcher urls) =>
        new(new AttachmentExtractor(), storage, urls);

    [Fact]
    public async Task ResolvesAFileFromStorage()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = Encoding.UTF8.GetBytes("Hello from the document.");

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "notes.txt", "k1", "text/plain", null));

        Assert.NotNull(result);
        Assert.Equal("notes.txt", result!.Name);
        Assert.Contains("Hello from the document.", result.Text);
    }

    [Fact]
    public async Task ResolvesAUrlThroughTheFetcher()
    {
        var urls = new FakeUrlFetcher();
        urls.Texts["https://example.test/x"] = "Fetched page text.";

        var result = await Make(new FakeAudioStorage(), urls).ResolveAsync(
            new AttachmentRef(AttachmentKind.Url, "A link", null, null, "https://example.test/x"));

        Assert.Equal("Fetched page text.", result!.Text);
        Assert.Equal("A link", result.Name);
    }

    [Fact]
    public async Task ReturnsNull_ForAnUnsupportedFileType()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = [1, 2, 3];

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "bundle.zip", "k1", "application/zip", null));

        Assert.Null(result);
        // Rejected before any blob was read - FakeAudioStorage records every key it was asked for.
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheExtractionIsEmpty()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = Encoding.UTF8.GetBytes("   ");

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "blank.txt", "k1", "text/plain", null));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheUrlFetchFails()
    {
        // An unknown URL returns null from FakeUrlFetcher, which is what an unreachable or blocked one does.
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.Url, "A link", null, null, "https://example.test/x"));

        Assert.Null(result);
    }

    /// <summary>A missing blob must not take the caller down - the bulk path swallowed these and the drop
    /// path turns null into a clean 400. FakeAudioStorage throws KeyNotFoundException for an unknown key.</summary>
    [Fact]
    public async Task ReturnsNull_WhenStorageThrows()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "gone.txt", "no-such-key", "text/plain", null));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenAFileHasNoBlobKey()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "orphan.txt", null, "text/plain", null));

        Assert.Null(result);
    }

    /// <summary>A cancelled request is not a failed attachment: the catch-all must not turn it into a
    /// silent null, or a cancelled chat turn would look like an unreadable document.</summary>
    [Fact]
    public async Task PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
                new AttachmentRef(AttachmentKind.Url, "A link", null, null, "https://example.test/x"), cts.Token));
    }
}
```

`PropagatesCancellation` only passes if `FakeUrlFetcher` actually observes the token. It does not today (it returns a completed task), so either add `ct.ThrowIfCancellationRequested()` to `FakeUrlFetcher.FetchTextAsync` in `TestSupport` - correct, and closer to the real fetcher - or drop this test. Do not leave it passing for the wrong reason.

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AttachmentTextResolverTests"
```

Expected: compile error - `AttachmentTextResolver` does not exist.

- [ ] **Step 3: Write the resolver**

Create `src/Diariz.Api/Services/AttachmentTextResolver.cs`:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>The fields of an attachment this resolver needs, independent of which table it came from.
/// <c>Attachment</c> (recording-owned) and <c>SectionAttachment</c> (folder-owned) are shape-identical here,
/// and a struct keeps the two callers from having to share a base type they otherwise do not need.</summary>
public readonly record struct AttachmentRef(
    AttachmentKind Kind, string Name, string? BlobKey, string? ContentType, string? Url);

/// <summary>Turns one attachment into text for chat context: an uploaded file is streamed from object storage
/// and extracted; a URL is fetched behind the existing SSRF guards.</summary>
public interface IAttachmentTextResolver
{
    /// <summary>The attachment's text, or null when there is none to be had - an unsupported file type, an
    /// empty extraction, an unreachable URL, or a read that failed. Never throws: the bulk chat-context path
    /// must not fail a whole turn over one bad attachment, and the single-drop path turns null into a 400.</summary>
    Task<AttachmentText?> ResolveAsync(AttachmentRef attachment, CancellationToken ct = default);
}

/// <summary>Extracted from <c>ChatController.LoadAttachmentDocumentsAsync</c> so that dropping one attachment
/// onto the composer and ticking "include attachments" read the same bytes the same way.</summary>
public sealed class AttachmentTextResolver(
    IAttachmentExtractor extractor, IAudioStorage storage, IUrlFetcher urls) : IAttachmentTextResolver
{
    public async Task<AttachmentText?> ResolveAsync(AttachmentRef a, CancellationToken ct = default)
    {
        try
        {
            if (a.Kind == AttachmentKind.Url)
            {
                if (string.IsNullOrWhiteSpace(a.Url)) return null;
                var text = await urls.FetchTextAsync(a.Url, ct);
                return string.IsNullOrWhiteSpace(text) ? null : new AttachmentText(a.Name, text!, text!.Length);
            }

            if (string.IsNullOrWhiteSpace(a.BlobKey)) return null;
            if (!extractor.IsSupported(a.Name, a.ContentType)) return null;

            await using var stream = await storage.OpenReadAsync(a.BlobKey!, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var extracted = extractor.Extract(a.Name, a.ContentType, buffer.ToArray());
            return string.IsNullOrWhiteSpace(extracted.Text) ? null : extracted;
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled request is not a failed attachment
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Register it**

In `src/Diariz.Api/Program.cs`, beside `AddSingleton<IAttachmentExtractor, AttachmentExtractor>()`:

```csharp
// Scoped, NOT singleton: it depends on IUrlFetcher, which is scoped. A singleton capturing a scoped service
// is a captive dependency and DI validation refuses it at startup.
builder.Services.AddScoped<IAttachmentTextResolver, AttachmentTextResolver>();
```

- [ ] **Step 5: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AttachmentTextResolverTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/AttachmentTextResolver.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/AttachmentTextResolverTests.cs tests/Diariz.Api.TestSupport/Fakes.cs
git commit -m "refactor(api): extract single-attachment text resolution from the chat controller"
```

---

## Task 2: Make the bulk path use the resolver

**Files:**
- Modify: `src/Diariz.Api/Controllers/ChatController.cs`
- Test: `tests/Diariz.Api.Tests/` - extend whichever file already covers `includeAttachments`

**Interfaces:**
- Consumes: `IAttachmentTextResolver` from Task 1.
- Produces: `ChatController`'s constructor gains `IAttachmentTextResolver attachmentText` (append it last). Find and fix every construction site:

```bash
grep -rn "new ChatController" --include=*.cs tests src | grep -v "/obj/"
```

- [ ] **Step 1: Write the characterisation test first**

This is a refactor, so a test must prove the behaviour is unchanged before you change it.

`tests/Diariz.Api.Tests/ChatControllerTests.cs:424` already has `Stream_IncludeAttachments_AddsFileAndUrlTextToTheSystemPrompt`, which covers the happy path for both kinds. Run it now and confirm it passes. What it does **not** cover is the skip-on-failure behaviour, which is the part most at risk from this refactor - so add that first, next to it:

```csharp
    /// <summary>One unreadable attachment must never fail the whole chat turn. Characterised before the
    /// resolver extraction so the refactor cannot quietly change it.</summary>
    [Fact]
    public async Task Stream_IncludeAttachments_SkipsAnAttachmentThatCannotBeRead()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, chat, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);

        storage.Objects["good"] = Encoding.UTF8.GetBytes("The widget must be blue.");
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "spec.txt", ContentType = "text/plain", BlobKey = "good", SizeBytes = 10, Ordinal = 0,
        });
        // Its blob is not in storage at all - FakeAudioStorage throws for an unknown key, exactly as a
        // deleted object would.
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "missing.txt", ContentType = "text/plain", BlobKey = "gone", SizeBytes = 10, Ordinal = 1,
        });
        await db.SaveChangesAsync();

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "What colour?")], IncludeAttachments: true),
            default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("The widget must be blue.", system);
        Assert.DoesNotContain("missing.txt", system);
    }
```

Run it and confirm it passes **before** touching `LoadAttachmentDocumentsAsync`. A characterisation test written after the refactor proves nothing.

- [ ] **Step 2: Rewrite `LoadAttachmentDocumentsAsync` over the resolver**

Replace the body of the `foreach` in `ChatController.LoadAttachmentDocumentsAsync` with:

```csharp
        var docs = new List<TranscriptContext>();
        foreach (var a in attachments)
        {
            var text = await _attachmentText.ResolveAsync(
                new AttachmentRef(a.Kind, a.Name, a.BlobKey, a.ContentType, a.Url), ct);
            // Still skip silently here: one bad attachment must not fail a whole chat turn. The single-drop
            // endpoint reports its failures instead, because there the user is waiting on that one document.
            if (text is not null) docs.Add(new TranscriptContext(text.Name, text.Text));
        }
        return docs;
```

Add the field, constructor parameter and assignment for `_attachmentText` in the same style as the existing ones.

- [ ] **Step 3: Run the tests**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
```

Expected: build succeeded, 0 warnings; all pass, including the characterisation test. Build `Diariz.slnx` so a missed `new ChatController(...)` in the integration project surfaces now.

- [ ] **Step 4: Commit**

```bash
git add src/Diariz.Api/Controllers/ChatController.cs tests/Diariz.Api.Tests
git commit -m "refactor(api): route bulk attachment context through the shared resolver"
```

---

## Task 3: `POST /api/chat/attachment/library`

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Controllers/ChatController.cs`
- Test: `tests/Diariz.Api.Tests/ChatControllerTests.cs` (append a new region)

**Interfaces:**
- Consumes: `IAttachmentTextResolver` (Task 1); `IRoomScope.ViewableSectionAsync(Guid userId, Guid sectionId)` - **read its real signature and return type in `src/Diariz.Api/Services/RoomScope.cs` before use**; `SectionAttachmentsController` calls it as `await _rooms.ViewableSectionAsync(UserId, sectionId) is null` for its read gate, so mirror that.
- Produces:
  - `public record ChatLibraryAttachmentRequest(Guid AttachmentId, Guid? RecordingId = null, Guid? SectionId = null);`
  - `POST /api/chat/attachment/library` returning the existing `ChatAttachmentDto(string Name, int Chars, string Text)`.

- [ ] **Step 1: Write the failing tests**

These go in `tests/Diariz.Api.Tests/ChatControllerTests.cs` rather than a new file, so they can reuse its private `Build(userId, storage:, urlFetcher:)` helper and `SeedTranscribedRecording`. Append after the existing "Attachments as chat context" region:

```csharp
    // ---- One existing attachment, read into chat context (the drag-and-drop endpoint) ----

    /// <summary>Seeds a File attachment on a recording owned by <paramref name="ownerId"/>, with its blob in
    /// fake storage.</summary>
    private static async Task<Guid> SeedFileAttachment(
        DiarizDbContext db, FakeAudioStorage storage, Guid recordingId, string name, string contentType, string body)
    {
        var key = Guid.NewGuid().ToString("N");
        storage.Objects[key] = Encoding.UTF8.GetBytes(body);
        var a = new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, Kind = AttachmentKind.File,
            Name = name, ContentType = contentType, BlobKey = key, SizeBytes = body.Length, Ordinal = 0,
        };
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    [Fact]
    public async Task LibraryAttachment_ReturnsTheTextOfARecordingAttachment()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "plan.txt", "text/plain", "Quarterly plan text.");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.Equal("plan.txt", res.Value!.Name);
        Assert.Contains("Quarterly plan text.", res.Value.Text);
    }

    [Fact]
    public async Task LibraryAttachment_ReturnsAUrlAttachmentsFetchedText()
    {
        var me = Guid.NewGuid();
        var fetcher = new FakeUrlFetcher();
        var (controller, db, _, _) = Build(me, urlFetcher: fetcher);
        var rid = await SeedTranscribedRecording(db, me);
        var a = new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.Url,
            Name = "Roadmap", Url = "https://example.com/roadmap", Ordinal = 0,
        };
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        fetcher.Texts["https://example.com/roadmap"] = "Ship in Q3.";

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(a.Id, RecordingId: rid), default);

        Assert.Equal("Ship in Q3.", res.Value!.Text);
    }

    /// <summary>Ownership is the access rule, and a stranger must not be able to tell "not yours" from
    /// "no such id" - both are 404.</summary>
    [Fact]
    public async Task LibraryAttachment_NotFound_WhenTheRecordingBelongsToSomeoneElse()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, Guid.NewGuid()); // someone else's recording
        var aid = await SeedFileAttachment(db, storage, rid, "plan.txt", "text/plain", "Secret.");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<NotFoundResult>(res.Result);
        Assert.Empty(storage.Reads); // rejected before any blob was read
    }

    [Fact]
    public async Task LibraryAttachment_NotFound_WhenTheAttachmentDoesNotExist()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
        var rid = await SeedTranscribedRecording(db, me);

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(Guid.NewGuid(), RecordingId: rid), default);

        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenNeitherRecordingNorSectionIsGiven()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(Guid.NewGuid()), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenBothRecordingAndSectionAreGiven()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(Guid.NewGuid(), RecordingId: Guid.NewGuid(), SectionId: Guid.NewGuid()),
            default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    /// <summary>The bulk path skips these silently, which is right when a whole turn is at stake. Here the
    /// user dropped this one document and is waiting on it, so it says why.</summary>
    [Fact]
    public async Task LibraryAttachment_BadRequest_ForAnUnsupportedFileType()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "bundle.zip", "application/zip", "PK...");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenNothingCouldBeExtracted()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "blank.txt", "text/plain", "   ");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    /// <summary>A folder attachment is gated on being able to VIEW the folder, mirroring
    /// SectionAttachmentsController rather than inventing a second rule.</summary>
    [Fact]
    public async Task LibraryAttachment_ResolvesAFolderAttachmentForAViewerOfThatFolder()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(me);
        var section = new Section { Id = Guid.NewGuid(), UserId = me, RoomId = roomId, Name = "Folder" };
        db.Sections.Add(section);
        storage.Objects["fk"] = Encoding.UTF8.GetBytes("Folder brief text.");
        var a = new SectionAttachment
        {
            Id = Guid.NewGuid(), SectionId = section.Id, UploadedByUserId = me, Kind = AttachmentKind.File,
            Name = "brief.txt", ContentType = "text/plain", BlobKey = "fk", SizeBytes = 18, Ordinal = 0,
        };
        db.SectionAttachments.Add(a);
        await db.SaveChangesAsync();

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(a.Id, SectionId: section.Id), default);

        Assert.Contains("Folder brief text.", res.Value!.Text);
    }

    [Fact]
    public async Task LibraryAttachment_NotFound_ForAFolderTheCallerCannotView()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        Users.Ensure(db, other);
        var otherRoom = await new RoomScope(db).PersonalRoomIdAsync(other);
        var section = new Section { Id = Guid.NewGuid(), UserId = other, RoomId = otherRoom, Name = "Theirs" };
        db.Sections.Add(section);
        storage.Objects["fk"] = Encoding.UTF8.GetBytes("Private brief.");
        db.SectionAttachments.Add(new SectionAttachment
        {
            Id = Guid.NewGuid(), SectionId = section.Id, UploadedByUserId = other, Kind = AttachmentKind.File,
            Name = "brief.txt", ContentType = "text/plain", BlobKey = "fk", SizeBytes = 14, Ordinal = 0,
        });
        await db.SaveChangesAsync();

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(db.SectionAttachments.First().Id, SectionId: section.Id), default);

        Assert.IsType<NotFoundResult>(res.Result);
        Assert.Empty(storage.Reads);
    }
```

Two things to confirm as you write these, rather than assuming:
- Whether `NotFound()` here surfaces as `NotFoundResult` or `NotFoundObjectResult` - adjust the `Assert.IsType<>` to whatever the controller actually returns; do not change the controller to suit the test.
- Whether `Users.Ensure(db, other)` is the right helper for seeding a second user in this file (`Build` already calls it for the caller).

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LibraryAttachment"
```

Expected: compile error - no `LibraryAttachment` method.

- [ ] **Step 3: Add the request DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, beside `ChatAttachmentDto`:

```csharp
/// <summary>Which existing attachment to read into chat context. Exactly one of <paramref name="RecordingId"/>
/// and <paramref name="SectionId"/> identifies where it hangs, and therefore which access rule applies.</summary>
public record ChatLibraryAttachmentRequest(Guid AttachmentId, Guid? RecordingId = null, Guid? SectionId = null);
```

- [ ] **Step 4: Add the endpoint**

In `ChatController`, directly after the existing `Attachment` action:

```csharp
    [HttpPost("attachment/library")]
    [EndpointSummary("Read an existing attachment into chat context")]
    [EndpointDescription(
        "Returns the **extracted text** of an attachment you already have - one filed against a recording, or " +
        "one filed against a folder - so it can be added to a chat turn as context. Nothing is stored and " +
        "nothing is copied: this reads the attachment you already own.\n\n" +
        "Give exactly one of `recordingId` or `sectionId`, naming where the attachment hangs. A recording " +
        "attachment requires that the recording is yours; a folder attachment requires that you can see the " +
        "folder. 404 for anything you cannot reach - the two are not distinguished, so a stranger cannot " +
        "probe for ids. 400 for a file type with no text in it (an archive, an image) or a document that " +
        "yields nothing.")]
    public async Task<ActionResult<ChatAttachmentDto>> LibraryAttachment(
        ChatLibraryAttachmentRequest req, CancellationToken ct)
    {
        if (req.RecordingId is null == (req.SectionId is null))
            return BadRequest("Give exactly one of recordingId or sectionId.");

        AttachmentRef? target = null;
        if (req.RecordingId is { } recordingId)
        {
            // Ownership is the access rule for a recording attachment, exactly as the bulk context path
            // requires (a.Recording!.UserId == UserId).
            var a = await _db.Attachments.FirstOrDefaultAsync(
                x => x.Id == req.AttachmentId && x.RecordingId == recordingId && x.Recording!.UserId == UserId, ct);
            if (a is not null) target = new AttachmentRef(a.Kind, a.Name, a.BlobKey, a.ContentType, a.Url);
        }
        else if (req.SectionId is { } sectionId)
        {
            // Folder attachments are gated on being able to VIEW the folder, mirroring
            // SectionAttachmentsController's read gate rather than inventing a second rule.
            if (await _rooms.ViewableSectionAsync(UserId, sectionId) is null) return NotFound();
            var a = await _db.SectionAttachments.FirstOrDefaultAsync(
                x => x.Id == req.AttachmentId && x.SectionId == sectionId, ct);
            if (a is not null) target = new AttachmentRef(a.Kind, a.Name, a.BlobKey, a.ContentType, a.Url);
        }

        if (target is not { } attachment) return NotFound();

        // Checked here rather than inside the resolver so the message can say WHY. The resolver returns a
        // bare null because its other caller (the bulk path) skips silently and needs no reason.
        if (attachment.Kind == AttachmentKind.File && !_extractor.IsSupported(attachment.Name, attachment.ContentType))
            return BadRequest("Only PDF, text, Office (.docx/.xlsx/.pptx), email (.eml) and calendar (.ics) files can be read as text.");

        var text = await _attachmentText.ResolveAsync(attachment, ct);
        if (text is null) return BadRequest("No text could be read from this attachment.");
        return new ChatAttachmentDto(text.Name, text.Chars, text.Text);
    }
```

Check that `DiarizDbContext` exposes `SectionAttachments` under that name before relying on it; if the `DbSet` is named differently, use the real name.

- [ ] **Step 5: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LibraryAttachment"
```

Expected: all pass.

- [ ] **Step 6: Regenerate the OpenAPI snapshot and the n8n node**

`api/chat` is in the published document (only `api/admin`, `api/platform`, `api/oauth` and `api/maintenance` are excluded).

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

Run it **twice**: the test rewrites its own snapshot, so run 1 fails and run 2 passes with no code change. Commit the regenerated file.

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

`generated/index.ts` does **not** self-heal; a stale copy reds the "n8n community node" check. Commit it.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ChatController.cs tests/Diariz.Api.Tests/ChatControllerTests.cs
git add tests/Diariz.Api.Tests/Snapshots integrations/n8n-nodes-diariz/generated
git commit -m "feat(api): read an existing attachment into chat context"
```

Confirm the real snapshot path with `git status` before staging.

---

## Task 4: The drag handle and the three lists

**Files:**
- Modify: `apps/web/src/lib/dragTypes.ts`
- Create: `apps/web/src/components/AttachmentDragHandle.tsx`
- Create: `apps/web/src/components/AttachmentDragHandle.test.tsx`
- Modify: `apps/web/src/components/AttachmentsManager.tsx`
- Modify: `apps/web/src/components/FolderAttachmentsList.tsx`
- Modify: `apps/web/src/components/FolderAttachmentsManager.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `export const ATTACHMENT_DRAG_TYPE = "application/x-diariz-attachment";`
  - `export interface AttachmentDragPayload { scope: "recording" | "section"; ownerId: string; attachmentId: string; name: string; }`
  - `<AttachmentDragHandle scope ownerId attachmentId name />`

- [ ] **Step 1: Add the i18n key**

In `apps/web/src/locales/en/workspace.json`:

```json
  "dragAttachmentToChat": "Drag onto the chat box to add this to the conversation",
```

Add it, translated, to `de`, `es` and `fr`.

- [ ] **Step 2: Write the failing test**

Create `apps/web/src/components/AttachmentDragHandle.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import AttachmentDragHandle from "./AttachmentDragHandle";
import { ATTACHMENT_DRAG_TYPE } from "../lib/dragTypes";

describe("AttachmentDragHandle", () => {
  function dragIt() {
    const setData = vi.fn();
    render(<AttachmentDragHandle scope="recording" ownerId="rec-1" attachmentId="att-1" name="Plan.pdf" />);
    const handle = screen.getByRole("button");
    fireEvent.dragStart(handle, { dataTransfer: { setData, effectAllowed: "" } });
    return setData;
  }

  it("puts the attachment payload on the drag", () => {
    const setData = dragIt();

    expect(setData).toHaveBeenCalledWith(
      ATTACHMENT_DRAG_TYPE,
      JSON.stringify({ scope: "recording", ownerId: "rec-1", attachmentId: "att-1", name: "Plan.pdf" }),
    );
  });

  /// The recordings panel reads a bare text/plain drag as a recording id being reordered, so setting it here
  /// would let an attachment drag land as a reorder. Only our own type goes on the drag.
  it("sets no other data type", () => {
    const setData = dragIt();

    expect(setData).toHaveBeenCalledTimes(1);
  });

  it("is marked draggable", () => {
    render(<AttachmentDragHandle scope="section" ownerId="sec-1" attachmentId="att-2" name="Brief.docx" />);

    expect(screen.getByRole("button").getAttribute("draggable")).toBe("true");
  });
});
```

- [ ] **Step 3: Run the test and verify it fails**

```bash
cd apps/web && npx vitest run src/components/AttachmentDragHandle.test.tsx
```

Expected: FAIL - cannot resolve `./AttachmentDragHandle`.

- [ ] **Step 4: Add the drag type**

Append to `apps/web/src/lib/dragTypes.ts`:

```ts
/// An existing attachment being dragged into the chat composer, from a recording's Attachments tab or from
/// either folder attachment list. Its own type so the composer cannot mistake a dragged word or link for one,
/// and - just as importantly - so the drag carries NOTHING else: the recordings panel reads a bare
/// `text/plain` payload as a recording id being reordered.
export const ATTACHMENT_DRAG_TYPE = "application/x-diariz-attachment";

/// `ownerId` is the recording id for `scope: "recording"` and the folder (section) id for `scope: "section"` -
/// which is also what decides the access rule the server applies.
export interface AttachmentDragPayload {
  scope: "recording" | "section";
  ownerId: string;
  attachmentId: string;
  name: string;
}
```

`dragHasFiles` needs no change: a `<span>` drag advertises no `Files` type (the exclusion it already carries exists because dragging an `<img>` thumbnail does).

- [ ] **Step 5: Write the component**

Create `apps/web/src/components/AttachmentDragHandle.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { ATTACHMENT_DRAG_TYPE, type AttachmentDragPayload } from "../lib/dragTypes";

/// The grip that makes one attachment row draggable into the chat composer.
///
/// A separate handle rather than a draggable row: a recording attachment's row holds a rename input, and a
/// draggable ancestor fights with selecting text inside it. One shared component rather than three copies so
/// the payload is constructed in exactly one place - a mismatch between the lists would be invisible until a
/// drop silently did nothing.
export default function AttachmentDragHandle(props: AttachmentDragPayload) {
  const { t } = useTranslation("workspace");
  return (
    <span
      role="button"
      tabIndex={-1}
      draggable
      title={t("dragAttachmentToChat")}
      aria-label={t("dragAttachmentToChat")}
      onDragStart={(e) => {
        e.dataTransfer.setData(ATTACHMENT_DRAG_TYPE, JSON.stringify(props));
        e.dataTransfer.effectAllowed = "copy";
      }}
      className="cursor-grab select-none px-1 text-gray-300 hover:text-gray-500 active:cursor-grabbing dark:text-gray-600 dark:hover:text-gray-400"
    >
      ⠿
    </span>
  );
}
```

`JSON.stringify(props)` relies on the props object having exactly the payload's four keys in declaration order, which is what the test asserts. If you add a fifth prop later, build the payload explicitly instead.

- [ ] **Step 6: Run the test and verify it passes**

```bash
cd apps/web && npx vitest run src/components/AttachmentDragHandle.test.tsx src/locales.test.ts
```

Expected: PASS.

- [ ] **Step 7: Add the handle to all three lists**

Import the component and place it as the first child of each row's **name** cell, before the existing content:

- `AttachmentsManager.tsx` - inside the `<td className="py-1 pr-2">` that holds the rename input, wrapping input and handle in a `flex items-center gap-1` div:
  `<AttachmentDragHandle scope="recording" ownerId={recordingId} attachmentId={a.id} name={a.name} />`
- `FolderAttachmentsList.tsx` - inside `<td className="truncate py-1 pr-2" title={a.name}>`; note the scope owner here is the **row's own** recording:
  `<AttachmentDragHandle scope="recording" ownerId={a.recordingId} attachmentId={a.id} name={a.name} />`
- `FolderAttachmentsManager.tsx` - inside its name cell:
  `<AttachmentDragHandle scope="section" ownerId={sectionId} attachmentId={a.id} name={a.name} />`

In `FolderAttachmentsList.tsx`, the name cell has `truncate` on it. `truncate` only ellipsises a block-level box, and adding a flex row inside needs `min-w-0` on the truncating child or the row will overflow and give the table a horizontal scrollbar. Use:

```tsx
<td className="py-1 pr-2" title={a.name}>
  <div className="flex min-w-0 items-center gap-1">
    <AttachmentDragHandle scope="recording" ownerId={a.recordingId} attachmentId={a.id} name={a.name} />
    <span className="min-w-0 truncate">{a.name}</span>
  </div>
</td>
```

- [ ] **Step 8: Run the affected suites**

```bash
cd apps/web && npx vitest run src/components/AttachmentsManager.test.tsx src/components/FolderAttachmentsList.test.tsx src/components/FolderAttachmentsManager.test.tsx
```

Expected: PASS. If a test asserts on a row's exact text content or child count, update it to account for the handle - but read what it was proving first, and keep proving it.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/dragTypes.ts apps/web/src/components/AttachmentDragHandle.tsx apps/web/src/components/AttachmentDragHandle.test.tsx apps/web/src/components/AttachmentsManager.tsx apps/web/src/components/FolderAttachmentsList.tsx apps/web/src/components/FolderAttachmentsManager.tsx apps/web/src/locales
git commit -m "feat(web): make attachment rows draggable into the chat composer"
```

---

## Task 5: Generalise the composer's accumulation rule

**Files:**
- Modify: `apps/web/src/components/ChatPanel.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/chat.json`
- Test: `apps/web/src/components/ChatPanel.test.tsx`

This task changes existing behaviour and is worth its own review gate: today only OCR text accumulates, and the paperclip replaces unconditionally.

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: the pill state's counter field is renamed `captures?: number` -> `parts?: number`. It is transient client state and is **not** part of the saved-conversation contract (`attachmentName` / `attachmentText` are all that persist), so nothing stored changes.

- [ ] **Step 1: Add the i18n keys**

In `apps/web/src/locales/en/chat.json`:

```json
  "attachedDocuments": "{{count}} documents",
  "replaceExtractedTextWithFile": "Replace the extracted text with \"{{name}}\"?",
```

Add both, translated, to `de`, `es` and `fr`. The existing `extractedTextFromCaptures` and `replaceAttachmentWithExtractedText` keys stay as they are.

- [ ] **Step 2: Write the failing tests**

In `apps/web/src/components/ChatPanel.test.tsx`, inside the existing `describe("screenshot attachments")` block or a new sibling `describe`, reuse the file's `drop()` helper by giving it the new type. Add:

```tsx
  describe("document attachments", () => {
    function dropDoc(payload: Record<string, unknown>) {
      const type = "application/x-diariz-attachment";
      fireEvent.drop(screen.getByTestId("chat-drop-zone"), {
        dataTransfer: { getData: (t: string) => (t === type ? JSON.stringify(payload) : ""), types: [type] },
      });
    }

    const docA = { scope: "recording", ownerId: "rec-1", attachmentId: "att-a", name: "Plan.pdf" };
    const docB = { scope: "recording", ownerId: "rec-1", attachmentId: "att-b", name: "Budget.xlsx" };

    it("adds a pill when an attachment is dropped on the composer", async () => {
      vi.mocked(api.chatAttachmentFromLibrary).mockResolvedValue({ name: "Plan.pdf", chars: 20, text: "The plan text." });
      await renderReady();

      act(() => dropDoc(docA));

      await waitFor(() => expect(screen.getByText("Plan.pdf")).toBeTruthy());
      expect(api.chatAttachmentFromLibrary).toHaveBeenCalledWith({
        scope: "recording", ownerId: "rec-1", attachmentId: "att-a", name: "Plan.pdf",
      });
    });

    it("accumulates a second attachment instead of replacing the first", async () => {
      vi.mocked(api.chatAttachmentFromLibrary)
        .mockResolvedValueOnce({ name: "Plan.pdf", chars: 14, text: "The plan text." })
        .mockResolvedValueOnce({ name: "Budget.xlsx", chars: 16, text: "The budget text." });
      await renderReady();

      act(() => dropDoc(docA));
      await waitFor(() => expect(screen.getByText("Plan.pdf")).toBeTruthy());
      act(() => dropDoc(docB));

      await waitFor(() => expect(screen.getByText("2 documents")).toBeTruthy());
    });

    it("opens the preview with both documents when the pill is clicked", async () => {
      vi.mocked(api.chatAttachmentFromLibrary)
        .mockResolvedValueOnce({ name: "Plan.pdf", chars: 14, text: "The plan text." })
        .mockResolvedValueOnce({ name: "Budget.xlsx", chars: 16, text: "The budget text." });
      await renderReady();

      act(() => dropDoc(docA));
      await waitFor(() => expect(screen.getByText("Plan.pdf")).toBeTruthy());
      act(() => dropDoc(docB));
      await waitFor(() => expect(screen.getByText("2 documents")).toBeTruthy());

      await userEvent.click(screen.getByText("2 documents"));

      const dialog = await screen.findByRole("dialog");
      expect(dialog.textContent).toContain("The plan text.");
      expect(dialog.textContent).toContain("The budget text.");
    });

    it("shows an error when the attachment cannot be read", async () => {
      vi.mocked(api.chatAttachmentFromLibrary).mockRejectedValue(new Error("nope"));
      await renderReady();

      act(() => dropDoc(docA));

      await waitFor(() => expect(screen.getByText(/could not read/i)).toBeTruthy());
    });

    /// A mixed pill would render half-wrong: the preview renders Markdown for OCR text and preformatted plain
    /// text for a document. The confirm is what keeps a pill single-origin, and it now works both ways.
    it("asks before a document replaces extracted text", async () => {
      vi.mocked(api.chatAttachmentFromLibrary).mockResolvedValue({ name: "Plan.pdf", chars: 14, text: "The plan text." });
      const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
      await renderReady();
      act(() => attachTextToChat({ name: "Extracted text", text: "From a capture" }));
      await waitFor(() => expect(screen.getByText(/extracted text/i)).toBeTruthy());

      act(() => dropDoc(docA));

      await waitFor(() => expect(confirm).toHaveBeenCalled());
      expect(screen.queryByText("Plan.pdf")).toBeNull();
      confirm.mockRestore();
    });
  });
```

Two setup notes:
- Add `chatAttachmentFromLibrary: vi.fn()` to the file's `vi.mock("../lib/api", ...)` factory. Some tests in this codebase deliberately guard a call by **omitting** a method from a mock factory, so before adding anything else there, check nothing was relying on its absence. Adding a brand-new method cannot break such a guard.
- `attachTextToChat` is the module channel from `../lib/chatAttachments`; import it if the file does not already.
- `userEvent` is installed; import from `@testing-library/user-event`.

- [ ] **Step 3: Run the tests and verify they fail**

```bash
cd apps/web && npx vitest run src/components/ChatPanel.test.tsx -t "document attachments"
```

Expected: FAIL - `api.chatAttachmentFromLibrary` is not a function / the pill never appears.

- [ ] **Step 4: Add the api method**

In `apps/web/src/lib/api.ts`, beside `uploadChatAttachment`:

```ts
  /// Read an attachment the user already has (on a recording, or filed against a folder) into chat context.
  /// Unlike the bulk "include attachments" toggle, this reports failures - the user is waiting on this one.
  async chatAttachmentFromLibrary(payload: AttachmentDragPayload): Promise<ChatAttachment> {
    const { data } = await http.post<ChatAttachment>("/api/chat/attachment/library", {
      attachmentId: payload.attachmentId,
      recordingId: payload.scope === "recording" ? payload.ownerId : null,
      sectionId: payload.scope === "section" ? payload.ownerId : null,
    });
    return data;
  },
```

Import `AttachmentDragPayload` from `./dragTypes`.

- [ ] **Step 5: Generalise the pill state**

In `ChatPanel.tsx`, rename the counter in the `attachment` state type:

```tsx
  const [attachment, setAttachment] = useState<
    { name: string; text: string; chars: number; origin?: "file" | "ocr"; parts?: number } | null
  >(null);
```

Add a single merge helper next to `addShot`, and route **both** the OCR channel and the new drop through it:

```tsx
  /// Merge new text into the single context pill.
  ///
  /// Accumulation used to be an OCR-only rule: extracted text appended, and a picked or dropped file replaced
  /// whatever was there. That made two ways of adding the same kind of thing behave differently, so both now
  /// append. The pill stays SINGLE-ORIGIN on purpose - the preview renders Markdown for OCR text and plain
  /// preformatted text for a document, so a mixed pill would render half of itself wrong. Crossing origins
  /// therefore asks first, in both directions.
  function mergeAttachment(name: string, text: string, origin: "file" | "ocr") {
    setAttachment((current) => {
      if (current && (current.origin ?? "file") !== origin) {
        const question = origin === "ocr"
          ? t("replaceAttachmentWithExtractedText", { name: current.name })
          : t("replaceExtractedTextWithFile", { name });
        if (!window.confirm(question)) return current;
        return { name, text, chars: text.length, origin, parts: 1 };
      }
      if (!current) return { name, text, chars: text.length, origin, parts: 1 };

      // Each block keeps its own heading so the model can tell which document a line came from, and the
      // label counts them rather than naming only the newest.
      const parts = (current.parts ?? 1) + 1;
      const merged = `${current.text}\n\n---\n\n## ${name}\n\n${text}`;
      return {
        name: origin === "ocr"
          ? t("extractedTextFromCaptures", { count: parts })
          : t("attachedDocuments", { count: parts }),
        text: merged,
        chars: merged.length,
        origin,
        parts,
      };
    });
  }
```

Replace the body of the `onChatTextAttached` effect with `mergeAttachment(name, text, "ocr")`, and change `onPickFile`'s success handler from `setAttachment({...})` to `mergeAttachment(r.name, r.text, "file")`.

The paperclip now accumulates too. That is the intended consequence; re-picking a wrong file needs one click on the pill's remove control first. Note it in the release summary.

- [ ] **Step 6: Branch the drop**

Rename `onDropShot` to `onDropOnComposer` and give it both branches:

```tsx
  /// Accept either kind of payload dropped on the composer. Each has its own MIME type, so a dragged word or
  /// link cannot be mistaken for one.
  function onDropOnComposer(e: React.DragEvent) {
    setDropActive(false);

    const shotRaw = e.dataTransfer.getData(SCREENSHOT_DRAG_TYPE);
    if (shotRaw) {
      e.preventDefault();
      try {
        const parsed = JSON.parse(shotRaw) as ChatScreenshotRef;
        if (!parsed?.recordingId || !parsed?.screenshotId) return;
        addShot({ recordingId: parsed.recordingId, screenshotId: parsed.screenshotId });
      } catch {
        return; // a malformed payload cannot have come from our own strip; not worth an error message
      }
      return;
    }

    const docRaw = e.dataTransfer.getData(ATTACHMENT_DRAG_TYPE);
    if (!docRaw) return;
    e.preventDefault();
    let payload: AttachmentDragPayload;
    try {
      payload = JSON.parse(docRaw) as AttachmentDragPayload;
    } catch {
      return;
    }
    if (!payload?.attachmentId || !payload?.ownerId) return;

    setError(null);
    setUploading(true);
    api
      .chatAttachmentFromLibrary(payload)
      .then((r) => mergeAttachment(r.name, r.text, "file"))
      .catch((err: unknown) => setError(apiErrorMessage(err, t("couldNotReadFile"))))
      .finally(() => setUploading(false));
  }
```

Update the drop zone to accept both types and to call the renamed handler:

```tsx
        onDragOver={(e) => {
          const types = e.dataTransfer.types;
          if (!types.includes(SCREENSHOT_DRAG_TYPE) && !types.includes(ATTACHMENT_DRAG_TYPE)) return;
          e.preventDefault(); // without this the browser refuses the drop entirely
          e.dataTransfer.dropEffect = "copy";
          setDropActive(true);
        }}
        onDragLeave={() => setDropActive(false)}
        onDrop={onDropOnComposer}
```

Import `ATTACHMENT_DRAG_TYPE` and `AttachmentDragPayload` from `../lib/dragTypes`.

- [ ] **Step 7: Run the tests and verify they pass**

```bash
cd apps/web && npx vitest run src/components/ChatPanel.test.tsx src/locales.test.ts
```

Expected: all pass, including the pre-existing screenshot-drop tests and the OCR accumulation tests - the rename must not have changed either.

- [ ] **Step 8: Typecheck and run the whole web suite**

```bash
cd apps/web && npm run build && npm test
```

Expected: green, no warnings.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/api.ts apps/web/src/components/ChatPanel.tsx apps/web/src/components/ChatPanel.test.tsx apps/web/src/locales
git commit -m "feat(web): drop an attachment on the chat composer to add it to context"
```

---

## Task 6: Verify it live

**Files:** none.

jsdom computes no geometry and has no real drag-and-drop, so the gesture itself has never actually been exercised by the tests above. This step is not optional.

- [ ] **Step 1: Confirm which stack you are pointing at**

The local `diariz` compose stack **is production** on this machine.

```bash
docker exec diariz-api-1 printenv App__PublicUrl
```

Prefer the dev stack.

- [ ] **Step 2: Rebuild**

```bash
cd deploy && docker compose up -d --build api web
```

Stale builds are the most common cause of "the change did not work" here. If live and test disagree, believe the test and force a fresh build.

- [ ] **Step 3: Drag a real attachment**

Open a recording with an attachment, open the chat panel, and drag the grip onto the composer. Confirm: the drop zone highlights while dragging, a pill appears named after the attachment, and clicking it opens the preview with the document's text.

- [ ] **Step 4: Check the three sources and both kinds**

Repeat from the folder attachments list and the folder's own attachments, and once with a **URL** attachment. Confirm the URL one fetches its page text.

- [ ] **Step 5: Check what the drag does NOT do**

Drag an attachment grip over the recordings panel and drop it there. Nothing should move or reorder - the payload carries only `application/x-diariz-attachment`, never `text/plain`. This is the regression the drag-handle unit test guards, verified for real.

- [ ] **Step 6: Check the layout**

The grip must not push the name column into overflow. In the browser console, on the folder attachments page:

```js
const t = document.querySelector("table"); [t.scrollWidth, t.clientWidth]
```

Expected: `scrollWidth <= clientWidth`. A class-presence assertion in jsdom cannot tell you this.

---

## Task 7: Release paperwork

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/web/package-lock.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: the chat help article under `apps/web/src/content/help/en/`

- [ ] **Step 1: Bump the version everywhere**

Read `version.json`, apply Minor +1 / Build 0, write it into all five mirrors. `apps/web/package-lock.json` holds it in **two** places. `versionMirrors.test.ts` fails the build on drift.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version`, `date`, `pr`, `headline`, a prose `summary`, and `added` / `changed` bullets. The `changed` list **must** mention that attaching a file now adds to what is already attached rather than replacing it - that is a behaviour change to the paperclip, not only to the new drop.

`releases.test.ts` asserts `RELEASES[0].version === version.json`. Plain hyphens only. Work out the real `pr` number rather than guessing "last + 1" - issues and Dependabot PRs share the sequence.

- [ ] **Step 3: Update the inventories**

- README **Features** table row.
- `docs/features.md` prose bullet - always both, never one.
- `CAPABILITIES` table row in `releases.ts`.

- [ ] **Step 4: Update the help article**

The chat help article needs a line: attachments can be dragged onto the chat box, and attaching adds to what is already there rather than replacing it. ASCII only, plain hyphens, and keep the `summary` front-matter to two or three sentences. Then:

```bash
cd apps/web && npx vitest run src/content/help/helpContent.test.ts
```

- [ ] **Step 5: Update the synopsis**

`docs/Overall_Synopsis_of_Platform.md`: the new `api/chat/attachment/library` endpoint and the shared `IAttachmentTextResolver`. No schema change, so `docs/Data_Schema.md` is untouched.

- [ ] **Step 6: Run everything**

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
cd apps/web && npm run build && npm test
```

Expected: green throughout, no warnings.

- [ ] **Step 7: Commit, push and open the PR**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help
git commit -m "chore: release <version>"
git push -u origin <branch>
gh pr create --title "..." --body "..."
```

The PR body must state the deployment surface: **server redeploy only, no desktop release**. No issue to close.
