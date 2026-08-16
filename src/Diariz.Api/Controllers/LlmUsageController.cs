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

    /// <summary>Composite key that is constant for every call in one operation - the fields are
    /// stamped once when the ambient LLM scope is pushed and copied onto every call made while that
    /// scope is active, so grouping by this tuple always yields exactly one row per
    /// <see cref="LlmCall.OperationId"/>. <see cref="LlmCall.OperationId"/> alone would already be a
    /// correct grouping key on its own (it's the thing this composite key is a superset of); the
    /// extra fields are carried through so they can be projected straight off <c>g.Key</c> without a
    /// second query.</summary>
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

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var filter = new LlmUsageFilter(from, to, userIds, kinds, models, outcome, recordingId, sectionId);
        var filtered = LlmUsageQuery.Apply(_db.LlmCalls.AsNoTracking(), filter, DateTimeOffset.UtcNow);

        // ONE round trip for the aggregate over the WHOLE filtered set - never derived from the page
        // fetched below. Both totals.Calls (COUNT(*)) and totals.Operations (COUNT(DISTINCT
        // OperationId)) are already exactly the "total" this endpoint needs for calls/operations mode
        // respectively, so "total" is read off this same result rather than issuing a second COUNT
        // query - see the OperationKey doc comment above for why COUNT(DISTINCT OperationId) equals
        // the number of operation groups.
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

        // Ordered on the grouping itself (aggregate expressions over each IGrouping<OperationKey, LlmCall>),
        // BEFORE projecting into LlmUsageOperationRow - keeps ORDER BY inside the same translated
        // GROUP BY query rather than asking EF to re-derive an aggregate from an already-shaped DTO.
        var orderedGroups = OrderOperationGroups(grouped, sortColumn, desc);

        var operationRows = await orderedGroups
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new LlmUsageOperationRow(
                g.Key.OperationId, g.Key.Kind, g.Key.UserId, g.Key.UserEmail,
                g.Key.RecordingId, g.Key.RecordingTitle, g.Key.SectionId, g.Key.SectionName, g.Key.Model,
                g.Count(),
                g.Min(c => c.StartedAt),
                g.Max(c => c.CompletedAt),
                g.Sum(c => (long?)c.PromptTokens),
                g.Sum(c => (long?)c.CompletionTokens),
                g.Sum(c => (long?)c.ReasoningTokens),
                g.Sum(c => (long?)c.TotalTokens),
                g.All(c => c.Success)))
            .ToListAsync(ct);
        return Ok(new LlmUsagePage<LlmUsageOperationRow>(operationRows, page, pageSize, totals.Operations, totals));
    }

    private static IQueryable<LlmCall> OrderCalls(IQueryable<LlmCall> source, string column, bool desc) =>
        column switch
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

    private static IQueryable<IGrouping<OperationKey, LlmCall>> OrderOperationGroups(
        IQueryable<IGrouping<OperationKey, LlmCall>> source, string column, bool desc) =>
        column switch
        {
            nameof(LlmCall.DurationMs) => desc
                ? source.OrderByDescending(g => g.Max(c => c.CompletedAt) - g.Min(c => c.StartedAt))
                : source.OrderBy(g => g.Max(c => c.CompletedAt) - g.Min(c => c.StartedAt)),
            nameof(LlmCall.PromptTokens) => desc
                ? source.OrderByDescending(g => g.Sum(c => (long?)c.PromptTokens))
                : source.OrderBy(g => g.Sum(c => (long?)c.PromptTokens)),
            nameof(LlmCall.CompletionTokens) => desc
                ? source.OrderByDescending(g => g.Sum(c => (long?)c.CompletionTokens))
                : source.OrderBy(g => g.Sum(c => (long?)c.CompletionTokens)),
            nameof(LlmCall.TotalTokens) => desc
                ? source.OrderByDescending(g => g.Sum(c => (long?)c.TotalTokens))
                : source.OrderBy(g => g.Sum(c => (long?)c.TotalTokens)),
            nameof(LlmCall.Kind) => desc ? source.OrderByDescending(g => g.Key.Kind) : source.OrderBy(g => g.Key.Kind),
            nameof(LlmCall.Model) => desc ? source.OrderByDescending(g => g.Key.Model) : source.OrderBy(g => g.Key.Model),
            nameof(LlmCall.UserEmail) => desc ? source.OrderByDescending(g => g.Key.UserEmail) : source.OrderBy(g => g.Key.UserEmail),
            _ /* StartedAt */ => desc
                ? source.OrderByDescending(g => g.Min(c => c.StartedAt))
                : source.OrderBy(g => g.Min(c => c.StartedAt)),
        };
}
