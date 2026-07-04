using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private readonly DiarizDbContext _db;
    private readonly IChatStreamClient _chat;
    private readonly ISummarizationSettingsResolver _settings;
    private readonly IChatContextResolver _contextResolver;
    private readonly IAttachmentExtractor _extractor;
    private readonly IAudioStorage _storage;
    private readonly IUrlFetcher _urlFetcher;
    private readonly IChatToolSettingsResolver _toolSettings;
    private readonly IChatToolOrchestrator _orchestrator;

    public ChatController(
        DiarizDbContext db, IChatStreamClient chat, ISummarizationSettingsResolver settings,
        IChatContextResolver contextResolver, IAttachmentExtractor extractor,
        IAudioStorage storage, IUrlFetcher urlFetcher,
        IChatToolSettingsResolver toolSettings, IChatToolOrchestrator orchestrator)
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
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ---- Streaming chat (SSE) ----

    [HttpPost("stream")]
    public async Task<IActionResult> Stream(ChatStreamRequest req, CancellationToken ct)
    {
        var cfg = await _settings.ResolveAsync(UserId, ct);
        if (!cfg.Enabled)
            return BadRequest("Chat is not configured. Set an LLM endpoint in Settings.");

        var (contexts, allOwned) = await LoadTranscriptsAsync(req.RecordingIds ?? [], ct);
        if (!allOwned) return NotFound(); // a selected recording isn't visible to the caller

        // Optionally pull the selected recordings' attachments into the context (extracted file text +
        // fetched URL text). Recordings were already ownership-checked by LoadTranscriptsAsync above.
        var documents = req.IncludeAttachments
            ? await LoadAttachmentDocumentsAsync(req.RecordingIds ?? [], ct)
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
        if (toolCfg.ActiveTools.Count > 0) system += "\n\n" + ToolSystemInstruction;
        var history = (req.Messages ?? []).Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        var messages = ChatContextBuilder.BuildMessages(system, history);
        var contextTotal = await _contextResolver.ResolveContextWindowAsync(UserId, ct);
        var promptTokens = messages.Sum(m => ChatContextMeter.EstimateTokens(m.Content));
        var toolContext = new ChatToolContext(UserId, req.RecordingIds ?? []);

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

    // ---- Saved conversations (per-user CRUD) ----

    [HttpGet("conversations")]
    public async Task<IReadOnlyList<ChatConversationSummaryDto>> ListConversations() =>
        await _db.ChatSessions
            .Where(c => c.UserId == UserId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ChatConversationSummaryDto(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync();

    [HttpGet("conversations/{id:guid}")]
    public async Task<ActionResult<ChatConversationDto>> GetConversation(Guid id)
    {
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (c is null) return NotFound();
        return ToDto(c);
    }

    [HttpPost("conversations")]
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
    public async Task<ActionResult<SaveChatConversationResult>> UpdateConversation(
        Guid id, SaveChatConversationRequest req, CancellationToken ct)
    {
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
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
    public async Task<IActionResult> DeleteConversation(Guid id)
    {
        var c = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
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
