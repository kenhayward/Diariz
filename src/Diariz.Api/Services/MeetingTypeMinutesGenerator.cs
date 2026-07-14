using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Turns a recording's chosen meeting type (or the General default) into Markdown minutes: resolves the
/// type, reads the platform-wide generation mode, builds the field resolver from the meeting's metadata + canonical
/// actions, and runs the matching <see cref="IMeetingTypeMinutesStrategy"/>. The processor calls this instead of the
/// old single-shot prompt.</summary>
public interface IMeetingTypeMinutesGenerator
{
    Task<string> GenerateAsync(
        Guid recordingOwnerId, Guid? meetingTypeId, MeetingMinutesContext context,
        IReadOnlyList<SegmentDto> segments, IReadOnlyList<ExtractedAction> actions,
        IReadOnlyList<MeetingNoteDto> notes,
        SummarizationRequestConfig config, int charBudget, CancellationToken ct = default);
}

public sealed class MeetingTypeMinutesGenerator : IMeetingTypeMinutesGenerator
{
    private readonly DiarizDbContext _db;
    private readonly IEnumerable<IMeetingTypeMinutesStrategy> _strategies;
    private readonly IPromptTemplateProvider _prompts;
    private readonly IMeetingMinutesClient _client;

    public MeetingTypeMinutesGenerator(
        DiarizDbContext db, IEnumerable<IMeetingTypeMinutesStrategy> strategies, IPromptTemplateProvider prompts,
        IMeetingMinutesClient client)
    {
        _db = db;
        _strategies = strategies;
        _prompts = prompts;
        _client = client;
    }

    public async Task<string> GenerateAsync(
        Guid recordingOwnerId, Guid? meetingTypeId, MeetingMinutesContext context,
        IReadOnlyList<SegmentDto> segments, IReadOnlyList<ExtractedAction> actions,
        IReadOnlyList<MeetingNoteDto> notes,
        SummarizationRequestConfig config, int charBudget, CancellationToken ct = default)
    {
        // A meeting type carries no prompts of its own: the document it produces is its PRIMARY FORMULA's
        // template. The type contributes only its framing (Overview).
        var (overview, content) = await ResolveTemplateAsync(recordingOwnerId, meetingTypeId, ct);

        var mode = (await _db.PlatformSettings.FirstOrDefaultAsync(ct))?.MinutesGenerationMode
                   ?? MinutesGenerationMode.SingleCall;
        var strategy = _strategies.FirstOrDefault(s => s.Mode == mode)
                       ?? _strategies.First(s => s.Mode == MinutesGenerationMode.SingleCall);

        // Steering: the note-taker's own lines are an emphasis signal for EVERY prompt-driven section - they
        // ride the shared preamble so both strategies inherit them. No notes -> the preamble is unchanged.
        var preamble = _prompts.Get("minutes-section-preamble", MeetingMinutesPreamble.Default);
        if (notes.Count > 0) preamble += "\n\n" + SteeringBlock(notes);

        // Enhanced notes pre-pass: only when the template asks for the field. One LLM call expands each note
        // line from the transcript; the section itself is then substituted deterministically (provenance:
        // bold user text + [mm:ss] transcript deep-links). A notes failure must never fail the minutes.
        string? notesMarkdown = null;
        if (content.HasField("notes"))
        {
            if (notes.Count == 0)
            {
                notesMarkdown = NotesComposer.NoNotes;
            }
            else
            {
                try
                {
                    var messages = NotesEnhancer.BuildMessages(notes, segments, charBudget);
                    var raw = await _client.GenerateAsync(config, messages, ct);
                    notesMarkdown = NotesComposer.Render(
                        notes, NotesEnhancer.ParseResponse(raw, notes.Count), context.RecordingId);
                }
                catch
                {
                    notesMarkdown = NotesComposer.RenderRaw(notes);
                }
            }
        }

        var input = new MinutesComposition(
            content, overview, name => ResolveField(name, context, actions, notesMarkdown),
            segments, config, charBudget, preamble);

        return await strategy.GenerateAsync(input, ct);
    }

    /// <summary>The preamble block listing the note-taker's own lines, so every section weights them.</summary>
    internal static string SteeringBlock(IReadOnlyList<MeetingNoteDto> notes)
    {
        var lines = notes.Select(n =>
            n.CapturedAtMs is { } ms ? $"- {n.Text} (at {NotesEnhancer.Mmss(ms)})" : $"- {n.Text}");
        return "NOTE-TAKER'S EMPHASIS\n" +
               "The attendee flagged these points while the meeting happened. Give them weight, resolve each " +
               "specifically from the transcript, and prefer their terminology:\n" + string.Join("\n", lines);
    }

    /// <summary>The template to produce: the chosen type's framing plus its <b>primary formula's</b> content.
    ///
    /// <para>Falls back in order: the chosen type (when it exists and is usable by the owner - a Platform type or
    /// their own Personal one), else the seeded General type, else the in-code General standard (nothing seeded
    /// yet). A type whose <c>PrimaryFormulaId</c> is null keeps its own framing but borrows the General formula's
    /// content, rather than producing an empty document.</para></summary>
    private async Task<(string Overview, TemplateContent Content)> ResolveTemplateAsync(
        Guid ownerId, Guid? meetingTypeId, CancellationToken ct)
    {
        if (meetingTypeId is { } id)
        {
            var chosen = await _db.MeetingTypes
                .Include(m => m.PrimaryFormula)
                .FirstOrDefaultAsync(m => m.Id == id && (m.UserId == null || m.UserId == ownerId), ct);

            if (chosen is not null)
                return (chosen.Overview, chosen.PrimaryFormula is { } f
                    ? TemplateContent.Parse(f.ContentJson)
                    : await GeneralContentAsync(ct));
        }

        var general = await _db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .FirstOrDefaultAsync(m => m.Key == MeetingType.GeneralKey, ct);

        if (general is not null)
            return (general.Overview, general.PrimaryFormula is { } gf
                ? TemplateContent.Parse(gf.ContentJson)
                : StandardGeneral().Content);

        var std = StandardGeneral();
        return (std.Overview, std.Content);
    }

    private async Task<TemplateContent> GeneralContentAsync(CancellationToken ct)
    {
        var general = await _db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .FirstOrDefaultAsync(m => m.Key == MeetingType.GeneralKey, ct);

        return general?.PrimaryFormula is { } f
            ? TemplateContent.Parse(f.ContentJson)
            : StandardGeneral().Content;
    }

    /// <summary>The in-code General standard - the last resort when nothing has been seeded.</summary>
    private static (string Overview, TemplateContent Content) StandardGeneral()
    {
        var std = MeetingTypeSeeder.Standards.First(s => s.Key == MeetingType.GeneralKey);
        return (std.Overview, TemplateContent.Parse(std.ContentJson));
    }

    /// <summary>Field substitution is shared with the formula run pipeline - see <see cref="TemplateFields"/>.</summary>
    private static string? ResolveField(
        string name, MeetingMinutesContext ctx, IReadOnlyList<ExtractedAction> actions, string? notesMarkdown) =>
        TemplateFields.Resolve(name, ctx.MeetingDate, ctx.Title, ctx.Attendees, ctx.DurationMs, actions, notesMarkdown);
}
