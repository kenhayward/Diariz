using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class BackupProgressTests
{
    [Fact]
    public void Current_WhenNothingIsRunning_ReportsIdle()
    {
        var progress = new BackupProgress();

        var snapshot = progress.Current;

        Assert.False(snapshot.Running);
        Assert.Null(snapshot.Phase);
        Assert.Null(snapshot.StartedAt);
        Assert.Equal(0, snapshot.ObjectsArchived);
    }

    [Fact]
    public void Begin_MarksRunning_AndTracksThePhaseAndObjectCount()
    {
        var progress = new BackupProgress();

        using var scope = progress.Begin();

        // A build always opens with the database dump, so callers see a meaningful phase immediately.
        Assert.True(progress.Current.Running);
        Assert.Equal(BackupPhase.Database, progress.Current.Phase);
        Assert.NotNull(progress.Current.StartedAt);

        progress.SetPhase(BackupPhase.Objects);
        progress.ObjectArchived();
        progress.ObjectArchived();

        Assert.Equal(BackupPhase.Objects, progress.Current.Phase);
        Assert.Equal(2, progress.Current.ObjectsArchived);
    }

    [Fact]
    public void DisposingTheScope_ReturnsToIdle()
    {
        var progress = new BackupProgress();

        progress.Begin().Dispose();

        Assert.False(progress.Current.Running);
        Assert.Null(progress.Current.Phase);
    }

    [Fact]
    public void ASecondBuild_StartsFromAFreshCount()
    {
        var progress = new BackupProgress();
        using (progress.Begin())
        {
            progress.SetPhase(BackupPhase.Objects);
            progress.ObjectArchived();
        }

        using var second = progress.Begin();

        Assert.Equal(0, progress.Current.ObjectsArchived);
        Assert.Equal(BackupPhase.Database, progress.Current.Phase);
    }

    [Fact]
    public void ConcurrentBuilds_StayRunningUntilTheLastOneFinishes()
    {
        // Two admins can hit Download at once; whichever finishes first must not clear the other's progress
        // (the UI would then stop reporting while an archive is still being assembled).
        var progress = new BackupProgress();
        var first = progress.Begin();
        var second = progress.Begin();

        first.Dispose();
        Assert.True(progress.Current.Running);

        second.Dispose();
        Assert.False(progress.Current.Running);
    }

    [Fact]
    public void DisposingAScopeTwice_DoesNotEndAnotherBuild()
    {
        var progress = new BackupProgress();
        var first = progress.Begin();
        using var second = progress.Begin();

        first.Dispose();
        first.Dispose();

        Assert.True(progress.Current.Running);
    }
}
