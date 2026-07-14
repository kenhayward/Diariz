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
        // template, and the formula also declares WHAT THE MODEL SEES. The type contributes only its framing
        // (Overview). Minutes and formulas are the same thing.
        var template = await ResolveTemplateAsync(recordingOwnerId, meetingTypeId, ct);
        var (overview, content) = (template.Overview, template.Content);

        // The assembled context - the user message. Built from the formula's FormulaContext flags, exactly as a
        // Formulas-tab run of the same formula would build it.
        var minutesContext = await FormulaRunProcessor.BuildRecordingContextAsync(
            _db, context.RecordingId, PrimaryContext(template.Context), charBudget, ct);

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
            minutesContext, config, preamble);

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

    /// <summary>What a minutes run needs from the meeting type: its framing, its primary formula's template, and
    /// that formula's declared context.
    ///
    /// <para>Falls back in order: the chosen type (when it exists and is usable by the owner - a Platform type or
    /// their own Personal one), else the seeded General type, else the in-code General standard (nothing seeded
    /// yet). A type whose <c>PrimaryFormulaId</c> is null keeps its own framing but borrows the General formula's
    /// template, rather than producing an empty document.</para></summary>
    private sealed record ResolvedTemplate(string Overview, TemplateContent Content, FormulaContext Context);

    /// <summary>The context a minutes template gets when nothing declares one (no primary formula anywhere): what
    /// a minutes document needs - the transcript, the note-taker's lines, and the canonical actions.</summary>
    private const FormulaContext DefaultMinutesContext =
        FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Actions;

    /// <summary>A primary formula's context, minus the <c>Minutes</c> bit: these ARE the minutes, so including it
    /// would ask the document to read itself. (The meeting-type editor doesn't offer it either.)</summary>
    private static FormulaContext PrimaryContext(FormulaContext flags) => flags & ~FormulaContext.Minutes;

    private async Task<ResolvedTemplate> ResolveTemplateAsync(
        Guid ownerId, Guid? meetingTypeId, CancellationToken ct)
    {
        if (meetingTypeId is { } id)
        {
            var chosen = await _db.MeetingTypes
                .Include(m => m.PrimaryFormula)
                .FirstOrDefaultAsync(m => m.Id == id && (m.UserId == null || m.UserId == ownerId), ct);

            if (chosen is not null)
                return chosen.PrimaryFormula is { } f
                    ? new(chosen.Overview, TemplateContent.Parse(f.ContentJson), f.Context)
                    : await GeneralAsync(chosen.Overview, ct);
        }

        var general = await _db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .FirstOrDefaultAsync(m => m.Key == MeetingType.GeneralKey, ct);

        if (general is not null) return await GeneralAsync(general.Overview, ct);

        var std = StandardGeneral();
        return new(std.Overview, std.Content, DefaultMinutesContext);
    }

    /// <summary>The seeded General type's formula, under <paramref name="overview"/> (which may belong to a type
    /// that has no primary formula of its own).</summary>
    private async Task<ResolvedTemplate> GeneralAsync(string overview, CancellationToken ct)
    {
        var general = await _db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .FirstOrDefaultAsync(m => m.Key == MeetingType.GeneralKey, ct);

        return general?.PrimaryFormula is { } f
            ? new(overview, TemplateContent.Parse(f.ContentJson), f.Context)
            : new(overview, StandardGeneral().Content, DefaultMinutesContext);
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
