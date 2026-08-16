using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Admin viewer over the LLM usage log (<c>LlmCalls</c>) - the read side that
/// <see cref="LlmUsageQuery"/>'s filter/sort/totals primitives were built for.
///
/// <c>[ManagePlatform]</c>, not the weaker <c>ReadAdminSettings</c> that Administrators also hold -
/// this log carries every user's activity across the whole platform, not just platform
/// configuration, so only a Platform Administrator may read it.</summary>
[ApiController]
[Route("api/admin/llm-usage")]
[Authorize(Policy = "ManagePlatform")]
public class LlmUsageController : ControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Ceiling on how many operations a single <c>mode=operations</c> request may materialize
    /// into API process memory (see the guard in <see cref="List"/>, and the "ONE round trip fetches
    /// EVERY operation..." comment below for why that materialization happens at all). Deliberately
    /// generous - 25,000 operations is far beyond anything a real admin filter/date-range combination
    /// produces today - because the guard exists purely as a backstop against a runaway request (e.g.
    /// <c>from</c> widened well past the 30-day default; <c>Uploads</c>/retention allows up to 90 days),
    /// not as a limit anyone should expect to hit in normal use.</summary>
    public const int MaxOperationsPerRequest = 25_000;

    private readonly DiarizDbContext _db;

    public LlmUsageController(DiarizDbContext db)
    {
        _db = db;
    }

    /// <summary>Composite key used to collapse operations-mode rows. <c>OperationId, Kind, UserId,
    /// UserEmail, RecordingId, RecordingTitle, SectionId, SectionName</c> - eight of the nine fields -
    /// are genuinely scope-invariant: <see cref="LlmCallScope"/> stamps them once when it is pushed and
    /// every call made while it is active copies them from the same scope instance
    /// (<see cref="LlmTelemetryHandler.Record"/>).
    ///
    /// <c>Model</c> is NOT one of those eight. <see cref="LlmCallScope"/> has no <c>Model</c> property
    /// at all - <c>Model</c> is parsed per call, out of that specific call's own request JSON body
    /// (<see cref="LlmTelemetryHandler.ModelOf"/>), independent of whatever scope is active. It is only
    /// CONVENTIONALLY constant within one operation, because every current call site resolves one model
    /// config and reuses its name for every call it makes - nothing enforces that. So two calls sharing
    /// an <c>OperationId</c> CAN land in different groups here if a future or misbehaving call site ever
    /// varies <c>Model</c> mid-operation, and <see cref="LlmCall.OperationId"/> alone is NOT a safe
    /// substitute grouping key the way it would be for the other eight fields.
    ///
    /// Consequence for counting: <c>LlmUsageTotals.Operations</c> (<c>COUNT(DISTINCT OperationId)</c>)
    /// is therefore only a lower bound on the number of rows this grouping can produce, not an exact
    /// count - <c>List</c> below counts the actual composite-key groups (<c>candidates.Count</c>, off
    /// the fully materialized operations-mode result set) instead of assuming they match distinct
    /// <c>OperationId</c>s.</summary>
    private sealed record OperationKey(
        Guid OperationId, LlmCallKind Kind, Guid? UserId, string UserEmail,
        Guid? RecordingId, string? RecordingTitle, Guid? SectionId, string? SectionName, string Model);

    [HttpGet]
    [EndpointSummary("List LLM usage log entries")]
    [EndpointDescription(
        "Platform Administrator only. mode=operations (default) collapses every call belonging to one " +
        "operation into a single row (turns, summed tokens, an all-succeeded outcome); mode=calls returns " +
        "one row per call. total and totals both cover the whole filtered set, never just the returned page.")]
    public async Task<IActionResult> List(
        [FromQuery] string mode = "operations",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] Guid[]? userIds = null,
        [FromQuery] int[]? kinds = null,
        [FromQuery] string[]? models = null,
        [FromQuery] string? outcome = null,
        [FromQuery] Guid? recordingId = null,
        [FromQuery] Guid? sectionId = null,
        [FromQuery] string? sort = null,
        [FromQuery] bool desc = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var isOperationsMode = string.Equals(mode, "operations", StringComparison.OrdinalIgnoreCase);
        var isCallsMode = string.Equals(mode, "calls", StringComparison.OrdinalIgnoreCase);
        if (!isOperationsMode && !isCallsMode)
            return BadRequest($"Unknown mode '{mode}'. Expected 'operations' or 'calls'.");

        string sortColumn;
        if (string.IsNullOrEmpty(sort))
        {
            sortColumn = nameof(LlmCall.StartedAt);
        }
        else if (!LlmUsageQuery.TryResolveSort(sort, out sortColumn))
        {
            // Silently ignoring an unrecognised sort would show the administrator data ordered
            // differently from what they asked for, which is worse than an error.
            return BadRequest($"Unknown sort key '{sort}'.");
        }

        // page/pageSize are clamped rather than rejected, unlike sort/mode above: any integer is a
        // meaningful request ("give me a page of this size") that clamping can satisfy without changing
        // what the response MEANS - a caller who asked for pageSize=10000 still gets a valid, correctly
        // ordered page, just a smaller one than they asked for. An unrecognised sort/mode has no such
        // reasonable substitute: guessing one changes what the data means (differently ordered/shaped
        // than requested), which is why those are rejected instead.
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var filter = new LlmUsageFilter(from, to, userIds, kinds, models, outcome, recordingId, sectionId);
        var filtered = LlmUsageQuery.Apply(_db.LlmCalls.AsNoTracking(), filter, DateTimeOffset.UtcNow);

        // ONE round trip for the aggregate over the WHOLE filtered set - never derived from the page
        // fetched below. totals.Calls (COUNT(*)) is exactly the "total" calls mode needs, so it is read
        // off this result rather than issuing a second COUNT query. totals.Operations (COUNT(DISTINCT
        // OperationId)) is deliberately NOT reused the same way for operations mode - see the
        // OperationKey doc comment for why that would only be a lower bound, not an exact count.
        var totals = await LlmUsageQuery.TotalsAsync(filtered, ct);

        if (isCallsMode)
        {
            var ordered = OrderCalls(filtered, sortColumn, desc);
            var rows = await ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(c => new LlmUsageCallRow(
                    c.Id, c.OperationId, c.Sequence, c.Kind, c.UserId, c.UserEmail,
                    c.RecordingId, c.RecordingTitle, c.SectionId, c.SectionName, c.Model,
                    c.StartedAt, c.CompletedAt, c.DurationMs,
                    c.PromptTokens, c.CompletionTokens, c.ReasoningTokens, c.TotalTokens,
                    c.Success, c.StatusCode, c.ErrorKind))
                .ToListAsync(ct);
            return Ok(new LlmUsagePage<LlmUsageCallRow>(rows, page, pageSize, totals.Calls, totals));
        }

        var grouped = filtered.GroupBy(c => new OperationKey(
            c.OperationId, c.Kind, c.UserId, c.UserEmail,
            c.RecordingId, c.RecordingTitle, c.SectionId, c.SectionName, c.Model));

        // Fence BEFORE the operations query below runs, using totals.Operations - already in hand, no
        // extra round trip. totals.Operations is COUNT(DISTINCT OperationId), a LOWER bound on the true
        // number of operation rows (see the OperationKey doc comment: the real composite-key group count
        // can only be greater, never smaller), so this can under-trigger but never falsely reject a
        // request that would actually have been fine.
        //
        // An explicit 400, never silent truncation: truncating the candidate set would make `total`
        // wrong and leave real rows unreachable - the exact under-reporting bug Finding 1 exists to
        // prevent, reintroduced through a different door. The real risk this fences is memory, not
        // correctness: the round trip below (see its comment) materializes every matching operation into
        // API process memory, and at very high counts that transient allocation risks GC pauses across
        // the whole API process - which also serves transcription callbacks, SignalR, and the
        // summarization worker. `from` is an ordinary query parameter and the platform's audio/backup
        // retention runs well past the 30-day default filter, so a wide-open date range is one URL edit
        // away, not a hypothetical.
        if (totals.Operations > MaxOperationsPerRequest)
            return BadRequest(
                $"This filter matches too many operations to list ({totals.Operations:N0}+, over the " +
                $"{MaxOperationsPerRequest:N0} limit). Narrow the date range or add more filters.");

        // ONE round trip fetches EVERY operation matching the filter, UNORDERED - not paged here.
        // Ordering, the exact group count, tiebreaking, and paging itself all happen in C# afterward (see
        // below) rather than in this query. That is a deliberate, forced trade, not a preference - see
        // the paragraph below the query for what forced it and what was actually established about why.
        var candidates = await grouped
            .Select(g => new
            {
                // A plain key projection, not an aggregate: Postgres's uuid type has no MAX/MIN
                // aggregate (`function max(uuid) does not exist`), and none is needed here anyway -
                // OperationId is part of the grouping key, already constant per group.
                g.Key.OperationId,
                g.Key.Kind, g.Key.UserId, g.Key.UserEmail,
                g.Key.RecordingId, g.Key.RecordingTitle, g.Key.SectionId, g.Key.SectionName, g.Key.Model,
                Turns = g.Count(),
                StartedAt = g.Min(c => c.StartedAt),
                CompletedAt = g.Max(c => c.CompletedAt),
                // Sum and "how many rows had a value" travel together, same as LlmUsageQuery.TotalsAsync:
                // EF's translation of a nullable Sum is COALESCE(sum(x), 0), not a bare sum(x), so an
                // operation where NOTHING reported a given token column would otherwise display "0
                // tokens" instead of "not measured" - see the null-coalescing below, and Fix 3 in
                // task-3-report.md's fix-round-2 section for the failing case this closes.
                PromptTokensSum = g.Sum(c => (long?)c.PromptTokens),
                PromptTokensMeasured = g.Count(c => c.PromptTokens != null),
                CompletionTokensSum = g.Sum(c => (long?)c.CompletionTokens),
                CompletionTokensMeasured = g.Count(c => c.CompletionTokens != null),
                ReasoningTokensSum = g.Sum(c => (long?)c.ReasoningTokens),
                ReasoningTokensMeasured = g.Count(c => c.ReasoningTokens != null),
                TotalTokensSum = g.Sum(c => (long?)c.TotalTokens),
                TotalTokensMeasured = g.Count(c => c.TotalTokens != null),
                Success = g.All(c => c.Success),
            })
            .ToListAsync(ct);

        // WHY THIS IS UNORDERED, AND WHAT WAS ACTUALLY ESTABLISHED (softened from an earlier, overreaching
        // version of this comment - see task-3-report.md's fix-round-2 section, Fix 2, for the full
        // correction). Three shapes were tried against real Postgres before landing here:
        //   1. Ordering the raw IQueryable<IGrouping<OperationKey, LlmCall>> directly: a single OrderBy
        //      translated fine; adding a ThenBy(g => g.Key.OperationId) tiebreaker made the query
        //      untranslatable (InvalidOperationException) for every one of the eight whitelisted keys.
        //      This IS real evidence for a compound-ordering limitation.
        //   2. Projecting into LlmUsageOperationRow (a named record) FIRST, then ordering the projection:
        //      also failed. This is NOT valid evidence about compound ordering specifically - EF cannot
        //      trace a property read on a positionally-constructed record's members back to the SQL
        //      expression that produced them once ordering happens outside the projection, so this would
        //      plausibly have failed even for a single OrderBy. It was never isolated that way.
        //   3. Projecting into an ANONYMOUS type instead (ordering by anonymous-projection properties is
        //      EF's documented happy path) and ordering with a ThenBy chain: also failed - this IS real
        //      evidence, parallel in structure to attempt 1. A single OrderBy on a composite anonymous
        //      tuple key (new { Primary, OperationId }) was also tried and also failed, but that result is
        //      NOT valid evidence against "it's specifically the two-call ThenBy chain": there is no SQL
        //      translation for "ORDER BY <an anonymous object value>" under any circumstances, grouped
        //      query or not, so this experiment could never have succeeded regardless of the real cause.
        // What that leaves established: a single-key ORDER BY over a GroupBy-sourced aggregate projection
        // translates without issue (attempt 1's first half, and every operations-mode test before the
        // tiebreaker existed); a second ORDER BY key over the same shape, expressed as a ThenBy chain,
        // failed twice in a row across two structurally different projections. That is consistent with a
        // genuine EF Core/Npgsql compound-ordering limitation, but it is not proven beyond those two data
        // points - this is a brand-new EF 10 / Npgsql 10 major version with a limited track record, and no
        // deeper investigation (e.g. EF's own issue tracker) was done given time already spent. If the
        // MaxOperationsPerRequest guard above ever fires in practice and true server-side windowed paging
        // becomes worth pursuing, `SqlQueryRaw`/`FromSqlRaw` (bypassing the LINQ translator entirely for a
        // hand-written query) is the documented escape hatch - not a further LINQ reshaping attempt.
        var operationsTotal = candidates.Count;

        // Tiebreak on the FULL composite key (every OperationKey field), not just (OperationId, Model).
        // An earlier version tie-broke on (OperationId, Model) alone, reasoning "Model is the only field
        // that can vary within an OperationId" - the exact class of unverified assumption Finding 1
        // punished, just relocated to the other eight fields. The full composite key needs no such
        // assumption: two different groups are guaranteed to differ in at least one of these fields by
        // construction (that is what "group" means), so this can never leave an unresolved tie. String
        // fields compare with StringComparer.Ordinal - see OrderCalls's doc comment for why calls mode
        // and operations mode must not silently use two different string collations for the same column.
        // Tiebreak direction is fixed (always ascending) regardless of `desc`: only totality/uniqueness
        // matters for correctness, not which direction the tiebreak itself runs.
        IOrderedEnumerable<T> RankBy<T, TKey>(
            IEnumerable<T> source, Func<T, TKey> primary, IComparer<TKey>? comparer,
            Func<T, Guid> operationId, Func<T, LlmCallKind> kind, Func<T, Guid?> userId, Func<T, string> userEmail,
            Func<T, Guid?> recordingId, Func<T, string?> recordingTitle, Func<T, Guid?> sectionId,
            Func<T, string?> sectionName, Func<T, string> model) =>
            (desc ? source.OrderByDescending(primary, comparer) : source.OrderBy(primary, comparer))
                .ThenBy(operationId)
                .ThenBy(kind)
                .ThenBy(userId)
                .ThenBy(userEmail, StringComparer.Ordinal)
                .ThenBy(recordingId)
                .ThenBy(recordingTitle, StringComparer.Ordinal)
                .ThenBy(sectionId)
                .ThenBy(sectionName, StringComparer.Ordinal)
                .ThenBy(model, StringComparer.Ordinal);

        // The per-column measured/null treatment (see the projection above) is applied here, once, in
        // memory - every consumer below (sorting, paging, the final DTO) sees the corrected long? value,
        // never the raw COALESCE-to-0 sum.
        var corrected = candidates
            .Select(x => new
            {
                x.OperationId, x.Kind, x.UserId, x.UserEmail,
                x.RecordingId, x.RecordingTitle, x.SectionId, x.SectionName, x.Model,
                x.Turns, x.StartedAt, x.CompletedAt,
                PromptTokens = x.PromptTokensMeasured > 0 ? x.PromptTokensSum : null,
                CompletionTokens = x.CompletionTokensMeasured > 0 ? x.CompletionTokensSum : null,
                ReasoningTokens = x.ReasoningTokensMeasured > 0 ? x.ReasoningTokensSum : null,
                TotalTokens = x.TotalTokensMeasured > 0 ? x.TotalTokensSum : null,
                x.Success,
            })
            .ToList();

        var rankedCandidates = sortColumn switch
        {
            nameof(LlmCall.DurationMs) => RankBy(corrected, x => x.CompletedAt - x.StartedAt, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.PromptTokens) => RankBy(corrected, x => x.PromptTokens, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.CompletionTokens) => RankBy(corrected, x => x.CompletionTokens, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.TotalTokens) => RankBy(corrected, x => x.TotalTokens, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.Kind) => RankBy(corrected, x => x.Kind, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.Model) => RankBy(corrected, x => x.Model, StringComparer.Ordinal,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            nameof(LlmCall.UserEmail) => RankBy(corrected, x => x.UserEmail, StringComparer.Ordinal,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
            _ /* StartedAt */ => RankBy(corrected, x => x.StartedAt, null,
                x => x.OperationId, x => x.Kind, x => x.UserId, x => x.UserEmail,
                x => x.RecordingId, x => x.RecordingTitle, x => x.SectionId, x => x.SectionName, x => x.Model),
        };

        var operationRows = rankedCandidates
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new LlmUsageOperationRow(
                x.OperationId, x.Kind, x.UserId, x.UserEmail, x.RecordingId, x.RecordingTitle, x.SectionId, x.SectionName, x.Model,
                x.Turns, x.StartedAt, x.CompletedAt, x.PromptTokens, x.CompletionTokens, x.ReasoningTokens, x.TotalTokens, x.Success))
            .ToList();
        return Ok(new LlmUsagePage<LlmUsageOperationRow>(operationRows, page, pageSize, operationsTotal, totals));
    }

    [HttpGet("summary")]
    [EndpointSummary("Roll up LLM usage log entries by user, model, and/or call kind")]
    [EndpointDescription(
        "Platform Administrator only. groupBy is a required, comma-separated list of 'user', 'model', " +
        "and/or 'kind' - an unrecognised or missing value is rejected with 400, same discipline as sort " +
        "and mode. Each returned group's tokens/second is that group's own SUM(completion)/SUM(duration), " +
        "never an average of its rows or the overall total's rate. turns is reported per operation as an " +
        "average and a maximum, never summed across operations. totals covers the whole filtered set, " +
        "the same as List's totals, computed through the same filter so the two views can never disagree " +
        "about what is in scope.")]
    public async Task<IActionResult> Summary(
        [FromQuery] string? groupBy = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] Guid[]? userIds = null,
        [FromQuery] int[]? kinds = null,
        [FromQuery] string[]? models = null,
        [FromQuery] string? outcome = null,
        [FromQuery] Guid? recordingId = null,
        [FromQuery] Guid? sectionId = null,
        CancellationToken ct = default)
    {
        // Silently ignoring an unrecognised (or missing) groupBy would show the administrator a report
        // grouped differently from what they asked for, which is worse than an error - same discipline
        // as the unrecognised-sort/unrecognised-mode rejections in List above.
        if (!LlmUsageQuery.TryResolveGroupBy(groupBy, out var dimensions))
            return BadRequest(
                $"Unknown or missing groupBy value '{groupBy}'. Expected a comma-separated list of " +
                "'user', 'model', and/or 'kind'.");

        // Reuse LlmUsageQuery.Apply for the filter, exactly as List does above, so the summary and the
        // detail view can never silently disagree about what is "in scope" for the same query string.
        var filter = new LlmUsageFilter(from, to, userIds, kinds, models, outcome, recordingId, sectionId);
        var filtered = LlmUsageQuery.Apply(_db.LlmCalls.AsNoTracking(), filter, DateTimeOffset.UtcNow);

        var totals = await LlmUsageQuery.TotalsAsync(filtered, ct);
        var groups = await LlmUsageQuery.SummaryAsync(filtered, dimensions, ct);
        return Ok(new LlmUsageSummary(groups, totals));
    }

    /// <summary>Applies the requested sort, then a final <c>Id</c> tiebreaker. Every whitelisted column
    /// - including <c>StartedAt</c>, which <see cref="LlmCallScope"/>'s own doc notes can tie under
    /// concurrent fan-out - is non-unique, and Postgres gives no guarantee that tied rows come back in
    /// the same relative order across two separately executed, separately paginated queries (a plain
    /// <c>OrderBy</c> with ties is not a total order). Without a unique tiebreaker, a row can land on two
    /// pages or on neither as new data is written between one page request and the next. <c>Id</c> is
    /// unique, so appending it makes the order total and paging stable.
    ///
    /// STRING COLLATION: <c>Model</c>/<c>UserEmail</c> here sort under whatever collation the
    /// <c>LlmCalls</c> table/database uses (an ordinary Postgres <c>ORDER BY</c> - not chosen or
    /// controlled by this method). <c>List</c>'s operations-mode path sorts the same two columns in C#
    /// instead (forced by the EF/Npgsql limitation documented there), and deliberately pins that side to
    /// <c>StringComparer.Ordinal</c> rather than leaving it on <c>Comparer&lt;string&gt;.Default</c>
    /// (culture-aware) - not because ordinal matches Postgres's collation (it generally does not), but
    /// because the same column silently sorting under two DIFFERENT, undocumented rules depending on
    /// which <c>mode</c> was requested is its own trap, independent of what either rule actually is. Full
    /// collation parity between the two modes is not attempted here.</summary>
    private static IQueryable<LlmCall> OrderCalls(IQueryable<LlmCall> source, string column, bool desc)
    {
        IOrderedQueryable<LlmCall> primary = column switch
        {
            nameof(LlmCall.DurationMs) => desc ? source.OrderByDescending(c => c.DurationMs) : source.OrderBy(c => c.DurationMs),
            nameof(LlmCall.PromptTokens) => desc ? source.OrderByDescending(c => c.PromptTokens) : source.OrderBy(c => c.PromptTokens),
            nameof(LlmCall.CompletionTokens) => desc ? source.OrderByDescending(c => c.CompletionTokens) : source.OrderBy(c => c.CompletionTokens),
            nameof(LlmCall.TotalTokens) => desc ? source.OrderByDescending(c => c.TotalTokens) : source.OrderBy(c => c.TotalTokens),
            nameof(LlmCall.Kind) => desc ? source.OrderByDescending(c => c.Kind) : source.OrderBy(c => c.Kind),
            nameof(LlmCall.Model) => desc ? source.OrderByDescending(c => c.Model) : source.OrderBy(c => c.Model),
            nameof(LlmCall.UserEmail) => desc ? source.OrderByDescending(c => c.UserEmail) : source.OrderBy(c => c.UserEmail),
            _ /* StartedAt */ => desc ? source.OrderByDescending(c => c.StartedAt) : source.OrderBy(c => c.StartedAt),
        };
        return desc ? primary.ThenByDescending(c => c.Id) : primary.ThenBy(c => c.Id);
    }
}
