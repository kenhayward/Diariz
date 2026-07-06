using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class PlatformSettingsControllerTests
{
    private static PlatformSettingsController Build(DiarizDbContext db) =>
        new(new PlatformSettingsService(db), db) { ControllerContext = Http.Context(Guid.NewGuid()) };

    [Fact]
    public async Task Get_ReturnsDefaults_WhenUnset()
    {
        using var db = TestDb.Create();
        var dto = await Build(db).Get();

        Assert.Equal(PlatformSettings.DefaultStarterQuotaBytes, dto.StarterQuotaBytes);
        Assert.Equal(PlatformSettings.DefaultMaxQuotaBytes, dto.MaxQuotaBytes);
    }

    [Fact]
    public async Task Update_PersistsNewValues()
    {
        using var db = TestDb.Create();
        var controller = Build(db);

        var result = await controller.Update(new UpdatePlatformSettingsRequest(2L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024));

        var dto = Assert.IsType<PlatformSettingsDto>(result.Value);
        Assert.Equal(2L * 1024 * 1024 * 1024, dto.StarterQuotaBytes);
        var row = await db.PlatformSettings.SingleAsync();
        Assert.Equal(10L * 1024 * 1024 * 1024, row.MaxQuotaBytes);
    }

    [Fact]
    public async Task Update_StarterAboveMax_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(20L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_NonPositive_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(0, 5L * 1024 * 1024 * 1024));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_RoundTrips_MinutesGenerationMode()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(
            new UpdatePlatformSettingsRequest(gb, gb, MinutesGenerationMode.PerSection));

        Assert.Equal(MinutesGenerationMode.PerSection, Assert.IsType<PlatformSettingsDto>(result.Value).MinutesGenerationMode);
        Assert.Equal(MinutesGenerationMode.PerSection, (await db.PlatformSettings.SingleAsync()).MinutesGenerationMode);
    }

    [Fact]
    public async Task Get_DefaultsTo_SingleCall()
    {
        using var db = TestDb.Create();
        Assert.Equal(MinutesGenerationMode.SingleCall, (await Build(db).Get()).MinutesGenerationMode);
    }
}
