namespace Diariz.Api.Services;

/// <summary>Which stage of the archive build is running.</summary>
public enum BackupPhase
{
    /// <summary>Streaming the <c>pg_dump</c> into the archive.</summary>
    Database,
    /// <summary>Copying the object store's blobs in, one entry at a time.</summary>
    Objects,
}

/// <summary>How the last finished build ended. Distinguishes "the archive is built" from "the build threw",
/// which the running/idle flag alone cannot: both leave the tracker idle.</summary>
public enum BackupOutcome
{
    Completed,
    Failed,
}

/// <summary>A point-in-time view of the backup build, as returned by <c>GET api/maintenance/backup/status</c>.
/// <paramref name="Phase"/> is null when nothing is running. <paramref name="LastOutcome"/> is how the last
/// finished build ended, and is null while one is running and before any has run.</summary>
public record BackupProgressSnapshot(
    bool Running, BackupPhase? Phase, int ObjectsArchived, DateTimeOffset? StartedAt,
    BackupOutcome? LastOutcome);

/// <summary>One tracked build. Commit-or-rollback: a scope disposed without <see cref="Succeeded"/> is
/// recorded as a failure, so an exception unwinding through the <c>using</c> reports itself.</summary>
public interface IBackupScope : IDisposable
{
    /// <summary>Marks this build as having produced a complete archive.</summary>
    void Succeeded();
}

/// <summary>Tracks whether a backup archive is currently being assembled.
///
/// <para><c>MaintenanceController.Backup</c> builds the whole zip to a temp file <em>before</em> the first
/// response byte is sent, so the browser shows nothing at all - no download entry, no progress - for what can
/// be several minutes on a large platform. The Maintenance panel polls this instead, and can say the backup is
/// running (and how far in) while the request is still in flight.</para>
///
/// <para>In-memory and per-instance, which is all that's needed: a backup is one request handled by one node,
/// and the admin polling it is talking to that same node.</para></summary>
public interface IBackupProgress
{
    /// <summary>What the build is doing right now (or idle).</summary>
    BackupProgressSnapshot Current { get; }

    /// <summary>Marks a build as running until the returned scope is disposed. Starting a build resets the
    /// phase, object count and outcome, so each archive reports its own progress from zero. Call
    /// <see cref="IBackupScope.Succeeded"/> before disposing, or the build is recorded as failed.</summary>
    IBackupScope Begin();

    void SetPhase(BackupPhase phase);

    /// <summary>Counts one object-store blob written into the archive.</summary>
    void ObjectArchived();
}

/// <inheritdoc cref="IBackupProgress"/>
public sealed class BackupProgress : IBackupProgress
{
    private readonly Lock _gate = new();
    private int _active;
    private BackupPhase _phase;
    private int _objectsArchived;
    private DateTimeOffset? _startedAt;
    private BackupOutcome? _lastOutcome;

    public BackupProgressSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _active > 0
                    ? new BackupProgressSnapshot(true, _phase, _objectsArchived, _startedAt, _lastOutcome)
                    : new BackupProgressSnapshot(false, null, 0, null, _lastOutcome);
            }
        }
    }

    public IBackupScope Begin()
    {
        lock (_gate)
        {
            // Only the outermost build resets the counters: two admins can download at once, and the first to
            // finish must not zero (or end) the other's progress.
            if (_active++ == 0)
            {
                _phase = BackupPhase.Database;
                _objectsArchived = 0;
                _startedAt = DateTimeOffset.UtcNow;
                _lastOutcome = null;
            }
        }
        return new Scope(this);
    }

    public void SetPhase(BackupPhase phase)
    {
        lock (_gate) _phase = phase;
    }

    public void ObjectArchived()
    {
        lock (_gate) _objectsArchived++;
    }

    private void End(bool succeeded)
    {
        lock (_gate)
        {
            if (_active == 0) return;
            _active--;
            // Only the last build in flight publishes a verdict. With one still running there is no settled
            // outcome to report, and stamping one would attribute this build's failure to that one.
            if (_active == 0) _lastOutcome = succeeded ? BackupOutcome.Completed : BackupOutcome.Failed;
        }
    }

    /// <summary>Ends one build on dispose, recording failure unless <see cref="Succeeded"/> was called.
    /// Idempotent, so a double-dispose can't end someone else's build.</summary>
    private sealed class Scope(BackupProgress owner) : IBackupScope
    {
        private bool _ended;
        private bool _succeeded;

        public void Succeeded() => _succeeded = true;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.End(_succeeded);
        }
    }
}
