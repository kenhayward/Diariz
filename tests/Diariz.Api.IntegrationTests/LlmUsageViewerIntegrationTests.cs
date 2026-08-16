using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>Covers <see cref="LlmUsageQuery.TotalsAsync"/> against real Postgres - aggregation is
/// exactly what the EF in-memory provider does not translate faithfully (GROUP BY / SUM / COUNT
/// DISTINCT), so a unit test here would prove nothing.
///
/// Isolation: every test filters by <c>Model == marker.ToString()</c>, and <see cref="Row"/> always
/// stamps that marker onto <c>Model</c>. <c>OperationId</c> defaults to the same marker but can be
/// overridden - it has to be, since <see cref="Totals_CountsOperationsDistinctly_AndFailures"/> is
/// specifically about distinct <c>OperationId</c> values, so it can't also be the isolation key.
/// Filtering on <c>Model</c> instead keeps one consistent isolation mechanism across every test in
/// this file (all tests in this shared "integration" collection run sequentially against one
/// database, so an unscoped totals query would go flaky the moment another test seeds an
/// <c>LlmCall</c>).</summary>
[Collection(IntegrationCollection.Name)]
public class LlmUsageViewerIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task Totals_SumIgnoresNulls_AndReportsHowManyCallsWereMeasured()
    {
        // A model that reports no usage must not be counted as having emitted 0 tokens - that would
        // silently deflate every average an administrator reads.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, durationMs: 1000, completionTokens: 100, totalTokens: 150),
            Row(marker, durationMs: 1000, completionTokens: 300, totalTokens: 450),
            Row(marker, durationMs: 1000, completionTokens: null, totalTokens: null));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(3, totals.Calls);
        Assert.Equal(400, totals.CompletionTokens);      // not 400/3 averaged, and the null is not a 0
        Assert.Equal(2, totals.TokenMeasuredCalls);      // "measured on 2 of 3"
    }

    [Fact]
    public async Task Totals_TokensPerSecond_IsSumOverSum_NotAnAverageOfRates()
    {
        // One tiny fast call and one large slow one. Averaging the per-row rates gives ~37.5/s;
        // the correct aggregate is 4003 tokens over 10.04s = ~399/s. This is the assertion that
        // stops the cheap implementation.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, durationMs: 40, completionTokens: 3),
            Row(marker, durationMs: 10_000, completionTokens: 4000));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.NotNull(totals.TokensPerSecond);
        Assert.InRange(totals.TokensPerSecond!.Value, 395, 403);
    }

    [Fact]
    public async Task Totals_CountsOperationsDistinctly_AndFailures()
    {
        // Operations is COUNT(DISTINCT OperationId), not COUNT(*) - a three-round-trip chat turn is
        // one operation, and reporting it as three would overstate usage threefold.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var turn = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: turn, sequence: 1),
            Row(marker, operationId: turn, sequence: 2),
            Row(marker, operationId: Guid.NewGuid(), success: false));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(3, totals.Calls);
        Assert.Equal(2, totals.Operations);
        Assert.Equal(1, totals.FailedCalls);
    }

    [Fact]
    public async Task Totals_WhenNoRowInTheSetReportsTokens_SumsAreNullNotZero()
    {
        // Distinct from the "sum ignores nulls" test above: there, 2 of 3 rows had real values, so the
        // SQL SUM was non-null regardless of how the "all inputs null" case is handled. Here every row
        // is null, which is exactly the case where EF's default LINQ translation of a nullable Sum
        // (COALESCE(sum(x), 0)) silently produces 0 instead of NULL - the "nulls are not zeros" rule
        // has to hold even when NOTHING in the set was measured, not only when some rows were.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, durationMs: 1000),
            Row(marker, durationMs: 1000));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(2, totals.Calls);
        Assert.Equal(0, totals.TokenMeasuredCalls);
        Assert.Null(totals.PromptTokens);
        Assert.Null(totals.CompletionTokens);
        Assert.Null(totals.ReasoningTokens);
        Assert.Null(totals.TotalTokens);
        Assert.Null(totals.TokensPerSecond);
    }

    [Fact]
    public async Task Totals_TokenMeasuredCalls_CountsAnyColumnMeasured_NotJustCompletion()
    {
        // Discriminates the two possible definitions of TokenMeasuredCalls: "calls with a completion-
        // token count specifically" vs "calls that reported ANY token count at all" (prompt, completion,
        // reasoning, or total). This row reports PromptTokens but NOT CompletionTokens, so the two
        // definitions disagree - under the (rejected) completion-only binding this would read 0; the
        // chosen any-column binding reads 1, because the admin got *some* usage data for this call.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker, promptTokens: 42, completionTokens: null));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(1, totals.Calls);
        Assert.Equal(1, totals.TokenMeasuredCalls);
    }

    [Fact]
    public async Task Totals_WhenDurationSumsToZero_TokensPerSecondIsNull()
    {
        // TokensPerSecond's other guard: even with a real completion-token count, dividing by a zero
        // summed duration must not raw-divide (that produces double.PositiveInfinity, which is not
        // valid JSON and would break the page) - it must return null, same as the "nothing measured"
        // case above.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker, durationMs: 0, completionTokens: 500));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(1, totals.Calls);
        Assert.Equal(500, totals.CompletionTokens);
        Assert.Null(totals.TokensPerSecond);
    }

    [Fact]
    public async Task Totals_OfAnEmptySet_AreZeroAndNull_NotAnError()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid(); // fresh marker: guaranteed to match no row anyone else seeded

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(0, totals.Calls);
        Assert.Null(totals.TokensPerSecond); // not NaN, not a divide-by-zero
    }

    // ---- LlmUsageController: mode=operations/calls, paging, sort validation ----
    //
    // Same isolation convention as the totals tests above: every call is filtered to
    // models: [marker.ToString()], so it can never see another test's rows even though all tests in
    // this collection share one Postgres database.

    private static LlmUsageController Build(DiarizDbContext db) =>
        new(db) { ControllerContext = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]) };

    private static T OkValue<T>(IActionResult result) =>
        Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task OperationsMode_CollapsesToOneRow_ButCallsMode_ReturnsEveryCall()
    {
        // Three calls belonging to one operation: one row with turns == 3 and summed tokens in
        // operations mode (the default), three rows in calls mode.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: operationId, sequence: 1, completionTokens: 100),
            Row(marker, operationId: operationId, sequence: 2, completionTokens: 200),
            Row(marker, operationId: operationId, sequence: 3, completionTokens: 300));
        await db.SaveChangesAsync();

        var opsPage = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: [marker.ToString()]));
        var opRow = Assert.Single(opsPage.Rows);
        Assert.Equal(3, opRow.Turns);
        Assert.Equal(600, opRow.CompletionTokens);
        Assert.Equal(1, opsPage.Total);

        var callsPage = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: [marker.ToString()]));
        Assert.Equal(3, callsPage.Rows.Count);
        Assert.Equal(3, callsPage.Total);
    }

    [Fact]
    public async Task PageSize_IsCappedAt200_EvenWhenTheCallerAsksForMore()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker));
        await db.SaveChangesAsync();

        var page = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: [marker.ToString()], pageSize: 10_000));

        Assert.Equal(LlmUsageController.MaxPageSize, page.PageSize);
    }

    [Fact]
    public async Task UnrecognisedSort_IsRejectedWith400_NotSilentlyIgnored()
    {
        // Silently ignoring it would show the administrator data ordered differently from what they
        // asked for, which is worse than an error.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();

        var result = await Build(db).List(mode: "calls", models: [marker.ToString()], sort: "notARealColumn");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Totals_CoverTheWholeFilter_NotJustTheReturnedPage()
    {
        // The requirement most likely to be implemented wrongly: summing the rows on screen is easy
        // and looks right until the second page.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(Row(marker), Row(marker), Row(marker), Row(marker), Row(marker));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: [marker.ToString()], pageSize: 2));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(5, result.Total);
        Assert.Equal(5, result.Totals.Calls);
    }

    [Fact]
    public async Task OperationsMode_Total_CoversTheWholeFilter_NotJustTheReturnedPage()
    {
        // Mirrors the calls-mode test above, but in operations mode - the DEFAULT the user sees first.
        // OperationsMode_CollapsesToOneRow_ButCallsMode_ReturnsEveryCall alone cannot catch a "total
        // derived from the page" bug here: it seeds a single operation, so Total == 1 would pass
        // identically whether or not Total was computed correctly. Five separate operations with a
        // pageSize smaller than the operation count is what actually exercises this.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: Guid.NewGuid()),
            Row(marker, operationId: Guid.NewGuid()),
            Row(marker, operationId: Guid.NewGuid()),
            Row(marker, operationId: Guid.NewGuid()),
            Row(marker, operationId: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: [marker.ToString()], pageSize: 2));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(5, result.Total);
    }

    [Fact]
    public async Task OperationsMode_SplitsIntoSeparateRows_WhenModelDiffersWithinOneOperationId()
    {
        // LlmCallScope (the "ambient scope" the OperationKey doc comment describes) has NO Model
        // property - Model is parsed per call, out of that specific call's own request body, entirely
        // independent of the scope. So two calls sharing an OperationId CAN carry different Model
        // values; nothing enforces otherwise, it just happens to be true for every current call site.
        // That means COUNT(DISTINCT OperationId) - which is all totals.Operations computes - is only a
        // LOWER BOUND on the number of operation rows this grouping actually produces: it would report
        // 1 here, but grouping by the real composite key (which includes Model) produces 2 rows for
        // this one OperationId. This is the scenario the exact grouped-count fix exists for.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var modelB = $"{marker}-b";
        db.LlmCalls.AddRange(
            Row(marker, operationId: operationId, sequence: 1, model: modelA),
            Row(marker, operationId: operationId, sequence: 2, model: modelB));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: [modelA, modelB]));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Total);
        // The old (wrong) behaviour is captured here too: totals.Operations alone would say 1 - it
        // still means "distinct OperationIds", which is a real and correct number, just not the same
        // number as "how many operation rows come back". That's exactly why Total is no longer read
        // off it for operations mode.
        Assert.Equal(1, result.Totals.Operations);
    }

    [Fact]
    public async Task OperationsMode_TokenSums_AreNullNotZero_WhenNothingWasMeasured()
    {
        // The same trap LlmUsageQuery.TotalsAsync goes out of its way to defuse for the aggregate totals
        // bar exists one level down, per row, here too: g.Sum(c => (long?)c.PromptTokens) translates to
        // COALESCE(sum(x), 0), so an operation whose calls reported no usage at all would otherwise
        // display "0 tokens" - indistinguishable from a model that genuinely used zero tokens - instead
        // of "not measured".
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: operationId, sequence: 1),
            Row(marker, operationId: operationId, sequence: 2));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: [marker.ToString()]));

        var row = Assert.Single(result.Rows);
        Assert.Null(row.PromptTokens);
        Assert.Null(row.CompletionTokens);
        Assert.Null(row.ReasoningTokens);
        Assert.Null(row.TotalTokens);
    }

    [Fact]
    public async Task OperationsMode_TokenSums_IgnoreNulls_WhenSomeCallsWereMeasured()
    {
        // The companion case: one call in the operation reports usage, the other does not - the sum
        // must reflect only the measured call (100), not be nulled out just because SOME row in the
        // group was unmeasured, and not silently treat the unmeasured row's null as a 0 contribution
        // (which COALESCE-to-0 would already get right for the raw sum - this asserts the corrected
        // value is still exactly the measured contribution, not coincidentally right for the wrong
        // reason).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: operationId, sequence: 1, completionTokens: 100),
            Row(marker, operationId: operationId, sequence: 2));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: [marker.ToString()]));

        var row = Assert.Single(result.Rows);
        Assert.Equal(100, row.CompletionTokens);
    }

    [Fact]
    public async Task OperationsMode_ExceedingTheOperationCeiling_Returns400_NotATruncatedPage()
    {
        // MaxOperationsPerRequest is a lower-bound guard (totals.Operations is COUNT(DISTINCT
        // OperationId), always <= the real group count) - this seeds MORE distinct OperationIds than
        // the ceiling to trip it without needing anywhere near a realistic MaxOperationsPerRequest-sized
        // seed. Asserts an explicit 400, not a silently truncated/wrong page - truncating would make
        // Total wrong and rows unreachable, the same failure class Finding 1 closed a different way in.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        const int overCeiling = LlmUsageController.MaxOperationsPerRequest + 1;
        db.LlmCalls.AddRange(
            Enumerable.Range(0, overCeiling).Select(_ => Row(marker, operationId: Guid.NewGuid())));
        await db.SaveChangesAsync();

        var result = await Build(db).List(mode: "operations", models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Paging_ThroughOperationsMode_CoversEveryOperationExactlyOnce()
    {
        // Finding 6(b): OperationsMode_Total_CoversTheWholeFilter_NotJustTheReturnedPage proves Total
        // isn't page-derived, but nothing exercised the Skip/Take arithmetic over the C#-materialized,
        // ranked candidate list itself (see LlmUsageController.List's operations-mode path) - a wrong
        // Skip/Take there would silently drop or repeat whole operations across pages. Mirrors
        // Paging_ThroughTiedSortValues_CoversEveryRowExactlyOnce_ViaCallsMode's shape for operations mode.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var operationIds = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        db.LlmCalls.AddRange(operationIds.Select(id => Row(marker, operationId: id)));
        await db.SaveChangesAsync();

        var collected = new HashSet<Guid>();
        for (var page = 1; page <= 3; page++) // ceil(7 / 3) == 3 pages
        {
            var result = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
                await Build(db).List(mode: "operations", models: [marker.ToString()], pageSize: 3, page: page));
            foreach (var row in result.Rows)
                Assert.True(collected.Add(row.OperationId), $"Operation {row.OperationId} was returned on more than one page.");
        }

        Assert.Equal(operationIds.ToHashSet(), collected);
    }

    // ---- Every whitelisted sort key, against real Postgres, in both modes (finding 3) ----
    //
    // Three rows/operations are seeded with every sortable dimension increasing together with the same
    // index i in [0, 1, 2] - StartedAt, DurationMs (calls mode) / the CompletedAt-StartedAt span
    // (operations mode), PromptTokens, CompletionTokens, TotalTokens, Kind, Model and UserEmail are all
    // ascending in lockstep - so sorting ascending by ANY whitelisted column must return them in the
    // same [0, 1, 2] order, and descending must reverse it. Each row is its own OperationId (one call
    // per operation) - used as-is by the calls-mode theory test below. The operations-mode theory test
    // uses BuildOrderableTripleWithAMultiCallOperation instead: with only one call per operation here,
    // every aggregate (MIN/MAX/SUM) would be an identity on that single row's own value, so a bug that
    // sorted by an arbitrary member call's raw column instead of the true aggregate would still pass.
    private static LlmCall[] BuildOrderableTriple(Guid marker, out string[] models)
    {
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var modelValues = Enumerable.Range(0, 3).Select(i => $"{marker}-{i}").ToArray();
        models = modelValues;
        var rows = Enumerable.Range(0, 3).Select(i => new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = (LlmCallKind)i, // Unknown(0) < Summarize(1) < SectionSummary(2)
            UserEmail = $"{marker}-u{i}@x.test", // lexically ascending: single-digit i
            Model = modelValues[i], // lexically ascending: single-digit i
            Endpoint = "http://x/v1",
            StartedAt = baseTime.AddSeconds(i),
            CompletedAt = baseTime.AddSeconds(i).AddMilliseconds((i + 1) * 100), // ascending span
            DurationMs = (i + 1) * 1000,
            PromptTokens = (i + 1) * 10,
            CompletionTokens = (i + 1) * 20,
            TotalTokens = (i + 1) * 30,
            Success = true,
        }).ToArray();
        return rows;
    }

    // Operations mode needs its own variant on top of BuildOrderableTriple: with one call per operation,
    // every MIN/MAX/SUM aggregate is an identity on that single row's own value, so a bug that sorted by
    // an arbitrary member call's raw column instead of the true aggregate would still pass. This gives
    // operation 0 a SECOND call, positioned so its aggregates are genuine multi-row computations (a real
    // MIN, a real MAX, a real SUM - not equal to either individual call's own value) while every ordering
    // relationship between operations 0/1/2 established by BuildOrderableTriple is preserved: operation
    // 0's span (110ms) stays below operation 1's (200ms), and its token sums (11/21/31) stay below
    // operation 1's (20/40/60).
    private static LlmCall[] BuildOrderableTripleWithAMultiCallOperation(Guid marker, out string[] models)
    {
        var baseTriple = BuildOrderableTriple(marker, out models);
        var first = baseTriple[0];
        var extra = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = first.OperationId,
            Sequence = 2,
            Kind = first.Kind,
            UserEmail = first.UserEmail,
            Model = first.Model,
            Endpoint = "http://x/v1",
            StartedAt = first.StartedAt.AddMilliseconds(-10), // pulls the group's MIN earlier than either row alone
            CompletedAt = first.CompletedAt.AddMilliseconds(10), // pushes the group's MAX later than either row alone
            DurationMs = 1,
            PromptTokens = 1,
            CompletionTokens = 1,
            TotalTokens = 1,
            Success = true,
        };
        return [.. baseTriple, extra];
    }

    public static IEnumerable<object[]> AllWhitelistedSortKeys() =>
        new[] { "startedAt", "durationMs", "promptTokens", "completionTokens", "totalTokens", "kind", "model", "userEmail" }
            .Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllWhitelistedSortKeys))]
    public async Task EveryWhitelistedSortKey_OrdersCallsMode_AscendingAndDescending(string sortKey)
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var rows = BuildOrderableTriple(marker, out var models);
        db.LlmCalls.AddRange(rows);
        await db.SaveChangesAsync();
        var expectedAscending = rows.Select(r => r.Id).ToArray();

        var asc = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: models, sort: sortKey, desc: false));
        Assert.Equal(expectedAscending, asc.Rows.Select(r => r.Id).ToArray());

        var desc = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: models, sort: sortKey, desc: true));
        Assert.Equal(expectedAscending.Reverse(), desc.Rows.Select(r => r.Id).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllWhitelistedSortKeys))]
    public async Task EveryWhitelistedSortKey_OrdersOperationsMode_AscendingAndDescending(string sortKey)
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var rows = BuildOrderableTripleWithAMultiCallOperation(marker, out var models);
        db.LlmCalls.AddRange(rows);
        await db.SaveChangesAsync();
        // Distinct, order-preserving: operation 0 now has two LlmCalls rows (same OperationId), so a
        // plain Select would list it twice.
        var expectedAscending = rows.Select(r => r.OperationId).Distinct().ToArray();

        var asc = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: models, sort: sortKey, desc: false));
        Assert.Equal(expectedAscending, asc.Rows.Select(r => r.OperationId).ToArray());

        var desc = OkValue<LlmUsagePage<LlmUsageOperationRow>>(
            await Build(db).List(mode: "operations", models: models, sort: sortKey, desc: true));
        Assert.Equal(expectedAscending.Reverse(), desc.Rows.Select(r => r.OperationId).ToArray());
    }

    // ---- Deterministic tiebreak so paging can't lose or duplicate rows (finding 4) ----

    [Fact]
    public async Task Paging_ThroughTiedSortValues_CoversEveryRowExactlyOnce_ViaCallsMode()
    {
        // WHAT THIS TEST DOES AND DOES NOT PROVE (read before "simplifying" this away). Every row shares
        // the same StartedAt (the default sort column) - the exact scenario LlmCallScope's own doc warns
        // can happen under concurrent fan-out - and even adds a write between page fetches to try to
        // force Postgres to reorder the tied rows physically. Despite that, removing OrderCalls's Id
        // tiebreaker (verified directly: see task-3-report.md's fix-round-1 section) does NOT make this
        // test fail in this environment - this specific Testcontainers Postgres instance returns tied
        // rows in a stable order across separately executed queries for a table this small, with or
        // without the tiebreaker. So this test does NOT demonstrate that the tiebreaker is necessary; do
        // not read a future green run of this test as evidence the tiebreaker can be removed.
        //
        // The tiebreaker's necessity rests on the SQL standard instead: `ORDER BY <non-unique column>
        // LIMIT/OFFSET` has no ordering guarantee across ties, independent of whether one specific
        // Postgres version/table size happens to exhibit the failure today. What this test DOES
        // genuinely guard, and would fail if broken, is paging completeness in general - a Skip/Take
        // off-by-one, a page-index regression, or a filter leaking across pages would all surface here as
        // a row appearing twice or not at all.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var tiedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var seeded = Enumerable.Range(0, 9)
            .Select(_ => new LlmCall
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                Sequence = 1,
                Kind = LlmCallKind.Tags,
                Model = marker.ToString(),
                Endpoint = "http://x/v1",
                StartedAt = tiedAt,
                CompletedAt = tiedAt,
                DurationMs = 1000,
                Success = true,
            })
            .ToArray();
        db.LlmCalls.AddRange(seeded);
        await db.SaveChangesAsync();
        var expectedIds = seeded.Select(r => r.Id).ToHashSet();

        var collected = new HashSet<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            // A fresh DbContext per page - the same as two separate HTTP requests hitting the endpoint
            // at different times, which is the real scenario this tiebreaker protects.
            await using var pageDb = fx.CreateDbContext();
            var result = OkValue<LlmUsagePage<LlmUsageCallRow>>(
                await Build(pageDb).List(mode: "calls", models: [marker.ToString()], pageSize: 3, page: page));
            foreach (var row in result.Rows)
                Assert.True(collected.Add(row.Id), $"Row {row.Id} was returned on more than one page.");

            // Perturb the physical storage of the rows NOT yet fetched, between page requests - an
            // UPDATE always writes a new tuple version in Postgres, which can change a row's position in
            // a plain sequential scan. This simulates the real scenario the tiebreaker protects against
            // (data changing between one page request and the next) rather than relying on incidental
            // physical stability of an untouched table, which a small, single-session integration test
            // would otherwise not disturb at all.
            if (page < 3)
            {
                await using var writeDb = fx.CreateDbContext();
                var notYetFetched = await writeDb.LlmCalls
                    .Where(c => c.Model == marker.ToString() && !collected.Contains(c.Id))
                    .ToListAsync();
                foreach (var row in notYetFetched) row.Endpoint = $"http://x/v1?touched={page}";
                await writeDb.SaveChangesAsync();
            }
        }

        Assert.Equal(expectedIds, collected);
    }

    private static LlmCall Row(
        Guid marker,
        int durationMs = 1000,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        Guid? operationId = null,
        int sequence = 1,
        bool success = true,
        string? model = null,
        Guid? recordingId = null,
        Guid? sectionId = null) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId ?? marker,
        Sequence = sequence,
        Kind = LlmCallKind.Tags,
        Model = model ?? marker.ToString(),
        Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        TotalTokens = totalTokens,
        Success = success,
        RecordingId = recordingId,
        SectionId = sectionId,
    };

    // ---- LlmUsageController.List: recordingId/sectionId filters (Task 5) ----
    //
    // Never tested anywhere before this task (Task 1's review flagged it and it was carried forward
    // here deliberately) - and the same two filters govern the destructive delete path below, so both
    // read and delete get dedicated coverage. RecordingId/SectionId are themselves fresh Guids per
    // test, which is isolation enough on its own (no collision risk with any other test's rows) - no
    // Model marker needed on top, though one is included anyway for consistency with the rest of the
    // file.
    //
    // Real Postgres enforces the LlmCalls -> Recordings/Sections FK (ON DELETE SET NULL, per
    // LlmCall's own doc comment), unlike the in-memory provider used elsewhere in this repo's unit
    // tests - so a real Recording/Section (and the user/room they in turn require) has to be seeded,
    // not just a bare Guid.

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedRecording()
    {
        var userId = await SeedUser();
        await using var db = fx.CreateDbContext();
        var recording = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}" };
        db.Recordings.Add(recording);
        await db.SaveChangesAsync();
        return recording.Id;
    }

    private async Task<Guid> SeedSection()
    {
        var userId = await SeedUser();
        await using var db = fx.CreateDbContext();
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "F" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return section.Id;
    }

    [Fact]
    public async Task List_FiltersByRecordingId_ReturnsOnlyThatRecordingsRows()
    {
        var recordingId = await SeedRecording();
        var otherRecordingId = await SeedRecording();
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, recordingId: recordingId),
            Row(marker, recordingId: otherRecordingId),
            Row(marker));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: [marker.ToString()], recordingId: recordingId));

        var row = Assert.Single(result.Rows);
        Assert.Equal(recordingId, row.RecordingId);
    }

    [Fact]
    public async Task List_FiltersBySectionId_ReturnsOnlyThatSectionsRows()
    {
        var sectionId = await SeedSection();
        var otherSectionId = await SeedSection();
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, sectionId: sectionId),
            Row(marker, sectionId: otherSectionId),
            Row(marker));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsagePage<LlmUsageCallRow>>(
            await Build(db).List(mode: "calls", models: [marker.ToString()], sectionId: sectionId));

        var row = Assert.Single(result.Rows);
        Assert.Equal(sectionId, row.SectionId);
    }

    // ---- LlmUsageController.Summary: roll-up grouped by user/model/kind (Task 4) ----
    //
    // Same isolation convention as the rest of this file: every seeded row carries Model ==
    // marker.ToString() (or a marker-prefixed variant when the test itself groups by Model), and every
    // call filters models: [...] down to exactly the markers it seeded.

    private static LlmCallKind[] Kinds => [LlmCallKind.Tags, LlmCallKind.ChatMessage, LlmCallKind.Summarize];

    private static LlmCall SummaryRow(
        Guid marker,
        Guid? operationId = null,
        int sequence = 1,
        Guid? userId = null,
        string? userEmail = null,
        string? model = null,
        LlmCallKind kind = LlmCallKind.Tags,
        int durationMs = 1000,
        int? promptTokens = null,
        int? completionTokens = null,
        int? reasoningTokens = null,
        int? totalTokens = null,
        bool success = true) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId ?? Guid.NewGuid(),
        Sequence = sequence,
        Kind = kind,
        UserId = userId,
        UserEmail = userEmail ?? "u@x.test",
        Model = model ?? marker.ToString(),
        Endpoint = "http://x/v1",
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        ReasoningTokens = reasoningTokens,
        TotalTokens = totalTokens,
        Success = success,
    };

    [Fact]
    public async Task Summary_GroupsByOneDimension_ReportsPerGroupCallsOperationsAndTokens()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var modelB = $"{marker}-b";
        db.LlmCalls.AddRange(
            SummaryRow(marker, model: modelA, durationMs: 1000, completionTokens: 100), // op 1
            SummaryRow(marker, model: modelA, durationMs: 1000, completionTokens: 300), // op 2
            SummaryRow(marker, model: modelB, durationMs: 500, completionTokens: 50));  // op 3
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [modelA, modelB]));

        Assert.Equal(2, result.Groups.Count);
        var groupA = result.Groups.Single(g => g.Model == modelA);
        Assert.Equal(2, groupA.Calls);
        Assert.Equal(2, groupA.Operations);
        Assert.Equal(400, groupA.CompletionTokens);
        Assert.NotNull(groupA.TokensPerSecond);
        Assert.InRange(groupA.TokensPerSecond!.Value, 199, 201); // 400 tokens / 2.0s

        var groupB = result.Groups.Single(g => g.Model == modelB);
        Assert.Equal(1, groupB.Calls);
        Assert.Equal(1, groupB.Operations);
        Assert.Equal(50, groupB.CompletionTokens);
        Assert.InRange(groupB.TokensPerSecond!.Value, 99, 101); // 50 tokens / 0.5s

        // The un-grouped dimensions are null on every row - a dimension not asked for collapses.
        Assert.All(result.Groups, g => Assert.Null(g.UserId));
        Assert.All(result.Groups, g => Assert.Null(g.Kind));
    }

    [Fact]
    public async Task Summary_GroupsByTwoDimensionsCombined_ProducesOneRowPerCombination()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var modelB = $"{marker}-b";
        db.LlmCalls.AddRange(
            SummaryRow(marker, model: modelA, kind: LlmCallKind.Tags),
            SummaryRow(marker, model: modelA, kind: LlmCallKind.ChatMessage),
            SummaryRow(marker, model: modelB, kind: LlmCallKind.Tags));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model,kind", models: [modelA, modelB]));

        Assert.Equal(3, result.Groups.Count);
        Assert.Contains(result.Groups, g => g.Model == modelA && g.Kind == LlmCallKind.Tags);
        Assert.Contains(result.Groups, g => g.Model == modelA && g.Kind == LlmCallKind.ChatMessage);
        Assert.Contains(result.Groups, g => g.Model == modelB && g.Kind == LlmCallKind.Tags);
        Assert.All(result.Groups, g => Assert.Equal(1, g.Calls));
    }

    [Fact]
    public async Task Summary_UnknownGroupBy_IsRejectedWith400_NotSilentlyIgnored()
    {
        // Same whitelist discipline as sort/mode - silently ignoring an unrecognised grouping would show
        // the administrator a different report from the one they asked for.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();

        var result = await Build(db).Summary(groupBy: "notARealDimension", models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Summary_MissingGroupBy_IsRejectedWith400()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();

        var result = await Build(db).Summary(groupBy: null, models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Summary_ReportsTurnsAsAveragePerOperation_NeverASum()
    {
        // Two operations in the same group: one with 2 calls, one with 5. Summing across operations
        // (2 + 5 = 7) is a meaningless number - the report must show an average (3.5) and a max (5).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var shortOp = Guid.NewGuid();
        var longOp = Guid.NewGuid();
        db.LlmCalls.AddRange(
            SummaryRow(marker, operationId: shortOp, sequence: 1),
            SummaryRow(marker, operationId: shortOp, sequence: 2),
            SummaryRow(marker, operationId: longOp, sequence: 1),
            SummaryRow(marker, operationId: longOp, sequence: 2),
            SummaryRow(marker, operationId: longOp, sequence: 3),
            SummaryRow(marker, operationId: longOp, sequence: 4),
            SummaryRow(marker, operationId: longOp, sequence: 5));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [marker.ToString()]));

        var group = Assert.Single(result.Groups);
        Assert.Equal(7, group.Calls);              // the row count, sum is fine here
        Assert.Equal(2, group.Operations);
        Assert.Equal(3.5, group.AverageTurnsPerOperation);
        Assert.Equal(5, group.MaxTurnsPerOperation);
        Assert.NotEqual(7, group.MaxTurnsPerOperation); // guards against a sum-of-turns regression
    }

    [Fact]
    public async Task Summary_TokensPerSecond_IsEachGroupsOwnSumOverSum()
    {
        // Two groups with genuinely different rates. A cheap implementation could pass a single-group
        // test either by averaging per-row rates OR by copying the overall Totals.TokensPerSecond onto
        // every group - this seeds two groups whose correct rates differ from EACH OTHER and from the
        // overall total, so both shortcuts are caught.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelFast = $"{marker}-fast"; // one tiny fast call, one large slow one: ~399 tokens/s
        var modelSlow = $"{marker}-slow"; // one call: exactly 100 tokens/s
        db.LlmCalls.AddRange(
            SummaryRow(marker, model: modelFast, durationMs: 40, completionTokens: 3),
            SummaryRow(marker, model: modelFast, durationMs: 10_000, completionTokens: 4000),
            SummaryRow(marker, model: modelSlow, durationMs: 1000, completionTokens: 100));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [modelFast, modelSlow]));

        var fast = result.Groups.Single(g => g.Model == modelFast);
        Assert.InRange(fast.TokensPerSecond!.Value, 395, 403);

        var slow = result.Groups.Single(g => g.Model == modelSlow);
        Assert.InRange(slow.TokensPerSecond!.Value, 99, 101);

        // The overall total (4103 tokens / 11.04s =~ 371.6/s) sits between the two - distinct from
        // either group's own rate, so a group silently inheriting Totals.TokensPerSecond would fail here.
        Assert.NotInRange(slow.TokensPerSecond!.Value, 370, 373);
    }

    [Fact]
    public async Task Summary_TokensPerSecond_IsNullNotNaNOrInfinity_WhenUnmeasuredOrDurationIsZero()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelUnmeasured = $"{marker}-unmeasured"; // no completion tokens reported
        var modelZeroDuration = $"{marker}-zero";       // completion tokens reported, but 0 duration
        db.LlmCalls.AddRange(
            SummaryRow(marker, model: modelUnmeasured, durationMs: 1000, completionTokens: null),
            SummaryRow(marker, model: modelZeroDuration, durationMs: 0, completionTokens: 500));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [modelUnmeasured, modelZeroDuration]));

        Assert.Null(result.Groups.Single(g => g.Model == modelUnmeasured).TokensPerSecond);
        Assert.Null(result.Groups.Single(g => g.Model == modelZeroDuration).TokensPerSecond);
    }

    [Fact]
    public async Task Summary_TotalsCoverTheWholeFilter_NotDerivedFromTheGroups()
    {
        // Same convention as the detail endpoint's Totals_CoverTheWholeFilter test - Totals here must be
        // LlmUsageQuery.TotalsAsync over the whole filtered set, not a fold over the returned Groups.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var modelB = $"{marker}-b";
        db.LlmCalls.AddRange(
            SummaryRow(marker, model: modelA, completionTokens: 10),
            SummaryRow(marker, model: modelB, completionTokens: 20),
            SummaryRow(marker, model: modelB, completionTokens: 30));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [modelA, modelB]));

        Assert.Equal(3, result.Totals.Calls);
        Assert.Equal(60, result.Totals.CompletionTokens);
    }

    [Fact]
    public async Task Summary_UsesTheSameFilterAsTheDetailView_RespectsOutcome()
    {
        // The mutation-check target (Step 5 of the task brief): Summary must build its scope through
        // LlmUsageQuery.Apply, the same primitive the detail endpoint uses, so the two views can never
        // silently disagree about what is in scope. This seeds a failed call that outcome="ok" must
        // exclude - if Summary ever grows its own hand-rolled filter and drops the outcome clause, this
        // is the test that catches it (see the task report for the mutation-check run).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            SummaryRow(marker, success: true, completionTokens: 100),
            SummaryRow(marker, success: false, completionTokens: 999));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "model", models: [marker.ToString()], outcome: "ok"));

        var group = Assert.Single(result.Groups);
        Assert.Equal(1, group.Calls);
        Assert.Equal(100, group.CompletionTokens);
    }

    // ---- Fix round 1: Finding 1 (UserEmail must not be part of the user grouping key) ----

    [Fact]
    public async Task Summary_GroupsByUser_CollapsesToOneGroup_EvenWhenUserEmailSnapshotDiffers()
    {
        // UserEmail is an explicitly denormalised SNAPSHOT (see LlmCall.UserEmail's own doc comment) -
        // its whole purpose is to survive the user being deleted. It must NOT be part of the grouping
        // key: two calls for the SAME UserId with two DIFFERENT UserEmail snapshots (e.g. the user
        // changed their email between the two calls) must still collapse into one group, the same way
        // Task 3 had to stop treating Model as safe to fold into every identity assumption. If UserEmail
        // is in the key, this silently splits one user's usage into two rows, each looking authoritative.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = "new@x.test" });
        db.LlmCalls.AddRange(
            SummaryRow(marker, userId: userId, userEmail: "old@x.test", completionTokens: 100),
            SummaryRow(marker, userId: userId, userEmail: "new@x.test", completionTokens: 200));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "user", models: [marker.ToString()]));

        var group = Assert.Single(result.Groups);
        Assert.Equal(userId, group.UserId);
        Assert.Equal(2, group.Calls);
        Assert.Equal(300, group.CompletionTokens);
    }

    // ---- Fix round 1: Finding 2 (groupBy=user / groupBy=kind / the three-way combination were untested) ----

    [Fact]
    public async Task Summary_GroupsByUserAlone_ReportsOneRowPerDistinctUser()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        db.Users.AddRange(
            new ApplicationUser { Id = userA, UserName = $"{userA}@x.test", Email = "a@x.test" },
            new ApplicationUser { Id = userB, UserName = $"{userB}@x.test", Email = "b@x.test" });
        db.LlmCalls.AddRange(
            SummaryRow(marker, userId: userA, userEmail: "a@x.test", completionTokens: 10),
            SummaryRow(marker, userId: userA, userEmail: "a@x.test", completionTokens: 20),
            SummaryRow(marker, userId: userB, userEmail: "b@x.test", completionTokens: 5));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "user", models: [marker.ToString()]));

        Assert.Equal(2, result.Groups.Count);
        var groupA = result.Groups.Single(g => g.UserId == userA);
        Assert.Equal("a@x.test", groupA.UserEmail);
        Assert.Equal(2, groupA.Calls);
        Assert.Equal(30, groupA.CompletionTokens);

        var groupB = result.Groups.Single(g => g.UserId == userB);
        Assert.Equal("b@x.test", groupB.UserEmail);
        Assert.Equal(1, groupB.Calls);

        // The un-grouped dimensions collapse, as usual.
        Assert.All(result.Groups, g => Assert.Null(g.Model));
        Assert.All(result.Groups, g => Assert.Null(g.Kind));
    }

    [Fact]
    public async Task Summary_GroupsByKindAlone_ReportsOneRowPerDistinctKind()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            SummaryRow(marker, kind: LlmCallKind.Tags, completionTokens: 10),
            SummaryRow(marker, kind: LlmCallKind.Tags, completionTokens: 20),
            SummaryRow(marker, kind: LlmCallKind.ChatMessage, completionTokens: 5));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "kind", models: [marker.ToString()]));

        Assert.Equal(2, result.Groups.Count);
        var tags = result.Groups.Single(g => g.Kind == LlmCallKind.Tags);
        Assert.Equal(2, tags.Calls);
        Assert.Equal(30, tags.CompletionTokens);

        var chat = result.Groups.Single(g => g.Kind == LlmCallKind.ChatMessage);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Summary_GroupsByUserModelKindCombined_DimensionsGenuinelyMultiplyOut()
    {
        // Proves the three requested dimensions combine (a cartesian-style split), not merely that
        // grouping by three things doesn't crash. Same user + same model but a different kind must be a
        // DIFFERENT group from the same user + model + the first kind - if the implementation silently
        // collapsed on a subset of the requested dimensions, this would show fewer than 4 groups.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var modelB = $"{marker}-b";
        db.Users.AddRange(
            new ApplicationUser { Id = userA, UserName = $"{userA}@x.test", Email = "a@x.test" },
            new ApplicationUser { Id = userB, UserName = $"{userB}@x.test", Email = "b@x.test" });
        db.LlmCalls.AddRange(
            SummaryRow(marker, userId: userA, userEmail: "a@x.test", model: modelA, kind: LlmCallKind.Tags),
            SummaryRow(marker, userId: userA, userEmail: "a@x.test", model: modelA, kind: LlmCallKind.ChatMessage),
            SummaryRow(marker, userId: userA, userEmail: "a@x.test", model: modelB, kind: LlmCallKind.Tags),
            SummaryRow(marker, userId: userB, userEmail: "b@x.test", model: modelA, kind: LlmCallKind.Tags));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageSummary>(
            await Build(db).Summary(groupBy: "user,model,kind", models: [modelA, modelB]));

        Assert.Equal(4, result.Groups.Count);
        Assert.Contains(result.Groups, g => g.UserId == userA && g.Model == modelA && g.Kind == LlmCallKind.Tags);
        Assert.Contains(result.Groups, g => g.UserId == userA && g.Model == modelA && g.Kind == LlmCallKind.ChatMessage);
        Assert.Contains(result.Groups, g => g.UserId == userA && g.Model == modelB && g.Kind == LlmCallKind.Tags);
        Assert.Contains(result.Groups, g => g.UserId == userB && g.Model == modelA && g.Kind == LlmCallKind.Tags);
        Assert.All(result.Groups, g => Assert.Equal(1, g.Calls));
    }

    // ---- Fix round 1: Finding 3 (Summary needs the same operation-count ceiling as List) ----

    [Fact]
    public async Task Summary_ExceedingTheOperationCeiling_Returns400_NotUnboundedMaterialization()
    {
        // Mirrors List's OperationsMode_ExceedingTheOperationCeiling_Returns400_NotATruncatedPage:
        // group count can never exceed the number of distinct operations (a group only exists if at
        // least one operation's calls fall into it), so totals.Operations - already computed before this
        // guard runs - is a safe, cost-free bound to check. Seeds one call more than the ceiling, each
        // its own operation, grouped by the widest combination (worst case for group count).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        const int overCeiling = LlmUsageController.MaxOperationsPerRequest + 1;
        db.LlmCalls.AddRange(
            Enumerable.Range(0, overCeiling).Select(_ => SummaryRow(marker, operationId: Guid.NewGuid())));
        await db.SaveChangesAsync();

        var result = await Build(db).Summary(groupBy: "user,model,kind", models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- LlmUsageController.Delete (Task 5) ----
    //
    // THE MOST DESTRUCTIVE ENDPOINT IN THIS FEATURE: it permanently removes LlmCalls rows, and the
    // usage log is the only record a given LLM call ever happened. Every test below that CAN isolate
    // via the file's usual Model marker does so; the one test that specifically has to prove "no filter
    // at all" behaviour (Delete_WithNoDateFilterGiven_StillAppliesTheDefault30DayWindow) cannot rely on
    // a Model filter for isolation - by definition, that test's whole point is to call Delete with no
    // date bound - so it isolates via a userIds filter carrying a fresh, real ApplicationUser instead,
    // which is still safe against every other test's rows in this shared database. It never calls
    // Delete with truly zero query parameters against the live shared table.

    [Fact]
    public async Task Delete_RemovesOnlyRowsMatchingTheFilter_LeavesOthersUntouched()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var wanted = Row(marker);
        var otherModel = Row(marker, model: $"{marker}-other");
        db.LlmCalls.AddRange(wanted, otherModel);
        await db.SaveChangesAsync();

        await Build(db).Delete(models: [marker.ToString()]);

        await using var verifyDb = fx.CreateDbContext();
        Assert.False(await verifyDb.LlmCalls.AnyAsync(c => c.Id == wanted.Id));
        Assert.True(await verifyDb.LlmCalls.AnyAsync(c => c.Id == otherModel.Id));
    }

    [Fact]
    public async Task Delete_ReturnsTheCountOfRowsRemoved()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(Row(marker), Row(marker), Row(marker));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageDeleteResult>(await Build(db).Delete(models: [marker.ToString()]));

        Assert.Equal(3, result.Deleted);
    }

    [Fact]
    public async Task Delete_WithNoDateFilterGiven_StillAppliesTheDefault30DayWindow()
    {
        // THE SINGLE MOST IMPORTANT TEST IN THIS TASK. Delete goes through the exact same
        // LlmUsageQuery.Apply the read endpoints use, and Apply's own default (From ??
        // nowUtc.AddDays(-30)) must still bound it even when the caller supplies no date filter at
        // all - "delete matching these filters" must never silently become "delete everything" just
        // because the filter happened to be empty on the date dimension. Isolated via a userIds filter
        // (a fresh, real ApplicationUser) rather than Model, specifically BECAUSE this test must not
        // pass any date-related parameter - userIds is an orthogonal dimension, so passing it doesn't
        // weaken what's being proven about the date default.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
        var recent = SummaryRow(marker, userId: userId, userEmail: $"{userId}@x.test");
        var old = SummaryRow(marker, userId: userId, userEmail: $"{userId}@x.test");
        old.StartedAt = DateTimeOffset.UtcNow.AddDays(-100);
        old.CompletedAt = old.StartedAt;
        db.LlmCalls.AddRange(recent, old);
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageDeleteResult>(await Build(db).Delete(userIds: [userId]));

        await using var verifyDb = fx.CreateDbContext();
        Assert.Equal(1, result.Deleted);
        Assert.False(await verifyDb.LlmCalls.AnyAsync(c => c.Id == recent.Id)); // inside the window: gone
        Assert.True(await verifyDb.LlmCalls.AnyAsync(c => c.Id == old.Id));     // outside it: untouched
    }

    [Fact]
    public async Task Delete_IsSetBased_LeavesPreviouslyTrackedEntitiesUnsyncedInTheChangeTracker()
    {
        // Proves the implementation is ExecuteDeleteAsync (set-based) BY BEHAVIOUR, not by inspecting
        // SQL: ExecuteDeleteAsync executes directly against the database and deliberately bypasses the
        // change tracker entirely - a well-known, documented EF Core distinguishing signature from
        // "load the matching entities, RemoveRange, SaveChangesAsync", which DOES update the tracker
        // (a successfully removed entry transitions to Detached). This test tracks one matching row in
        // the SAME DbContext instance the delete call itself uses, runs the delete, and checks whether
        // that already-tracked entry is still sitting in the tracker as Unchanged (ExecuteDeleteAsync -
        // correct) or was transitioned to Detached (materialize-then-remove - the absurd approach this
        // whole task exists to rule out on the largest table in the database).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker));
        await db.SaveChangesAsync();

        var tracked = await db.LlmCalls.FirstAsync(c => c.Model == marker.ToString());
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);

        await Build(db).Delete(models: [marker.ToString()]);

        // The row is genuinely gone from the database...
        await using var verifyDb = fx.CreateDbContext();
        Assert.False(await verifyDb.LlmCalls.AnyAsync(c => c.Model == marker.ToString()));

        // ...but the entry THIS test tracked BEFORE the delete call is still sitting in db's own
        // tracker, still Unchanged - proof the delete went straight to the database and never touched
        // db's change tracker.
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        Assert.Contains(tracked, db.LlmCalls.Local);
    }

    [Fact]
    public async Task Delete_FiltersByRecordingId_RemovesOnlyThatRecordingsRows()
    {
        var recordingId = await SeedRecording();
        var otherRecordingId = await SeedRecording();
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var wanted = Row(marker, recordingId: recordingId);
        var otherRecording = Row(marker, recordingId: otherRecordingId);
        db.LlmCalls.AddRange(wanted, otherRecording);
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageDeleteResult>(await Build(db).Delete(recordingId: recordingId));

        Assert.Equal(1, result.Deleted);
        await using var verifyDb = fx.CreateDbContext();
        Assert.False(await verifyDb.LlmCalls.AnyAsync(c => c.Id == wanted.Id));
        Assert.True(await verifyDb.LlmCalls.AnyAsync(c => c.Id == otherRecording.Id));
    }

    [Fact]
    public async Task Delete_FiltersBySectionId_RemovesOnlyThatSectionsRows()
    {
        var sectionId = await SeedSection();
        var otherSectionId = await SeedSection();
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var wanted = Row(marker, sectionId: sectionId);
        var otherSection = Row(marker, sectionId: otherSectionId);
        db.LlmCalls.AddRange(wanted, otherSection);
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageDeleteResult>(await Build(db).Delete(sectionId: sectionId));

        Assert.Equal(1, result.Deleted);
        await using var verifyDb = fx.CreateDbContext();
        Assert.False(await verifyDb.LlmCalls.AnyAsync(c => c.Id == wanted.Id));
        Assert.True(await verifyDb.LlmCalls.AnyAsync(c => c.Id == otherSection.Id));
    }

    [Fact]
    public async Task Delete_ExceedingTheRowCeiling_OneCallPerOperation_Returns400_NotAPartialDelete()
    {
        // Mirrors List's OperationsMode_ExceedingTheOperationCeiling_Returns400_NotATruncatedPage, but
        // for the judgement call documented in task-5-report.md: Delete DOES carry a
        // MaxDeleteRowsPerRequest-sized guard even though ExecuteDeleteAsync never materializes anything
        // into memory (the original reason List/Summary need MaxOperationsPerRequest) - here it exists to
        // cap the blast radius of a single irreversible action, checked against totals.Calls (the actual
        // row count about to be destroyed), not totals.Operations. An explicit 400 with nothing deleted,
        // never a silently partial delete.
        //
        // NOTE what this fixture does NOT prove: every row here is its own operation, so totals.Calls ==
        // totals.Operations for this exact data shape - the guard would fire identically whichever
        // quantity it checked, so this test alone cannot tell Calls and Operations apart. See the
        // companion test below (ViaManyCallsPerOperation) for the fixture shape that actually pins
        // totals.Calls as the correct quantity, and its own comment for the mutation run that proves it.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        const int overCeiling = LlmUsageController.MaxDeleteRowsPerRequest + 1;
        db.LlmCalls.AddRange(
            Enumerable.Range(0, overCeiling).Select(_ => Row(marker, operationId: Guid.NewGuid())));
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
        await using var verifyDb = fx.CreateDbContext();
        Assert.Equal(overCeiling, await verifyDb.LlmCalls.CountAsync(c => c.Model == marker.ToString()));
    }

    [Fact]
    public async Task Delete_ExceedingTheRowCeiling_ViaManyCallsPerOperation_Returns400_NotAPartialDelete()
    {
        // THE test that actually pins the judgement call (totals.Calls, not totals.Operations) rather
        // than merely being consistent with it. FEW operations, MANY calls each - operationCount is
        // comfortably UNDER MaxDeleteRowsPerRequest while totalRows (operationCount * callsPerOperation)
        // sits OVER it, so this is exactly the shape the guard's own rationale describes: a filter that
        // matches few operations which each fan out to many calls. A regression that swapped the guard
        // back to totals.Operations would sail past the ceiling here and delete everything - verified
        // directly by temporarily making that swap and confirming this exact test fails (see
        // task-5-report.md's fix-round-1 section for the pasted failure output).
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        const int callsPerOperation = 5;
        const int operationCount = LlmUsageController.MaxDeleteRowsPerRequest / callsPerOperation + 1;
        const int totalRows = operationCount * callsPerOperation;
        Assert.True(totalRows > LlmUsageController.MaxDeleteRowsPerRequest); // rows: OVER the ceiling
        Assert.True(operationCount < LlmUsageController.MaxDeleteRowsPerRequest); // operations: comfortably UNDER it
        db.LlmCalls.AddRange(
            Enumerable.Range(0, operationCount).SelectMany(_ =>
            {
                var operationId = Guid.NewGuid();
                return Enumerable.Range(1, callsPerOperation)
                    .Select(seq => Row(marker, operationId: operationId, sequence: seq));
            }));
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(models: [marker.ToString()]);

        Assert.IsType<BadRequestObjectResult>(result);
        await using var verifyDb = fx.CreateDbContext();
        Assert.Equal(totalRows, await verifyDb.LlmCalls.CountAsync(c => c.Model == marker.ToString()));
    }

    // ---- LlmUsageController.Filters (Task 5) ----

    [Fact]
    public async Task Filters_ReturnsOnlyValuesActuallyPresentInTheScopedSet()
    {
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var modelA = $"{marker}-a";
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
        db.LlmCalls.AddRange(
            SummaryRow(marker, userId: userId, userEmail: $"{userId}@x.test", model: modelA, kind: LlmCallKind.Tags),
            SummaryRow(marker, model: modelA, kind: LlmCallKind.Tags));
        await db.SaveChangesAsync();

        // Filters has no models/userIds parameter of its own to scope by - it's what POPULATES those
        // dropdowns - so this test can only assert "contains what we seeded", not "equals exactly";
        // other tests' rows within the same default 30-day window are legitimately in scope too.
        var result = OkValue<LlmUsageFilterOptions>(await Build(db).Filters());

        Assert.Contains(modelA, result.Models);
        Assert.Contains(LlmCallKind.Tags, result.Kinds);
        Assert.Contains(result.Users, u => u.UserId == userId && u.UserEmail == $"{userId}@x.test");
    }

    [Fact]
    public async Task Filters_ExcludesValuesOutsideTheDefault30DayWindow_UnlessWidened()
    {
        // The judgement call from task-5-report.md, proved directly: Filters accepts from/to and
        // defaults to the same 30-day window as every other endpoint rather than reporting across all
        // history. A model that only appears on a 100-day-old row must not show up in the
        // default-window dropdown (offering it together with the default date range would guarantee a
        // zero-row match), but widening `from` must reveal it. staleModel is a fresh marker-derived
        // value never seeded anywhere else, so its absence/presence is a safe assertion even though
        // this endpoint is not otherwise scoped to one test's rows.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var staleModel = $"{marker}-stale";
        var stale = SummaryRow(marker, model: staleModel);
        stale.StartedAt = DateTimeOffset.UtcNow.AddDays(-100);
        stale.CompletedAt = stale.StartedAt;
        db.LlmCalls.Add(stale);
        await db.SaveChangesAsync();

        var defaultWindow = OkValue<LlmUsageFilterOptions>(await Build(db).Filters());
        Assert.DoesNotContain(staleModel, defaultWindow.Models);

        var widened = OkValue<LlmUsageFilterOptions>(
            await Build(db).Filters(from: DateTimeOffset.UtcNow.AddDays(-365)));
        Assert.Contains(staleModel, widened.Models);
    }

    [Fact]
    public async Task Filters_RowWithNullUserId_IsExcludedFromUsers_ButStillContributesModelAndKind()
    {
        // The Where(c => c.UserId != null) filter before grouping in Filters is deliberate -
        // LlmUsageFilterUser.UserId is a non-nullable Guid, and there is nothing a userIds filter could
        // match a null UserId against - but was previously unasserted. Proves it by behaviour: a
        // null-UserId row must not surface as a bogus entry (the specific failure mode this guards
        // against is grouping by `c.UserId ?? Guid.Empty` without filtering nulls out first, which would
        // collapse every anonymous row into one spurious Guid.Empty "user"), while its Model/Kind must
        // still show up in the other two lists - the exclusion is per-FIELD, not per-ROW.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        var anonymousModel = $"{marker}-anon";
        db.LlmCalls.Add(SummaryRow(marker, userId: null, model: anonymousModel, kind: LlmCallKind.SectionSummary));
        await db.SaveChangesAsync();

        var result = OkValue<LlmUsageFilterOptions>(await Build(db).Filters());

        Assert.DoesNotContain(result.Users, u => u.UserId == Guid.Empty);
        Assert.Contains(anonymousModel, result.Models);
        Assert.Contains(LlmCallKind.SectionSummary, result.Kinds);
    }

    // ---- Fix round 1 (Task 6 follow-up): `kinds` binds both the enum NAME and NUMERIC query-string
    // forms, and both select the same rows ----
    //
    // Everything above in this file calls the controller action directly (Build(db).List(...)), which
    // never runs ASP.NET Core's actual query-string model binder - a C# enum-array argument is passed
    // in as-is, so it proves nothing about what a real "?kinds=Tags" or "?kinds=7" URL binds to. This is
    // the one test in the file that goes through the real HTTP pipeline (DiarizWebAppFactory, the same
    // harness FeedbackIntegrationTests/RbacIntegrationTests use), specifically to answer that question:
    // the web client (Task 6) now types `kinds` as LlmCallKind[] end-to-end and sends the enum name,
    // e.g. "?kinds=Tags", and the server binds it via [FromQuery] LlmCallKind[]? kinds - if ASP.NET Core
    // did not accept enum names there, that would be the wrong recommendation to have shipped.

    private DiarizWebAppFactory NewHttpFactory() => new(fx);

    private static async Task<Guid> SeedHttpPlatformAdminAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        Users.Ensure(db, id);
        Perms.Grant(db, id, Perms.PlatformAdministrator);
        return id;
    }

    private static HttpClient AuthenticatedHttpClient(DiarizWebAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Deserializes an LlmUsage response the same way the real server serialized it: enums as
    /// their NAME (JsonConfig.Apply adds the same JsonStringEnumConverter Program.cs wires up), not the
    /// System.Net.Http.Json default (which would throw trying to parse "Tags" as a number).</summary>
    private static readonly JsonSerializerOptions HttpJsonOptions = BuildHttpJsonOptions();

    private static JsonSerializerOptions BuildHttpJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonConfig.Apply(options);
        return options;
    }

    [Fact]
    public async Task List_FiltersByKind_TheNameFormAndTheNumericFormSelectTheSameRows()
    {
        using var factory = NewHttpFactory();
        var adminId = await SeedHttpPlatformAdminAsync(factory);
        var token = TestTokens.Issue(adminId);
        using var client = AuthenticatedHttpClient(factory, token);

        var marker = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.LlmCalls.AddRange(
                SummaryRow(marker, kind: LlmCallKind.Tags),
                SummaryRow(marker, kind: LlmCallKind.ChatMessage));
            await db.SaveChangesAsync();
        }

        // The NAME form - what the web client (Task 6) actually sends now that LlmUsageFilter.kinds is
        // typed LlmCallKind[] end-to-end.
        var byName = await client.GetFromJsonAsync<LlmUsagePage<LlmUsageCallRow>>(
            $"/api/admin/llm-usage?mode=calls&models={marker}&kinds=Tags", HttpJsonOptions);
        // The NUMERIC form - the old wire shape (int[]), which must keep working for any caller still
        // sending it (see LlmCallKind.Tags == 7 in LlmCallKind.cs).
        var byNumber = await client.GetFromJsonAsync<LlmUsagePage<LlmUsageCallRow>>(
            $"/api/admin/llm-usage?mode=calls&models={marker}&kinds={(int)LlmCallKind.Tags}", HttpJsonOptions);

        Assert.NotNull(byName);
        Assert.NotNull(byNumber);
        var byNameRow = Assert.Single(byName!.Rows);
        var byNumberRow = Assert.Single(byNumber!.Rows);
        Assert.Equal(LlmCallKind.Tags, byNameRow.Kind);
        Assert.Equal(byNameRow.Id, byNumberRow.Id); // both forms select the exact same row
    }
}
