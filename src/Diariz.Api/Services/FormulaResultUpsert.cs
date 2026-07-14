using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>The row a formula run writes into.
///
/// <para>A run used to <b>append</b> a result every time. Now that a meeting type's additional formulas re-fire
/// automatically whenever the minutes regenerate, appending would pile up duplicates on any recording that is
/// re-transcribed. So a run of formula F over recording R <b>replaces R's existing result for F</b>.</para>
///
/// <para>The row is reused, keeping its <c>Id</c> and <c>Ordinal</c> - so the list doesn't reshuffle and an open
/// deep-link to the result stays valid.</para>
///
/// <para><b>The hazard, and the guard.</b> A result's Markdown is user-editable, so a blind replace would silently
/// destroy a hand-edited document. This mirrors the rule minutes have always had: an <b>automatic</b> run (the
/// pipeline) <b>skips</b> a result the user has edited, exactly as <c>MeetingMinutesProcessor</c> refuses to
/// overwrite hand-edited minutes; an <b>explicit</b> run replaces it and clears the flag, exactly as
/// <c>ApplyMeetingType</c> clears it before regenerating.</para>
///
/// <para>There is deliberately <b>no unique index</b> on (RecordingId, FormulaId): enforcing one would mean
/// de-duplicating existing rows on upgrade, and those are real user documents. Legacy duplicates are left alone
/// (the most recent is the one replaced) and collapse naturally the next time that formula runs.</para></summary>
public static class FormulaResultUpsert
{
    /// <summary>The <see cref="FormulaResult"/> to fill in, added to the context if new. Returns <c>null</c> when
    /// an automatic run must stand down because the existing result was hand-edited.</summary>
    public static async Task<FormulaResult?> ForRecordingAsync(
        DiarizDbContext db, Guid recordingId, Formula formula, Guid? userId, bool automatic,
        CancellationToken ct = default)
    {
        var existing = await db.FormulaResults
            .Where(r => r.RecordingId == recordingId && r.FormulaId == formula.Id)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (automatic && existing.IsUserEdited) return null;

            Reset(existing, formula, userId);
            return existing;
        }

        var ordinal = (await db.FormulaResults
            .Where(r => r.RecordingId == recordingId)
            .Select(r => (int?)r.Ordinal)
            .MaxAsync(ct) ?? -1) + 1;

        var row = new FormulaResult
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            CreatedByUserId = userId,
            FormulaId = formula.Id,
            Name = formula.Name,
            Ordinal = ordinal,
            Status = FormulaRunStatus.Generating,
        };
        db.FormulaResults.Add(row);
        return row;
    }

    /// <summary>The folder twin: replaces this folder's existing result for the same formula.</summary>
    public static async Task<SectionFormulaResult?> ForSectionAsync(
        DiarizDbContext db, Guid sectionId, Formula formula, Guid? userId, bool automatic,
        CancellationToken ct = default)
    {
        var existing = await db.SectionFormulaResults
            .Where(r => r.SectionId == sectionId && r.FormulaId == formula.Id)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (automatic && existing.IsUserEdited) return null;

            existing.Name = formula.Name;
            existing.Text = string.Empty;
            existing.Status = FormulaRunStatus.Generating;
            existing.Error = null;
            existing.IsUserEdited = false;
            existing.CreatedByUserId = userId ?? existing.CreatedByUserId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var ordinal = (await db.SectionFormulaResults
            .Where(r => r.SectionId == sectionId)
            .Select(r => (int?)r.Ordinal)
            .MaxAsync(ct) ?? -1) + 1;

        var row = new SectionFormulaResult
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
            CreatedByUserId = userId,
            FormulaId = formula.Id,
            Name = formula.Name,
            Ordinal = ordinal,
            Status = FormulaRunStatus.Generating,
        };
        db.SectionFormulaResults.Add(row);
        return row;
    }

    /// <summary>Put an existing row back into the Generating state for a fresh run. Id and Ordinal are kept.</summary>
    private static void Reset(FormulaResult r, Formula formula, Guid? userId)
    {
        r.Name = formula.Name;
        r.Text = string.Empty;
        r.Status = FormulaRunStatus.Generating;
        r.Error = null;
        r.IsUserEdited = false;
        r.CreatedByUserId = userId ?? r.CreatedByUserId;
        r.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
