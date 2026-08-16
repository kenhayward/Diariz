using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Ambient "who asked for this call, and why", read by <see cref="LlmTelemetryHandler"/>.
///
/// WHY AMBIENT: the handler lives inside HttpClient, several layers below the code that knows the user
/// and the recording. Threading a context parameter through every client interface instead would touch
/// eight interfaces and every call site, and a new client could still forget to pass it on. A scope is
/// pushed once per job at the top; everything below it is attributed for free.
///
/// A call made with no scope active is still logged, as Kind = Unknown. That is deliberate: an
/// unattributed row is visible and fixable, whereas a dropped row is not.</summary>
public sealed class LlmCallScope : IDisposable
{
    private static readonly AsyncLocal<LlmCallScope?> CurrentScope = new();

    /// <summary>The innermost active scope, or null when the call is unattributed.</summary>
    public static LlmCallScope? Active => CurrentScope.Value;

    private readonly LlmCallScope? _parent;
    private int _calls;

    public LlmCallKind Kind { get; }
    public Guid OperationId { get; }
    public Guid? UserId { get; }
    public string UserEmail { get; }
    public Guid? RecordingId { get; }
    public string? RecordingTitle { get; }
    public Guid? SectionId { get; }
    public string? SectionName { get; }

    private LlmCallScope(
        LlmCallKind kind, Guid? userId, string? userEmail, Guid? recordingId, string? recordingTitle,
        Guid? sectionId, string? sectionName, LlmCallScope? parent)
    {
        Kind = kind;
        OperationId = Guid.NewGuid();
        UserId = userId;
        UserEmail = userEmail ?? string.Empty;
        RecordingId = recordingId;
        RecordingTitle = recordingTitle;
        SectionId = sectionId;
        SectionName = sectionName;
        _parent = parent;
    }

    /// <summary>Starts a new operation. Dispose restores whatever was active before.</summary>
    public static LlmCallScope Push(
        LlmCallKind kind, Guid? userId = null, string? userEmail = null, Guid? recordingId = null,
        string? recordingTitle = null, Guid? sectionId = null, string? sectionName = null)
    {
        var scope = new LlmCallScope(
            kind, userId, userEmail, recordingId, recordingTitle, sectionId, sectionName, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The 1-based index of the next call in this operation. Interlocked because a single
    /// operation can fan out concurrently (per-section minutes).</summary>
    public int NextSequence() => Interlocked.Increment(ref _calls);

    public void Dispose() => CurrentScope.Value = _parent;
}
