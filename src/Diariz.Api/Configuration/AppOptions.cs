namespace Diariz.Api.Configuration;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "diariz";
    public string Audience { get; set; } = "diariz";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 120;
}

public class StorageOptions
{
    public const string Section = "Storage";
    public string Endpoint { get; set; } = "http://minio:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string Bucket { get; set; } = "recordings";
    public bool ForcePathStyle { get; set; } = true;
}

public class JobQueueOptions
{
    public const string Section = "JobQueue";
    public string RedisConnection { get; set; } = "redis:6379";
    public string StreamKey { get; set; } = "transcription-jobs";
    public string ConsumerGroup { get; set; } = "workers";
    /// <summary>Stream the GPU worker consumes audio-concatenation merge jobs from.</summary>
    public string MergeStreamKey { get; set; } = "audio-merge-jobs";
}

public class WorkerOptions
{
    public const string Section = "Worker";
    /// <summary>Shared secret the Python worker presents on the internal callback endpoint.</summary>
    public string CallbackSecret { get; set; } = "change-me";
}

/// <summary>OpenAI-compatible endpoint used to summarise transcripts (and auto-name recordings).</summary>
public class SummarizationOptions
{
    public const string Section = "Summarization";
    /// <summary>Base URL of the OpenAI-compatible API, e.g. https://api.openai.com/v1. Empty disables summarisation.</summary>
    public string ApiBase { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 120;
    public string StreamKey { get; set; } = "summarization-jobs";
    public string ConsumerGroup { get; set; } = "summarizers";
    public string ConsumerName { get; set; } = "api-1";

    /// <summary>True when an endpoint is configured; otherwise the summarise endpoint is a no-op.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
}

/// <summary>Chat-specific settings. The LLM endpoint/model/key are shared with summarisation
/// (per-user, via <c>UserSettings</c>); only the context-window size is chat-specific.</summary>
public class ChatOptions
{
    public const string Section = "Chat";
    /// <summary>Model context window in tokens, used by the context dial. Per-user overridable in Settings.</summary>
    public int ContextLength { get; set; } = 131072;

    /// <summary>Server-wide default for chat tool calling. Off by default (per-user overridable in Settings);
    /// many self-hosted models don't support OpenAI tool calling, so opt-in is the safe default.</summary>
    public bool ToolsEnabled { get; set; } = false;

    /// <summary>Comma-separated tool names that are off by default server-wide. Every other registered tool
    /// is on when tools are enabled. Per-user overridable in Settings.</summary>
    public string DisabledTools { get; set; } = "";
}

/// <summary>SMTP settings for transactional email (the account-setup link). When <see cref="Enabled"/>
/// is false (no host), the grant flow falls back to showing the admin the link instead of emailing it.</summary>
public class EmailOptions
{
    public const string Section = "Email";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool UseStartTls { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(SmtpHost);
}

/// <summary>Public-facing app settings — the browser origin used to build links in emails.</summary>
public class AppPublicOptions
{
    public const string Section = "App";
    /// <summary>Browser-reachable base URL, e.g. http://localhost:8081. Empty = derive from the request.</summary>
    public string PublicUrl { get; set; } = "";
}

/// <summary>Limits and codec policy for user-uploaded audio files (the "Upload" button). Recorded clips
/// from the browser are not gated by these.</summary>
public class UploadOptions
{
    public const string Section = "Uploads";
    /// <summary>Max size of a single uploaded file, in bytes (in addition to the per-user storage quota).</summary>
    public long MaxBytes { get; set; } = 500L * 1024 * 1024; // 500 MB (~4 h of typical voice audio)
    /// <summary>Accept M4A/AAC uploads. AAC has active patents, so it can be disabled for maximum
    /// commercial caution; the royalty-free formats (WAV/MP3/FLAC/Ogg/Opus/WebM) are always accepted.</summary>
    public bool AllowAac { get; set; } = true;
}

/// <summary>Limits for supporting-document attachments on a recording.</summary>
public class AttachmentOptions
{
    public const string Section = "Attachments";
    /// <summary>Max size of a single uploaded attachment file, in bytes (also counts toward the user's quota).</summary>
    public long MaxBytes { get; set; } = 50L * 1024 * 1024; // 50 MB
}

/// <summary>Automatic speaker identification (matching new recordings' speakers to enrolled voiceprints).</summary>
public class IdentificationOptions
{
    public const string Section = "Identification";
    /// <summary>Master switch for auto-identification.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Max cosine distance (0..2) to accept a match — lower is stricter. A starting point;
    /// calibrate per docs/Speaker_Identification_and_Verification.md (false-accepts attach a wrong identity).</summary>
    public double Threshold { get; set; } = 0.4;
}
