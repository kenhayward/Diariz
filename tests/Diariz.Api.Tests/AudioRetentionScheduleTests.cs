using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class AudioRetentionScheduleTests
{
    [Fact]
    public void NextRun_TodaysTimeStillAhead_ReturnsToday()
    {
        var now = new DateTimeOffset(2026, 1, 10, 1, 0, 0, TimeSpan.Zero);

        var next = AudioRetentionSchedule.NextRun(now, new TimeOnly(3, 0));

        Assert.Equal(new DateTimeOffset(2026, 1, 10, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextRun_TodaysTimeAlreadyPassed_ReturnsTomorrow()
    {
        var now = new DateTimeOffset(2026, 1, 10, 5, 0, 0, TimeSpan.Zero);

        var next = AudioRetentionSchedule.NextRun(now, new TimeOnly(3, 0));

        Assert.Equal(new DateTimeOffset(2026, 1, 11, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextRun_ExactlyAtScheduledTime_ReturnsTomorrow()
    {
        var now = new DateTimeOffset(2026, 1, 10, 3, 0, 0, TimeSpan.Zero);

        var next = AudioRetentionSchedule.NextRun(now, new TimeOnly(3, 0));

        Assert.Equal(new DateTimeOffset(2026, 1, 11, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextRun_RollsOverMonthBoundary()
    {
        var now = new DateTimeOffset(2026, 1, 31, 5, 0, 0, TimeSpan.Zero);

        var next = AudioRetentionSchedule.NextRun(now, new TimeOnly(3, 0));

        Assert.Equal(new DateTimeOffset(2026, 2, 1, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextRun_PreservesLocalOffset()
    {
        var now = new DateTimeOffset(2026, 1, 10, 1, 0, 0, TimeSpan.FromHours(2));

        var next = AudioRetentionSchedule.NextRun(now, new TimeOnly(3, 30));

        Assert.Equal(new DateTimeOffset(2026, 1, 10, 3, 30, 0, TimeSpan.FromHours(2)), next);
    }
}
