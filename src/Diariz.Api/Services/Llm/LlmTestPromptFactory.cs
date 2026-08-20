using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services.Llm;

/// <summary>One test call's prompt, and the shape its reply parses into.</summary>
public record LlmTestPrompt(IReadOnlyList<ChatMessage> Messages, string ParsedKind);

public interface ILlmTestPromptFactory
{
    /// <summary>Whether this call group's test runs against a real recording rather than the built-in
    /// sample.</summary>
    bool NeedsRecording(LlmCallGroup group);

    /// <summary>The messages the real pipeline would send for this group and recording, or null when the
    /// recording is missing, is not <paramref name="ownerId"/>'s, or has no transcript. Returning null
    /// rather than throwing keeps the three refusals distinguishable at the controller, which owns the
    /// status codes.</summary>
    Task<LlmTestPrompt?> BuildAsync(
        LlmCallGroup group, Guid recordingId, Guid ownerId, int charBudget, CancellationToken ct = default);
}

/// <summary>Builds the REAL prompt for a real recording, so the model editor's test answers "is this model
/// any good at this job" and not merely "does this endpoint answer".
///
/// <para>Every group here delegates to the same pure builder and the same editable template file the
/// production path uses. That is the whole point: a prompt reproduced by hand would drift, and the test
/// would then be measuring a call the platform never makes.</para></summary>
public sealed class LlmTestPromptFactory : ILlmTestPromptFactory
{
    /// <summary>The response cap for a recording-backed test. Twice the built-in sample's, because the
    /// Actions prompt asks the model to reason in PROSE before emitting its JSON array - on a long meeting
    /// that legitimately runs past 8,000 characters, and truncating it would fake an empty extraction the
    /// real pipeline would never see.</summary>
    public const int RecordingMaxResponseChars = 16000;

    private readonly DiarizDbContext _db;
    private readonly IPromptTemplateProvider _templates;

    public LlmTestPromptFactory(DiarizDbContext db, IPromptTemplateProvider templates)
    {
        _db = db;
        _templates = templates;
    }

    public bool NeedsRecording(LlmCallGroup group) =>
        group is LlmCallGroup.Tags or LlmCallGroup.Actions or LlmCallGroup.Summaries;

    public async Task<LlmTestPrompt?> BuildAsync(
        LlmCallGroup group, Guid recordingId, Guid ownerId, int charBudget, CancellationToken ct = default)
    {
        if (!NeedsRecording(group)) return null;

        // Ownership is part of the query, not a check afterwards: this endpoint is ManagePlatform-gated, so
        // the caller can name any recording id in the database.
        var rec = await _db.Recordings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == recordingId && r.UserId == ownerId, ct);
        if (rec is null) return null;

        // The highest version, the same "current transcription" rule the detail endpoint applies.
        var transcriptionId = await _db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (transcriptionId is null) return null;

        var segmentRows = await _db.Segments
            .AsNoTracking()
            .Where(s => s.TranscriptionId == transcriptionId)
            .ToListAsync(ct);
        if (segmentRows.Count == 0) return null;

        var names = await _db.Speakers
            .AsNoTracking()
            .Where(s => s.RecordingId == rec.Id)
            .ToDictionaryAsync(s => s.Label, s => s.DisplayName, ct);

        var segments = SegmentMapper.ToDtos(segmentRows, names);

        return group switch
        {
            LlmCallGroup.Tags => new LlmTestPrompt(
                TagsPrompt.BuildMessages(
                    _templates.Get("tagcloud", TagsPrompt.DefaultTemplate), segments, charBudget),
                "Tags"),

            LlmCallGroup.Actions => new LlmTestPrompt(
                ActionsPrompt.BuildMessages(
                    _templates.Get("extract-actions", ActionsPrompt.DefaultTemplate), segments,
                    rec.StartedAt ?? rec.CreatedAt, charBudget),
                "Actions"),

            // needName mirrors the pipeline: the summariser is asked for a title only when the user has not
            // named the recording. Forcing it on would test a prompt this recording never gets.
            LlmCallGroup.Summaries => new LlmTestPrompt(
                SummarizationPrompt.BuildMessages(
                    _templates.Get("summarise", SummarizationPrompt.DefaultTemplate), segments,
                    needName: string.IsNullOrWhiteSpace(rec.Name), charBudget),
                "Summary"),

            _ => null,
        };
    }
}
