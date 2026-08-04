namespace Diariz.Domain.Entities;

/// <summary>A user's report that something looked or behaved wrong, captured with the technical trail
/// leading up to it. Distinct from error tracking: nothing threw, so the exception path never saw it.
///
/// <para>Readable and deletable by a Platform Administrator only - including the submitter's own. A
/// per-user view would imply a support conversation this feature does not have.</para></summary>
public class Feedback
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Who submitted it. Cascade-deleted with the user: this is user-authored content and must
    /// disappear with them, like everything else they own.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user's own words. Free text, so it may quote meeting content - which is exactly why
    /// it lives here, under the same retention, backup and deletion rules as the rest of their data,
    /// rather than in an external error tracker.</summary>
    public string Description { get; set; } = "";

    /// <summary>The SPA route at submission.</summary>
    public string Route { get; set; } = "";

    /// <summary>The app version the browser was running.</summary>
    public string Release { get; set; } = "";

    /// <summary>The client trail, already scrubbed browser-side, stored verbatim as JSON.</summary>
    public string TrailJson { get; set; } = "[]";

    /// <summary>Reserved for the deferred screenshot phase, which needs an Electron shell change and so a
    /// desktop release. Added now so that phase needs no migration. Always null today.</summary>
    public string? ScreenshotBlobKey { get; set; }
}
