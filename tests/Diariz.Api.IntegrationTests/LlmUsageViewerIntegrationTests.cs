using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        string? model = null) => new()
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
    };
}
