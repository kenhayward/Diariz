namespace Diariz.Domain.Entities;

/// <summary>Platform-wide configuration held as a single row (<see cref="SingletonId"/>), edited by the
/// Platform Administrator. Currently the storage-quota defaults applied to new users.</summary>
public class PlatformSettings
{
    /// <summary>The one and only row's primary key.</summary>
    public const int SingletonId = 1;

    public const long DefaultStarterQuotaBytes = 5L * 1024 * 1024 * 1024;  // 5 GiB
    public const long DefaultMaxQuotaBytes = 50L * 1024 * 1024 * 1024;     // 50 GiB

    /// <summary>Default retention window (days) after which a recording's audio is eligible for auto-deletion.</summary>
    public const int DefaultAudioRetentionDays = 30;

    public int Id { get; set; } = SingletonId;

    /// <summary>Storage quota (bytes of recorded audio) granted to each user at account creation.</summary>
    public long StarterQuotaBytes { get; set; } = DefaultStarterQuotaBytes;

    /// <summary>Ceiling (bytes) any administrator may raise a user's quota to.</summary>
    public long MaxQuotaBytes { get; set; } = DefaultMaxQuotaBytes;

    /// <summary>How template-driven meeting minutes are generated (per-section calls vs a single call). Applies
    /// from the next template run.</summary>
    public MinutesGenerationMode MinutesGenerationMode { get; set; } = MinutesGenerationMode.SingleCall;

    /// <summary>Master switch for the nightly audio-retention job. Off by default: no audio is auto-deleted
    /// until the Platform Administrator opts in.</summary>
    public bool AutoDeleteAudioEnabled { get; set; }

    /// <summary>Audio older than this many days (by <see cref="Recording.CreatedAt"/>) is deleted by the
    /// nightly job - only for fully transcribed, unprotected recordings.</summary>
    public int AudioRetentionDays { get; set; } = DefaultAudioRetentionDays;

    /// <summary>Server-local time of day at which the nightly audio-retention job runs (default 03:00).</summary>
    public TimeOnly AudioDeletionTimeOfDay { get; set; } = new TimeOnly(3, 0);

    /// <summary>Master switch for user API access (personal <c>dz_api_</c> tokens). Off by default: no API key
    /// authenticates until the Platform Administrator opts in.</summary>
    public bool ApiAccessEnabled { get; set; }
}
