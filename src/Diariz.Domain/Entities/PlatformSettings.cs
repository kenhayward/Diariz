namespace Diariz.Domain.Entities;

/// <summary>Platform-wide configuration held as a single row (<see cref="SingletonId"/>), edited by the
/// Platform Administrator. Currently the storage-quota defaults applied to new users.</summary>
public class PlatformSettings
{
    /// <summary>The one and only row's primary key.</summary>
    public const int SingletonId = 1;

    public const long DefaultStarterQuotaBytes = 5L * 1024 * 1024 * 1024;  // 5 GiB
    public const long DefaultMaxQuotaBytes = 50L * 1024 * 1024 * 1024;     // 50 GiB

    public int Id { get; set; } = SingletonId;

    /// <summary>Storage quota (bytes of recorded audio) granted to each user at account creation.</summary>
    public long StarterQuotaBytes { get; set; } = DefaultStarterQuotaBytes;

    /// <summary>Ceiling (bytes) any administrator may raise a user's quota to.</summary>
    public long MaxQuotaBytes { get; set; } = DefaultMaxQuotaBytes;

    /// <summary>How template-driven meeting minutes are generated (per-section calls vs a single call). Applies
    /// from the next template run.</summary>
    public MinutesGenerationMode MinutesGenerationMode { get; set; } = MinutesGenerationMode.SingleCall;
}
