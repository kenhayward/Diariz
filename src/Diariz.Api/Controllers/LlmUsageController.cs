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

        // ONE round trip fetches EVERY operation matching the filter, UNORDERED - not paged here. This
        // is not the preferred design; it is where three failed attempts against real Postgres (all
        // recorded in task-3-report.md's fix-round-1 section, since the next person should not repeat
        // them) landed:
        //   1. Ordering the raw IQueryable<IGrouping<OperationKey, LlmCall>> directly translated fine for
        //      a single OrderBy, but adding a ThenBy(g => g.Key.OperationId) tiebreaker made the WHOLE
        //      query untranslatable, for every one of the eight whitelisted sort keys.
        //   2. Projecting into LlmUsageOperationRow (a named record) FIRST, then ordering the projected
        //      IQueryable<LlmUsageOperationRow>, ALSO failed to translate.
        //   3. Projecting into an ANONYMOUS type instead (which EF normally has first-class support for
        //      ordering by, unlike a named record) and ordering that with a ThenBy chain still failed;
        //      collapsing the two keys into a SINGLE OrderBy call on a composite anonymous tuple - still
        //      one InvalidOperationException. A narrowed, two-column-only version of the same projection
        //      failed identically, ruling out "the aggregate-heavy SELECT list is too wide" as the cause.
        // Every attempt hit the same InvalidOperationException: EF Core/Npgsql (this stack's versions)
        // cannot translate ANY second ORDER BY key - aggregate or plain column, ThenBy or composite -
        // once the query's SELECT list contains a GroupBy-sourced aggregate. A single-key ORDER BY over
        // the same shape translates without issue (proven by every calls-mode test, and by every
        // operations-mode test before the tiebreaker fix was added), so this is specifically a multi-key
        // ordering limitation, not a GroupBy-translation problem in general.
        //
        // The correctness requirement (a deterministic final tiebreaker - see OrderCalls's doc comment)
        // does not go away because Postgres cannot compute it, so sorting AND the tiebreak both happen in
        // C#, in memory, below - on every operation matching the filter, not just the requested page,
        // because there is no way to ask Postgres for a correctly tie-broken WINDOW when it cannot
        // compute the tie-broken order at all. This is bounded by how many distinct operations match the
        // filter (the default 30-day window caps that for a realistic deployment) rather than by
        // LlmCalls itself, but it is a real, deliberate trade forced by the translation limitation above,
        // not a preference - a future EF/Npgsql upgrade that resolves the underlying limitation should
        // revisit this in favour of true server-side paging.
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
                PromptTokens = g.Sum(c => (long?)c.PromptTokens),
                CompletionTokens = g.Sum(c => (long?)c.CompletionTokens),
                ReasoningTokens = g.Sum(c => (long?)c.ReasoningTokens),
                TotalTokens = g.Sum(c => (long?)c.TotalTokens),
                Success = g.All(c => c.Success),
            })
            .ToListAsync(ct);

        // The full filtered set is already in hand, so this IS the exact group count - no extra COUNT
        // round trip needed, and (unlike totals.Operations) it is exact even when Model varies within an
        // OperationId (see the OperationKey doc comment): each split sub-group is its own entry here.
        var operationsTotal = candidates.Count;

        // Primary key first, then OperationId, then Model - not just OperationId. OperationId alone is
        // NOT guaranteed unique per candidate row: the same OperationId splits into more than one row
        // when Model varies within it (see the OperationKey doc comment), and (OperationId, Model)
        // together IS unique per row, because Model is the only one of the nine key fields that can vary
        // within an OperationId. Without this the sort would still have unresolved ties on exactly the
        // scenario the exact-count fix above exists for.
        IOrderedEnumerable<T> Rank<T, TKey>(IEnumerable<T> source, Func<T, TKey> primary, Func<T, Guid> operationId, Func<T, string> model) =>
            desc
                ? source.OrderByDescending(primary).ThenByDescending(operationId).ThenByDescending(model)
                : source.OrderBy(primary).ThenBy(operationId).ThenBy(model);

        var rankedCandidates = sortColumn switch
        {
            nameof(LlmCall.DurationMs) => Rank(candidates, x => x.CompletedAt - x.StartedAt, x => x.OperationId, x => x.Model),
            nameof(LlmCall.PromptTokens) => Rank(candidates, x => x.PromptTokens, x => x.OperationId, x => x.Model),
            nameof(LlmCall.CompletionTokens) => Rank(candidates, x => x.CompletionTokens, x => x.OperationId, x => x.Model),
            nameof(LlmCall.TotalTokens) => Rank(candidates, x => x.TotalTokens, x => x.OperationId, x => x.Model),
            nameof(LlmCall.Kind) => Rank(candidates, x => x.Kind, x => x.OperationId, x => x.Model),
            nameof(LlmCall.Model) => Rank(candidates, x => x.Model, x => x.OperationId, x => x.Model),
            nameof(LlmCall.UserEmail) => Rank(candidates, x => x.UserEmail, x => x.OperationId, x => x.Model),
            _ /* StartedAt */ => Rank(candidates, x => x.StartedAt, x => x.OperationId, x => x.Model),
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

    /// <summary>Applies the requested sort, then a final <c>Id</c> tiebreaker. Every whitelisted column
    /// - including <c>StartedAt</c>, which <see cref="LlmCallScope"/>'s own doc notes can tie under
    /// concurrent fan-out - is non-unique, and Postgres gives no guarantee that tied rows come back in
    /// the same relative order across two separately executed, separately paginated queries (a plain
    /// <c>OrderBy</c> with ties is not a total order). Without a unique tiebreaker, a row can land on two
    /// pages or on neither as new data is written between one page request and the next. <c>Id</c> is
    /// unique, so appending it makes the order total and paging stable.</summary>
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
