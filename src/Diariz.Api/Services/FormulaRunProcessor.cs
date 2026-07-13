using System.Text;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Async run pipeline for a Formula over a recording (the enqueued path): resolve the owner's LLM
/// config, build the formula's context, stream a completion, and flip the pre-created
/// <see cref="FormulaResult"/> row to Ready (or Failed with an error), notifying the hub each way. Static so it
/// can be unit-tested with fake clients + an in-memory DbContext, mirroring <see cref="SectionSummaryProcessor"/>.
/// The context/LLM primitives here are shared with the synchronous <see cref="FormulaRunner"/> (the MCP/chat
/// tool path) so the two stay DRY.</summary>
public static class FormulaRunProcessor
{
    public static async Task ProcessAsync(
        DiarizDbContext db, IChatStreamClient chat, ISummarizationSettingsResolver settings,
        IHubContext<TranscriptionHub> hub, FormulaRunJob job, int reduceCharBudget, ILogger logger,
        CancellationToken ct = default)
    {
        var cfg = await settings.ResolveAsync(job.UserId, ct);
        if (!cfg.Enabled)
        {
            await FailAsync(db, hub, job, "No LLM endpoint is configured for this user or server.", ct);
            return;
        }

        var formula = await db.Formulas.FirstOrDefaultAsync(f => f.Id == job.FormulaId, ct);
        if (formula is null)
        {
            await FailAsync(db, hub, job, "The formula was removed before the run completed.", ct);
            return;
        }

        try
        {
            string text;
            if (job.RecordingId is { } recordingId)
            {
                text = await RunOverRecordingAsync(db, chat, cfg, formula, recordingId, ct);
            }
            else
            {
                // Section branch added in Phase 2 - a section-scoped run is never enqueued yet.
                throw new NotImplementedException("Section-scoped formula runs are not implemented yet.");
            }

            var result = await db.FormulaResults.FirstOrDefaultAsync(r => r.Id == job.ResultId, ct);
            if (result is null) return; // result row deleted while the job ran - nothing to flip.

            result.Text = text;
            result.Status = FormulaRunStatus.Ready;
            result.Error = null;
            result.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await hub.NotifyFormulaStatusAsync(job.UserId, job.RecordingId, job.SectionId, job.ResultId,
                nameof(FormulaRunStatus.Ready));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked CTS fired (LLM timeout), not the caller's ct - record it as a failure, don't propagate.
            logger.LogWarning("Formula run {ResultId} timed out", job.ResultId);
            await FailAsync(db, hub, job, "The LLM request timed out.", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Formula run {ResultId} failed", job.ResultId);
            await FailAsync(db, hub, job, ex.Message, ct);
        }
    }

    private static async Task FailAsync(
        DiarizDbContext db, IHubContext<TranscriptionHub> hub, FormulaRunJob job, string error, CancellationToken ct)
    {
        var result = await db.FormulaResults.FirstOrDefaultAsync(r => r.Id == job.ResultId, ct);
        if (result is not null)
        {
            result.Status = FormulaRunStatus.Failed;
            result.Error = error;
            result.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await hub.NotifyFormulaStatusAsync(job.UserId, job.RecordingId, job.SectionId, job.ResultId,
            nameof(FormulaRunStatus.Failed));
    }

    // -- Shared context/LLM primitives (also used by the synchronous FormulaRunner) --

    /// <summary>Runs <paramref name="formula"/> over a single recording: build its flagged context, then stream a
    /// completion with the formula prompt as the system message and the context as the user message.</summary>
    internal static async Task<string> RunOverRecordingAsync(
        DiarizDbContext db, IChatStreamClient chat, SummarizationRequestConfig cfg,
        Formula formula, Guid recordingId, CancellationToken ct)
    {
        var context = await BuildRecordingContextAsync(db, recordingId, formula.Context, ct);
        return await RunPromptAsync(chat, cfg, formula.Prompt, context, ct);
    }

    /// <summary>Loads the recording by id with the includes named by <paramref name="flags"/> (Segments/Speakers
    /// only under Transcript, Summary/Minutes/Actions each behind their own flag), pulls its note lines, and
    /// assembles the Markdown context via <see cref="FormulaContextBuilder.Build"/>. Deliberately applies NO
    /// ownership filter - the recording-run access check lives in the controller/runner (a later section feature
    /// calls this for recordings the caller doesn't personally own). Returns the builder's empty-context fallback
    /// if the recording is missing or yields nothing.</summary>
    internal static async Task<string> BuildRecordingContextAsync(
        DiarizDbContext db, Guid recordingId, FormulaContext flags, CancellationToken ct)
    {
        IQueryable<Recording> query = db.Recordings;

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

        var rec = await query.FirstOrDefaultAsync(r => r.Id == recordingId, ct);
        if (rec is null) return FormulaContextBuilder.EmptyContextFallback;

        var noteLines = flags.HasFlag(FormulaContext.Notes)
            ? await LoadNoteLinesAsync(db, recordingId, ct)
            : [];
        return FormulaContextBuilder.Build(flags, BuildContextData(rec, noteLines));
    }

    /// <summary>Streams a single completion (formula prompt as system, context as user) into a string. A
    /// <c>TimeoutSeconds</c> expiry surfaces as an <see cref="OperationCanceledException"/> with the OUTER
    /// <paramref name="ct"/> still uncancelled (only the linked source fired) - callers distinguish a timeout
    /// from a genuine cancel by testing <c>!ct.IsCancellationRequested</c>.</summary>
    internal static async Task<string> RunPromptAsync(
        IChatStreamClient chat, SummarizationRequestConfig cfg, string systemPrompt, string userContext,
        CancellationToken ct)
    {
        var messages = new[] { new ChatMessage("system", systemPrompt), new ChatMessage("user", userContext) };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        var sb = new StringBuilder();
        await foreach (var token in chat.StreamAsync(cfg, messages, cts.Token))
            sb.Append(token);
        return sb.ToString().Trim();
    }

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
    private static async Task<IReadOnlyList<string>> LoadNoteLinesAsync(
        DiarizDbContext db, Guid recordingId, CancellationToken ct) =>
        await db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .OrderBy(n => n.Ordinal)
            .Select(n => n.Text)
            .ToListAsync(ct);
}
