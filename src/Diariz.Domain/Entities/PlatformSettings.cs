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

    /// <summary>Default per-request timeout (seconds) for every LLM call (summary, actions, minutes, tags,
    /// translation, embeddings). Local models can be slow, so it is generous and admin-adjustable.</summary>
    public const int DefaultLlmTimeoutSeconds = 120;

    /// <summary>Default number of days LLM usage log rows are retained before the nightly sweep deletes them.
    /// 0 means keep forever.</summary>
    public const int DefaultLlmUsageRetentionDays = 90;

    /// <summary>Default max cosine distance (0..2) at which a voice match is applied automatically.
    ///
    /// <para>0.30, which is the operating point deployments were already running under the old
    /// <c>Identification:Threshold</c> environment variable this replaces - so upgrading changes no
    /// behaviour. It is deliberately strict: measured against the live distance distribution, true matches
    /// cluster around 0.2-0.3 and impostors around 0.55-0.75, so the valley sits near 0.45 and there is
    /// recall to be had by loosening. The <see cref="DefaultIdentificationConfirmBand">confirmation
    /// band</see>, not a looser default, is the safe way to reach for it.</para></summary>
    public const double DefaultIdentificationThreshold = 0.30;

    /// <summary>Default max distance at which a match is <em>suggested</em> rather than applied; between this
    /// and <see cref="DefaultIdentificationThreshold"/> the user is asked.
    ///
    /// <para>0.40 rather than 0.50: on the measured instance that is a first queue of roughly 90 items
    /// instead of 163, and a backlog nobody works through produces worse evidence than a smaller one that
    /// gets read. Widen it once the decision log shows where the real boundary sits.</para></summary>
    public const double DefaultIdentificationConfirmBand = 0.40;

    /// <summary>Default gap by which the best-matching <b>person</b> must beat the next person before either
    /// is acted on. Guards confusable voices, where the nearest is close to a coin-flip.</summary>
    public const double DefaultIdentificationMargin = 0.05;

    /// <summary>Default minimum speech (ms) before a speaker is matched at all. Accuracy climbs steeply up to
    /// 10-20s and sub-2s utterances are unreliable, so scoring them lends false confidence to noise.</summary>
    public const int DefaultIdentificationMinSpeechMs = 3000;

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

    /// <summary>The platform-wide request timeout, edited in Settings and applied to EVERY LLM call.
    ///
    /// Declared obsolete in 0.221.0 - the timeout had become a per-model parameter - and read only on the
    /// environment-fallback path. That made it inert the moment a deployment configured its first model,
    /// while the Settings control went on promising "a platform-wide request timeout for every AI call".
    /// An administrator who raised it to 600 for a large local model still got 120 and a failure
    /// indistinguishable from a dead endpoint, so 0.235.1 made the control honest rather than removing it.
    ///
    /// It is a FLOOR, not an override: <see cref="Diariz.Api.Services.Llm.LlmPlatformLayers"/> places it
    /// below a model's own layers, so per-model tuning still wins, and it stays silent at its default so an
    /// operator's LlmDefaults__TimeoutSeconds is not outranked by a row that merely exists.</summary>
    public int LlmTimeoutSeconds { get; set; } = DefaultLlmTimeoutSeconds;

    /// <summary>The model used by any call group with no explicit assignment. Null falls through to the
    /// model synthesized from Summarization:ApiBase, so an upgrade with no rows keeps working unchanged.</summary>
    public Guid? DefaultLlmModelId { get; set; }
    public LlmModel? DefaultLlmModel { get; set; }

    /// <summary>Master switch for the MCP server + dz_mcp_ tokens. On by default (bounded by env Mcp:Enabled).
    /// Seeded true in the migration so shipping this never disables an existing connector.</summary>
    public bool McpAccessEnabled { get; set; } = true;

    /// <summary>Master switch for outbound webhooks / user Automations. Off by default; used from Phase 2.</summary>
    public bool WebhooksEnabled { get; set; }

    /// <summary>Master switch for the LLM usage log. On by default - the log is the feature. Enforced by
    /// LlmUsageWriter, not the handler, so the call path never pays for a settings lookup.</summary>
    public bool LlmUsageLoggingEnabled { get; set; } = true;

    /// <summary>Usage rows older than this many days are deleted by the nightly sweep. 0 = keep forever.
    /// This table gets a row per call, and embeddings write one per chunk, so a bound matters.</summary>
    public int LlmUsageRetentionDays { get; set; } = DefaultLlmUsageRetentionDays;

    /// <summary>Whether streaming requests ask for token counts via stream_options.include_usage.
    /// A toggle rather than a constant because an OpenAI-compatible endpoint that rejects the unknown
    /// field must be recoverable without a redeploy. Used from PR 2.</summary>
    public bool LlmStreamUsageEnabled { get; set; } = true;

    /// <summary>Max cosine distance at which a voice match is applied automatically. Lower is stricter.
    ///
    /// <para>Replaces the compiled <c>Identification:Threshold</c>: calibrating an operating point needs it
    /// changeable without a redeploy, and there must be exactly one copy of the number.</para></summary>
    public double IdentificationThreshold { get; set; } = DefaultIdentificationThreshold;

    /// <summary>Max distance at which a match is offered for confirmation instead of applied. Must be
    /// <b>looser</b> (larger) than <see cref="IdentificationThreshold"/>; inverted, nothing would ever
    /// auto-apply.</summary>
    public double IdentificationConfirmBand { get; set; } = DefaultIdentificationConfirmBand;

    /// <summary>How far the nearest person must beat the next <b>person</b> before either is acted on.
    /// Measured between people, never between two templates of the same person.</summary>
    public double IdentificationMargin { get; set; } = DefaultIdentificationMargin;

    /// <summary>Below this much total speech, a speaker is not matched at all.</summary>
    public int IdentificationMinSpeechMs { get; set; } = DefaultIdentificationMinSpeechMs;
}
