using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>
/// Multi-turn chat over the user's transcripts. Stateless per turn: each request carries the full
/// message history and the selected context (recording ids + optional attachment text). The LLM
/// endpoint/model/key are the per-user summarisation config (server <c>.env</c> fallback); only the
/// context-window size is chat-specific. Conversations can be saved/loaded/deleted.
/// </summary>
[ApiController]
[Authorize]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SavedChatContextDto EmptyContext = new([], null, null);

    /// <summary>Appended to the system prompt when tools are active: steers the model to use the built-in
    /// tools to look beyond the supplied context and to report findings in the standard format.</summary>
    private const string ToolSystemInstruction =
        "You have tools that search the user's full transcript library, which usually contains far more than " +
        "the context above. Search finds passages by meaning as well as exact words, so it can surface a " +
        "relevant moment even when the user's wording doesn't appear verbatim. When the user scopes their " +
        "question (a date or period, a person, or a folder/section), narrow the search accordingly - resolve " +
        "relative dates against today's date given above. Default to using the tools: whenever the user asks " +
        "about a person, company, customer, project, or topic — including open-ended questions like \"what do " +
        "we know about X\" — call search_transcripts (or a more specific tool) to look across their transcripts " +
        "BEFORE answering. " +
        "Never reply that you don't know about something until you have searched and found nothing. Also use a " +
        "tool for questions about who said something, action items, summaries, attendees, or talk time. " +
        "When you report transcript findings, use the format: When (date/time) · Who (speaker) · What (what was " +
        "said). Each tool result includes a markdown 'Link:' to the recording (and the exact moment); when you " +
        "reference a recording or a specific moment, include that markdown link inline so the user can open the " +
        "transcript there. Keep the links exactly as given.";

    /// <summary>Appended in "All meetings" mode: no transcript is pre-loaded, so the model must answer by
    /// searching the whole library rather than assuming a specific meeting is in context.</summary>
    private const string AllMeetingsInstruction =
        "The user has NOT opened or pinned any specific meeting to this conversation - they are asking across " +
        "their ENTIRE library. Do not assume a particular recording is in context: use the search tools to find " +
        "the relevant meetings and moments before answering, and cite the recordings you draw from.";

    private readonly DiarizDbContext _db;
    private readonly IChatStreamClient _chat;
    private readonly ISummarizationSettingsResolver _settings;
    private readonly IChatContextResolver _contextResolver;
    private readonly IAttachmentExtractor _extractor;
    private readonly IAudioStorage _storage;
    private readonly IUrlFetcher _urlFetcher;
    private readonly IChatToolSettingsResolver _toolSettings;
    private readonly IChatToolOrchestrator _orchestrator;
    private readonly IRoomScope _rooms;
    private readonly IDictationClient _dictation;
    private readonly DictationOptions _dictationOptions;

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

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ---- Streaming chat (SSE) ----

    [HttpPost("stream")]
    [EndpointSummary("Ask a question and stream the answer")]
    [EndpointDescription(
        "The chat endpoint. The response is **streamed**, not a single JSON body, so read it incrementally " +
        "rather than waiting for completion; the final chunk carries a context-usage snapshot.\n\n" +
        "**Stateless per turn.** Nothing is remembered between calls: send the full message history every " +
        "time, along with the context you want in scope. `recordingIds` pre-loads those transcripts; " +
        "`sectionId` additionally prepends a folder's roll-up and widens the scope to every recording beneath " +
        "it; sending neither means library-wide mode, where the model answers by searching rather than from " +
        "pre-loaded text. `includeAttachments` pulls the in-scope recordings' attachments into the context - " +
        "files are extracted and URLs fetched, and anything unsupported or unreachable is skipped rather than " +
        "failing the request.\n\n" +
        "Context is trimmed to fit a character budget, so a very large selection is truncated rather than " +
        "rejected. When you have chat tools enabled the model can also look beyond the supplied context. " +
        "Returns 404 if any selected recording or folder is not visible to you, and 400 when no LLM endpoint " +
        "is configured for you or the platform.")]
    public async Task<IActionResult> Stream(ChatStreamRequest req, CancellationToken ct)
    {
        var cfg = await _settings.ResolveAsync(UserId, ct);
        if (!cfg.Enabled)
            return BadRequest("Chat is not configured. Set an LLM endpoint in Settings.");

        var (contexts, allOwned) = await LoadTranscriptsAsync(req.RecordingIds ?? [], ct);
        if (!allOwned) return NotFound(); // a selected recording isn't visible to the caller

        // Folder chat: prepend the folder's roll-up (summary + minutes + aggregated actions) and scope the
        // attachments/tools to the folder's recordings (across it and its sub-folders).
        var scopeRecIds = (req.RecordingIds ?? []).ToList();
        if (req.SectionId is { } sectionId)
        {
            var folder = await LoadFolderContextAsync(sectionId, ct);
            if (folder is null) return NotFound(); // folder not visible to the caller
            contexts.Insert(0, folder.Value.Context);
            scopeRecIds.AddRange(folder.Value.RecordingIds);
        }

        // Optionally pull the in-context recordings' attachments into the context (extracted file text +
        // fetched URL text). For a folder that's every attachment across it and its sub-folders.
        var documents = req.IncludeAttachments
            ? await LoadAttachmentDocumentsAsync(scopeRecIds, ct)
            : [];

        // Resolve the user's enabled tools; when any are active, add a tool-usage instruction so the model
        // prefers the built-in tools and answers in the standard When / Who / What format.
        var toolCfg = await _toolSettings.ResolveAsync(UserId, ct);
        // Identify the current user in the system prompt (name the model can sign emails as, and the address
        // the send_email tool always delivers to).
        var me = await _db.Users.Where(u => u.Id == UserId)
            .Select(u => new { u.FullName, u.Email }).FirstOrDefaultAsync(ct);
        var system = ChatContextBuilder.BuildSystemPrompt(
            contexts, req.AttachmentName, req.AttachmentText, documents,
            ChatContextBuilder.DefaultCharBudget, me?.FullName, me?.Email, DateTimeOffset.UtcNow);
        if (toolCfg.ActiveTools.Count > 0)
        {
            system += "\n\n" + ToolSystemInstruction;
            if (req.SearchAllMeetings) system += "\n\n" + AllMeetingsInstruction;
        }
        var history = (req.Messages ?? []).Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        var messages = ChatContextBuilder.BuildMessages(system, history);
        var contextTotal = await _contextResolver.ResolveContextWindowAsync(UserId, ct);
        var promptTokens = messages.Sum(m => ChatContextMeter.EstimateTokens(m.Content));
        var toolContext = new ChatToolContext(UserId, scopeRecIds);

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // tell nginx not to buffer the stream
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await WriteEventAsync(new { type = "meta", model = cfg.Model, contextUsed = promptTokens, contextTotal }, ct);

        long completionChars = 0;
        try
        {
            await foreach (var evt in _orchestrator.RunAsync(cfg, messages, toolCfg.ActiveTools, toolContext, ct))
            {
                switch (evt)
                {
                    case ChatTokenEvent t:
                        completionChars += t.Value.Length;
                        await WriteEventAsync(new { type = "token", value = t.Value }, ct);
                        break;
                    case ChatToolStartEvent s:
                        await WriteEventAsync(new { type = "tool_start", name = s.Name }, ct);
                        break;
                    case ChatToolEndEvent e:
                        await WriteEventAsync(new { type = "tool_end", name = e.Name }, ct);
                        break;
                    case ChatRefEvent r:
                        await WriteEventAsync(new { type = "ref", name = r.Name, href = r.Href }, ct);
                        break;
                    case ChatAttachmentDraftEvent a:
                        await WriteEventAsync(new
                        {
                            type = "attachment",
                            name = a.Name,
                            content = a.Content,
                            recordings = a.Recordings.Select(x => new { id = x.Id, title = x.Title }),
                        }, ct);
                        break;
                }
            }
            var used = promptTokens + ChatContextMeter.EstimateFromChars(completionChars);
            await WriteEventAsync(new { type = "done", model = cfg.Model, contextUsed = used, contextTotal }, ct);
        }
        catch (OperationCanceledException)
        {
            // Client aborted (Stop button / navigation) — stop quietly.
        }
        catch (ChatStreamException ex)
        {
            await WriteEventAsync(new { type = "error", message = ex.Message }, ct);
        }
        return new EmptyResult();
    }

    private async Task WriteEventAsync(object payload, CancellationToken ct)
    {
        // JSON-encoding the payload escapes any newlines, so each event is a single safe SSE frame.
        var json = JsonSerializer.Serialize(payload, Json);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    // ---- Attachment extraction (PDF / text) ----

    [HttpPost("attachment")]
    [EndpointSummary("Extract text from a file for chat")]
    [EndpointDescription(
        "Uploads a file and returns its **extracted text**, plus a character count, for you to pass back as " +
        "context on a chat turn. Nothing is stored: this is a one-shot conversion, not an attachment on a " +
        "recording (use the recording or folder attachment endpoints for that).\n\n" +
        "PDF and text-based formats only (`.pdf`, `.txt`, `.md`, and similar) - anything else, or a file with " +
        "no extractable text, returns 400.")]
    [RequestSizeLimit(50L * 1024 * 1024)] // 50 MiB
    public async Task<ActionResult<ChatAttachmentDto>> Attachment([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (!_extractor.IsSupported(file.FileName, file.ContentType))
            return BadRequest("Only PDF and text-based files (.pdf, .txt, .md, …) are supported.");

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.OpenReadStream().CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        try
        {
            var r = _extractor.Extract(file.FileName, file.ContentType, bytes);
            if (string.IsNullOrWhiteSpace(r.Text))
                return BadRequest("No text could be extracted from the file.");
            return new ChatAttachmentDto(r.Name, r.Chars, r.Text);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ---- Voice dictation (server fallback path) ----

    /// <summary>Transcribe one short audio utterance for chat voice dictation. Server-level STT config
    /// (<see cref="DictationOptions"/>); returns 400 when no STT endpoint is configured. Persists nothing.</summary>
    [HttpPost("transcribe")]
    [EndpointSummary("Transcribe a spoken chat question")]
    [EndpointDescription(
        "Converts one short audio utterance to text for voice dictation into the chat box. Nothing is stored " +
        "- no recording is created and the audio is discarded once transcribed.\n\n" +
        "This is the **server fallback** for browsers without built-in speech recognition, and uses the " +
        "platform's speech-to-text endpoint rather than your own AI settings; 400 when the platform has none " +
        "configured. Intended for single utterances, not meetings - upload those as recordings instead.")]
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
        try
        {
            var text = await _dictation.TranscribeAsync(config, stream, file.ContentType, file.FileName, ct);
            return Ok(new ChatTranscriptionDto(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine client abort - let the framework handle it
        }
        catch (Exception)
        {
            // A downstream STT failure (service down, bad model, timeout, malformed response) - report a
            // clean 502 so the dictation UI can show an actionable message instead of a bare 500.
            return StatusCode(StatusCodes.Status502BadGateway, "The transcription service could not be reached.");
        }
    }

    // ---- Saved conversations (per-user CRUD) ----

    [HttpGet("conversations")]
    [EndpointSummary("List saved conversations")]
    [EndpointDescription(
        "Your saved chats, most recently updated first, as id, title, and timestamp. **Titles only** - load " +
        "one by id for its messages. Conversations are private to you and never shared into a room.")]
    public async Task<IReadOnlyList<ChatConversationSummaryDto>> ListConversations()
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await _db.ChatSessions
            .Where(c => c.RoomId == roomId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ChatConversationSummaryDto(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync();
    }

    [HttpGet("conversations/{id:guid}")]
    [EndpointSummary("Load a saved conversation")]
    [EndpointDescription(
        "The full message history plus the context selection it was saved with - the recordings or folder in " +
        "scope - so you can resume it exactly where it left off. Because chat itself is stateless, resuming " +
        "means sending these messages back on the next stream call.")]
    public async Task<ActionResult<ChatConversationDto>> GetConversation(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.RoomId == roomId);
        if (c is null) return NotFound();
        return ToDto(c);
    }

    [HttpPost("conversations")]
    [EndpointSummary("Save a conversation")]
    [EndpointDescription(
        "Stores a chat's messages and its context selection, returning the new id and title. The **title is " +
        "generated for you** - a short LLM-written phrase, falling back to the first user message when chat " +
        "is not configured or generation fails - so there is no title parameter. Saving an empty conversation " +
        "is rejected with 400.")]
    public async Task<ActionResult<SaveChatConversationResult>> CreateConversation(
        SaveChatConversationRequest req, CancellationToken ct)
    {
        var messages = req.Messages ?? [];
        if (messages.Count == 0) return BadRequest("Cannot save an empty conversation.");

        var title = await GenerateTitleAsync(messages, ct);
        var now = DateTimeOffset.UtcNow;
        var c = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            RoomId = await _rooms.PersonalRoomIdAsync(UserId, ct), // populated now; queries flip to it in Phase 4
            Title = title,
            MessagesJson = JsonSerializer.Serialize(messages, Json),
            ContextJson = JsonSerializer.Serialize(req.Context ?? EmptyContext, Json),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ChatSessions.Add(c);
        await _db.SaveChangesAsync(ct);
        return new SaveChatConversationResult(c.Id, title);
    }

    [HttpPut("conversations/{id:guid}")]
    [EndpointSummary("Update a saved conversation")]
    [EndpointDescription(
        "Replaces a saved conversation's messages and context - what you call after adding turns to one you " +
        "loaded. The whole history is replaced, not appended to, so send the complete message list.\n\n" +
        "The **title is regenerated** from the new messages, so it may change as the conversation moves on. " +
        "Saving an empty conversation is rejected with 400.")]
    public async Task<ActionResult<SaveChatConversationResult>> UpdateConversation(
        Guid id, SaveChatConversationRequest req, CancellationToken ct)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId, ct);
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.RoomId == roomId, ct);
        if (c is null) return NotFound();

        var messages = req.Messages ?? [];
        if (messages.Count == 0) return BadRequest("Cannot save an empty conversation.");

        c.Title = await GenerateTitleAsync(messages, ct);
        c.MessagesJson = JsonSerializer.Serialize(messages, Json);
        c.ContextJson = JsonSerializer.Serialize(req.Context ?? EmptyContext, Json);
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new SaveChatConversationResult(c.Id, c.Title);
    }

    [HttpDelete("conversations/{id:guid}")]
    [EndpointSummary("Delete a saved conversation")]
    [EndpointDescription(
        "Removes the conversation permanently. Only the chat goes - the recordings it discussed, and any " +
        "attachment you saved from it, are untouched.")]
    public async Task<IActionResult> DeleteConversation(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.RoomId == roomId);
        if (c is null) return NotFound();
        _db.ChatSessions.Remove(c);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---- helpers ----

    private static ChatConversationDto ToDto(ChatSession c)
    {
        var messages = JsonSerializer.Deserialize<List<ChatTurnDto>>(c.MessagesJson, Json) ?? [];
        var context = JsonSerializer.Deserialize<SavedChatContextDto>(c.ContextJson, Json) ?? EmptyContext;
        return new ChatConversationDto(c.Id, c.Title, messages, context, c.UpdatedAt);
    }

    /// <summary>Loads owned recordings' transcripts as context. The second item is false when any
    /// requested id isn't visible to the caller (so the endpoint can 404 before streaming).</summary>
    private async Task<(List<TranscriptContext> Contexts, bool AllOwned)> LoadTranscriptsAsync(
        IReadOnlyList<Guid> recordingIds, CancellationToken ct)
    {
        var ids = recordingIds.Distinct().ToList();
        if (ids.Count == 0) return ([], true);

        var recs = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Where(r => ids.Contains(r.Id) && r.UserId == UserId)
            .ToListAsync(ct);

        var contexts = new List<TranscriptContext>();
        foreach (var rec in recs)
        {
            var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
            var current = rec.Transcriptions.FirstOrDefault();
            var segs = current?.Segments
                .OrderBy(s => s.Ordinal)
                .Select(s => new SegmentDto(
                    s.Id, s.SpeakerLabel,
                    names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                    s.StartMs, s.EndMs, s.Original, s.Revised))
                .ToList() ?? [];
            var actions = rec.Actions
                .OrderBy(a => a.Ordinal)
                .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
                .ToList();

            // Include any extracted actions alongside the transcript so the model can answer about them.
            var text = TranscriptFormatter.ToPlainText(segs);
            var actionsText = TranscriptFormatter.ActionsForChat(actions);
            if (actionsText.Length > 0) text = text + "\n" + actionsText;

            contexts.Add(new TranscriptContext(rec.Name ?? rec.Title, text));
        }
        return (contexts, recs.Count == ids.Count);
    }

    /// <summary>Build a folder's chat context from its roll-ups (summary + minutes + aggregated actions
    /// across the section and its child sections) and return the folder's recording ids (for attachments +
    /// tool scope). Null when the folder isn't the caller's.</summary>
    private async Task<(TranscriptContext Context, List<Guid> RecordingIds)?> LoadFolderContextAsync(
        Guid sectionId, CancellationToken ct)
    {
        // Folder membership + ownership now come from the caller's personal room, not Section.UserId /
        // Recording.SectionId.
        var roomId = await _rooms.PersonalRoomIdAsync(UserId, ct);
        var section = await _db.Sections
            .Include(s => s.Summary)
            .Include(s => s.Minutes)
            .FirstOrDefaultAsync(s => s.Id == sectionId && s.RoomId == roomId, ct);
        if (section is null) return null;

        var childIds = await _db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId).Select(s => s.Id).ToListAsync(ct);
        var allIds = childIds.Append(sectionId).ToList();

        var recIds = await _db.RoomRecordings
            .Where(p => p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value))
            .Select(p => p.RecordingId).ToListAsync(ct);

        var actions = await (
            from a in _db.RecordingActions
            join r in _db.Recordings on a.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt, a.Ordinal
            select new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal)).ToListAsync(ct);

        var text = ChatFolderContext.BuildText(
            section.Summary?.Text, section.Minutes?.Text, TranscriptFormatter.ActionsForChat(actions));
        return (new TranscriptContext($"Folder: {section.Name}", text), recIds);
    }

    /// <summary>Resolve the selected recordings' attachments into text documents for chat context: uploaded
    /// files are downloaded and extracted (supported types only); URL attachments are fetched (behind SSRF
    /// guards). An attachment that fails or is unsupported is skipped.</summary>
    private async Task<List<TranscriptContext>> LoadAttachmentDocumentsAsync(
        IReadOnlyList<Guid> recordingIds, CancellationToken ct)
    {
        var ids = recordingIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var attachments = await _db.Attachments
            .Where(a => ids.Contains(a.RecordingId) && a.Recording!.UserId == UserId)
            .OrderBy(a => a.RecordingId).ThenBy(a => a.Ordinal)
            .ToListAsync(ct);

        var docs = new List<TranscriptContext>();
        foreach (var a in attachments)
        {
            try
            {
                if (a.Kind == AttachmentKind.Url && a.Url is not null)
                {
                    var text = await _urlFetcher.FetchTextAsync(a.Url, ct);
                    if (!string.IsNullOrWhiteSpace(text)) docs.Add(new TranscriptContext(a.Name, text!));
                }
                else if (a.Kind == AttachmentKind.File && a.BlobKey is not null
                         && _extractor.IsSupported(a.Name, a.ContentType))
                {
                    await using var stream = await _storage.OpenReadAsync(a.BlobKey, ct);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);
                    var extracted = _extractor.Extract(a.Name, a.ContentType, buffer.ToArray());
                    if (!string.IsNullOrWhiteSpace(extracted.Text))
                        docs.Add(new TranscriptContext(extracted.Name, extracted.Text));
                }
            }
            catch
            {
                // Skip an attachment that can't be fetched/extracted — never fail the whole chat turn.
            }
        }
        return docs;
    }

    /// <summary>A short LLM-generated title (3–6 words); falls back to the first user message when
    /// chat isn't configured or generation fails.</summary>
    private async Task<string> GenerateTitleAsync(IReadOnlyList<ChatTurnDto> messages, CancellationToken ct)
    {
        var firstUser = messages.FirstOrDefault(m => RoleIs(m.Role, "user"))?.Content?.Trim() ?? "";
        var firstAssistant = messages.FirstOrDefault(m => RoleIs(m.Role, "assistant"))?.Content?.Trim() ?? "";
        var fallback = Truncate(firstUser.Length > 0 ? firstUser : "Saved conversation", 60);

        var excerpt = Truncate((firstUser + "\n\n" + firstAssistant).Trim(), 1500);
        if (excerpt.Length == 0) return fallback;

        var cfg = await _settings.ResolveAsync(UserId, ct);
        if (!cfg.Enabled) return fallback;

        try
        {
            var prompt = new[]
            {
                new ChatMessage("system",
                    "Generate a concise 3-6 word title for this conversation. " +
                    "Return only the title, with no quotes or punctuation."),
                new ChatMessage("user", excerpt),
            };
            var sb = new StringBuilder();
            await foreach (var t in _chat.StreamAsync(cfg, prompt, ct))
                sb.Append(t);

            var title = StripModelTokens(sb.ToString()).Trim().Trim('"');
            return string.IsNullOrWhiteSpace(title) ? fallback : Truncate(title, 120);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool RoleIs(string? role, string expected) =>
        string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);

    private static string StripModelTokens(string s) => Regex.Replace(s, @"<\|[^|]*\|>", " ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
