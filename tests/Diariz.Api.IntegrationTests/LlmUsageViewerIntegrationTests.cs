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

    private static LlmCall Row(
        Guid marker,
        int durationMs = 1000,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        Guid? operationId = null,
        int sequence = 1,
        bool success = true) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId ?? marker,
        Sequence = sequence,
        Kind = LlmCallKind.Tags,
        Model = marker.ToString(),
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
