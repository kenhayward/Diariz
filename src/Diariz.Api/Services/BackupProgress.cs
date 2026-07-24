namespace Diariz.Api.Services;

/// <summary>Which stage of the archive build is running.</summary>
public enum BackupPhase
{
    /// <summary>Streaming the <c>pg_dump</c> into the archive.</summary>
    Database,
    /// <summary>Copying the object store's blobs in, one entry at a time.</summary>
    Objects,
}

/// <summary>A point-in-time view of the backup build, as returned by <c>GET api/maintenance/backup/status</c>.
/// <paramref name="Phase"/> is null when nothing is running.</summary>
public record BackupProgressSnapshot(
    bool Running, BackupPhase? Phase, int ObjectsArchived, DateTimeOffset? StartedAt);

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
    /// phase and object count, so each archive reports its own progress from zero.</summary>
    IDisposable Begin();

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

    public BackupProgressSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _active > 0
                    ? new BackupProgressSnapshot(true, _phase, _objectsArchived, _startedAt)
                    : new BackupProgressSnapshot(false, null, 0, null);
            }
        }
    }

    public IDisposable Begin()
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

    private void End()
    {
        lock (_gate)
        {
            if (_active > 0) _active--;
        }
    }

    /// <summary>Ends one build on dispose. Idempotent, so a double-dispose can't end someone else's build.</summary>
    private sealed class Scope(BackupProgress owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.End();
        }
    }
}
