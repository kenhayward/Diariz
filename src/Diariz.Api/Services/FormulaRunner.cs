using System.Linq.Expressions;
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

    /// <summary>Runs the shared pre-flight guards for a recording formula-run - load formula (404 if missing),
    /// recording access (404), subscription/run-access (<see cref="EnsureCanRun"/> 404/403), and LLM config
    /// (<see cref="FormulaNotConfiguredException"/> 400) - and returns the resolved <see cref="Formula"/> on
    /// success. Shared by the synchronous tool path (<see cref="RunAsync"/>) and the async controller, which
    /// enqueues a job instead of running inline.</summary>
    Task<Formula> ValidateRecordingRunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default);
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

    /// <summary>The shared pre-flight guards, in the order the controller and the tool path both rely on:
    /// load formula (404) -> recording access (404) -> subscription/run-access (404/403) -> LLM config (400).
    /// Returns the resolved <see cref="Formula"/> so the caller doesn't re-load it.</summary>
    public async Task<Formula> ValidateRecordingRunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default)
    {
        var formula = await _db.Formulas.FirstOrDefaultAsync(f => f.Id == formulaId, ct);
        if (formula is null) throw new FormulaNotFoundException("Formula not found.");

        if (!await IsRecordingAccessibleAsync(userId, recordingId, ct))
            throw new FormulaNotFoundException("Recording not found.");

        var subscribed = formula.Scope == FormulaScope.Personal
            && formula.OwnerUserId != userId
            && formula.Shared
            && await _db.FormulaSubscriptions.AnyAsync(s => s.FormulaId == formula.Id && s.UserId == userId, ct);
        EnsureCanRun(formula, userId, subscribed);

        var cfg = await _settings.ResolveAsync(userId, ct);
        if (!cfg.Enabled)
            throw new FormulaNotConfiguredException("No LLM endpoint is configured for this user or server.");

        return formula;
    }

    public async Task<FormulaResult> RunAsync(Guid userId, Guid recordingId, Guid formulaId, CancellationToken ct = default)
    {
        var formula = await ValidateRecordingRunAsync(userId, recordingId, formulaId, ct);
        var cfg = await _settings.ResolveAsync(userId, ct);

        // Reuse the shared context/LLM primitives (also driving the async FormulaRunProcessor). Ownership was
        // already enforced above by IsRecordingAccessibleAsync, so re-loading the context here (no ownership
        // filter) is safe. A TimeoutSeconds expiry surfaces as an OperationCanceledException with the OUTER `ct` still
        // uncancelled; the calling controller distinguishes a timeout from a client disconnect via
        // `!ct.IsCancellationRequested` (outer-ct-cancelled = client went away; otherwise = LLM timeout -> 504).
        var text = await FormulaRunProcessor.RunOverRecordingAsync(_db, _chat, cfg, formula, recordingId, ct);

        var ordinal = await NextOrdinalAsync(recordingId, ct);
        var result = new FormulaResult
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            CreatedByUserId = userId,
            FormulaId = formula.Id,
            Name = formula.Name,
            Text = text,
            Ordinal = ordinal,
            Status = FormulaRunStatus.Ready,
        };
        _db.FormulaResults.Add(result);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Phase 1 run-access: a Personal formula may only be run by its owner; a Platform/Diariz
    /// formula must be enabled. A non-owned Personal formula throws <see cref="FormulaNotFoundException"/>
    /// (not Access) so its very existence isn't leaked - consistent with the CRUD controller's 404s; a
    /// disabled Platform/Diariz formula is public knowledge, so it stays <see cref="FormulaAccessException"/>.
    /// (Access to the *recording* is checked separately, in <see cref="IsRecordingAccessibleAsync"/>, before
    /// this ever runs.)</summary>
    private static void EnsureCanRun(Formula formula, Guid userId, bool subscribed)
    {
        if (formula.Scope == FormulaScope.Personal)
        {
            // Owner, or a subscriber to a shared formula. Otherwise hide its existence (404) - it's added via
            // the discovery browser, not run directly.
            if (formula.OwnerUserId != userId && !subscribed)
                throw new FormulaNotFoundException("Formula not found.");
        }
        else if (!formula.Enabled)
        {
            throw new FormulaAccessException("This formula is disabled.");
        }
    }

    /// <summary>Phase 1 recording-run access = ownership. The actual run context is (re)built - with the
    /// Context-gated includes - by <see cref="FormulaRunProcessor.BuildRecordingContextAsync"/>, which
    /// deliberately applies no ownership filter, so this gate stays here.</summary>
    private async Task<bool> IsRecordingAccessibleAsync(Guid userId, Guid recordingId, CancellationToken ct) =>
        await _db.Recordings.Where(AccessibleBy(userId)).AnyAsync(r => r.Id == recordingId, ct);

    /// <summary>Phase 1 recording access = ownership. Factored out (as a translatable expression, not just a
    /// predicate over a loaded entity) so room-sharing access can extend this later.</summary>
    private static Expression<Func<Recording, bool>> AccessibleBy(Guid userId) => r => r.UserId == userId;

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
