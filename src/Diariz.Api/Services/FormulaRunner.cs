using System.Linq.Expressions;
using System.Text;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>The formula or the recording it would run over doesn't exist, or the recording isn't
/// accessible to the caller (Phase 1: not visible at all vs. "you can see it but can't run this formula" -
/// see <see cref="FormulaAccessException"/> - is deliberately collapsed to 404 either way).</summary>
public sealed class FormulaNotFoundException(string message) : Exception(message);

/// <summary>The formula exists (and its recording is accessible) but the caller may not run it: a Personal
/// formula they don't own, or a disabled Platform/Diariz formula.</summary>
public sealed class FormulaAccessException(string message) : Exception(message);

/// <summary>The resolved per-user/server LLM config has no endpoint - nothing to run the formula against.</summary>
public sealed class FormulaNotConfiguredException(string message) : Exception(message);

public interface IFormulaRunner
{
    /// <summary>Runs <paramref name="formulaId"/> over <paramref name="recordingId"/> on the caller's behalf:
    /// loads the formula + the recording's context, enforces run-access, calls the resolved LLM synchronously,
    /// and persists (and returns) the resulting <see cref="FormulaResult"/>.</summary>
    Task<FormulaResult> RunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default);
}

/// <summary>Synchronous run pipeline for a Formula: mirrors the one-off LLM calls in
/// <c>ChatController.GenerateTitleAsync</c> - resolve the per-user LLM config, stream a single completion,
/// and persist the Markdown result. Context assembly itself is delegated to the pure
/// <see cref="FormulaContextBuilder"/>.</summary>
public class FormulaRunner : IFormulaRunner
{
    private readonly DiarizDbContext _db;
    private readonly IChatStreamClient _chat;
    private readonly ISummarizationSettingsResolver _settings;

    public FormulaRunner(DiarizDbContext db, IChatStreamClient chat, ISummarizationSettingsResolver settings)
    {
        _db = db;
        _chat = chat;
        _settings = settings;
    }

    public async Task<FormulaResult> RunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default)
    {
        var formula = await _db.Formulas.FirstOrDefaultAsync(f => f.Id == formulaId, ct);
        if (formula is null) throw new FormulaNotFoundException("Formula not found.");

        var rec = await LoadRecordingAsync(userId, recordingId, formula.Context, ct);
        if (rec is null) throw new FormulaNotFoundException("Recording not found.");

        EnsureCanRun(formula, userId);

        var cfg = await _settings.ResolveAsync(userId, ct);
        if (!cfg.Enabled)
            throw new FormulaNotConfiguredException("No LLM endpoint is configured for this user or server.");

        var noteLines = formula.Context.HasFlag(FormulaContext.Notes)
            ? await LoadNoteLinesAsync(recordingId, ct)
            : [];
        var context = FormulaContextBuilder.Build(formula.Context, BuildContextData(rec, noteLines));
        var messages = new[] { new ChatMessage("system", formula.Prompt), new ChatMessage("user", context) };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        // A TimeoutSeconds expiry surfaces here as an OperationCanceledException with the OUTER `ct` still
        // uncancelled (only the linked source fired). The calling controller (added in the next task)
        // distinguishes a timeout from a genuine client disconnect by testing `!ct.IsCancellationRequested`:
        // outer-ct-cancelled = client went away (let it propagate); otherwise = LLM timeout (map to 504).
        var sb = new StringBuilder();
        await foreach (var token in _chat.StreamAsync(cfg, messages, cts.Token))
            sb.Append(token);

        var ordinal = await NextOrdinalAsync(recordingId, ct);
        var result = new FormulaResult
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            CreatedByUserId = userId,
            FormulaId = formula.Id,
            Name = formula.Name,
            Text = sb.ToString().Trim(),
            Ordinal = ordinal,
        };
        _db.FormulaResults.Add(result);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Phase 1 run-access: a Personal formula may only be run by its owner; a Platform/Diariz
    /// formula must be enabled. (Access to the *recording* is checked separately, in
    /// <see cref="LoadRecordingAsync"/>, before this ever runs.)</summary>
    private static void EnsureCanRun(Formula formula, Guid userId)
    {
        if (formula.Scope == FormulaScope.Personal)
        {
            if (formula.OwnerUserId != userId)
                throw new FormulaAccessException("You may not run this formula.");
        }
        else if (!formula.Enabled)
        {
            throw new FormulaAccessException("This formula is disabled.");
        }
    }

    /// <summary>Loads the recording, scoped to the caller via <see cref="AccessibleBy"/>. Every include is
    /// gated on the formula's Context flags so a Summary-only formula doesn't drag the full segment list into
    /// memory: Segments + Speakers only when Transcript is set, Summary/Minutes/Actions each behind their own
    /// flag. The highest-version Transcription itself is always loaded (Summary/Minutes hang off it).</summary>
    private async Task<Recording?> LoadRecordingAsync(
        Guid userId, Guid recordingId, FormulaContext flags, CancellationToken ct)
    {
        IQueryable<Recording> query = _db.Recordings;

        if (flags.HasFlag(FormulaContext.Transcript))
            query = query
                .Include(r => r.Speakers)
                .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                    .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal));

        if (flags.HasFlag(FormulaContext.Summary))
            query = query.Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary);
        if (flags.HasFlag(FormulaContext.Minutes))
            query = query.Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes);
        if (flags.HasFlag(FormulaContext.Actions))
            query = query.Include(r => r.Actions);

        return await query.Where(AccessibleBy(userId)).FirstOrDefaultAsync(r => r.Id == recordingId, ct);
    }

    /// <summary>Phase 1 recording access = ownership. Factored out (as a translatable expression, not just a
    /// predicate over a loaded entity) so room-sharing access can extend this later without touching the
    /// query-building above.</summary>
    private static Expression<Func<Recording, bool>> AccessibleBy(Guid userId) => r => r.UserId == userId;

    private static FormulaContextData BuildContextData(Recording rec, IReadOnlyList<string> noteLines)
    {
        var current = rec.Transcriptions.FirstOrDefault();
        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var segments = current?.Segments
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

        return new FormulaContextData(
            segments, current?.Summary?.Text, current?.MeetingMinutes?.Text, noteLines, actions);
    }

    /// <summary>The recording's own note lines, in order. Loaded separately - <see cref="Recording"/> has no
    /// navigation collection onto <see cref="MeetingNote"/> (it's addressed by <c>RecordingId</c> only).</summary>
    private async Task<IReadOnlyList<string>> LoadNoteLinesAsync(Guid recordingId, CancellationToken ct) =>
        await _db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .OrderBy(n => n.Ordinal)
            .Select(n => n.Text)
            .ToListAsync(ct);

    /// <summary>The next display ordinal for this recording's results (max+1). <c>(RecordingId, Ordinal)</c>
    /// is intentionally NOT a unique index: Ordinal is display-order only, so a rare collision from two
    /// concurrent runs on the same recording is benign - it would nudge list ordering, never correctness -
    /// and doesn't warrant a lock or retry in Phase 1.</summary>
    private async Task<int> NextOrdinalAsync(Guid recordingId, CancellationToken ct)
    {
        var max = await _db.FormulaResults
            .Where(r => r.RecordingId == recordingId)
            .Select(r => (int?)r.Ordinal)
            .MaxAsync(ct);
        return (max ?? -1) + 1;
    }
}
