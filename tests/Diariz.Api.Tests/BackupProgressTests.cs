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

    [Fact]
    public void Current_BeforeAnyBuild_ReportsNoOutcome()
    {
        Assert.Null(new BackupProgress().Current.LastOutcome);
    }

    [Fact]
    public void AScopeDisposedAfterSucceeded_ReportsCompleted()
    {
        var progress = new BackupProgress();

        using (var scope = progress.Begin()) scope.Succeeded();

        Assert.Equal(BackupOutcome.Completed, progress.Current.LastOutcome);
    }

    [Fact]
    public void AScopeDisposedWithoutSucceeded_ReportsFailed()
    {
        // A build that threw unwinds through the using without ever committing, which is the whole point:
        // the panel used to read "went from running to idle" as success.
        var progress = new BackupProgress();

        progress.Begin().Dispose();

        Assert.Equal(BackupOutcome.Failed, progress.Current.LastOutcome);
    }

    [Fact]
    public void AFailedBuild_PublishesNoOutcomeWhileAnotherIsStillRunning()
    {
        // Two admins downloading at once: the first one failing must not be read as the second one's verdict.
        var progress = new BackupProgress();
        var first = progress.Begin();
        var second = progress.Begin();

        first.Dispose();
        Assert.Null(progress.Current.LastOutcome);

        second.Succeeded();
        second.Dispose();
        Assert.Equal(BackupOutcome.Completed, progress.Current.LastOutcome);
    }

    [Fact]
    public void StartingABuild_ClearsThePreviousOutcome()
    {
        // Otherwise a new build reports the last one's verdict before it has reached one of its own.
        var progress = new BackupProgress();
        progress.Begin().Dispose();

        using var second = progress.Begin();

        Assert.Null(progress.Current.LastOutcome);
    }
}
