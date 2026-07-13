namespace Diariz.Api.Configuration;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "diariz";
    public string Audience { get; set; } = "diariz";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 120;
}

/// <summary>Google OAuth 2.0 sign-in (server-side authorization-code flow). Sign-in is available only when
/// both a client id and secret are configured; otherwise the endpoints 404 and the web login hides the
/// Google button.</summary>
public class GoogleAuthOptions
{
    public const string Section = "GoogleAuth";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Explicit OAuth redirect URI. Empty = derive from the request as
    /// <c>{scheme}://{host}/api/auth/google/callback</c>. Must exactly match a URI registered on the
    /// Google OAuth client.</summary>
    public string RedirectUri { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
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

    /// <summary>Server-wide default for sending an OpenAI-style <c>reasoning_effort</c> on LLM requests
    /// (reasoning models). Off by default; per-user overridable in Settings. Many endpoints reject the
    /// param, so opt-in is the safe default.</summary>
    public bool ReasoningEnabled { get; set; } = false;

    /// <summary>Default reasoning effort when enabled: <c>low</c> | <c>medium</c> | <c>high</c>.</summary>
    public string ReasoningEffort { get; set; } = "medium";

    public string StreamKey { get; set; } = "summarization-jobs";
    public string ConsumerGroup { get; set; } = "summarizers";
    public string ConsumerName { get; set; } = "api-1";

    /// <summary>True when an endpoint is configured; otherwise the summarise endpoint is a no-op.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
}

/// <summary>Meeting-minutes generation. The LLM endpoint/model/key/reasoning are shared with summarisation
/// (resolved per-user via <c>ISummarizationSettingsResolver</c>); only the stream and transcript budget are
/// minutes-specific (minutes cover more of the conversation than a short summary).</summary>
public class MeetingMinutesOptions
{
    public const string Section = "MeetingMinutes";

    /// <summary>Upper bound on transcript characters sent to the model. Larger than the summary budget —
    /// minutes need more of the conversation to be accurate.</summary>
    public int TranscriptCharBudget { get; set; } = 16000;

    public string StreamKey { get; set; } = "meeting-minutes-jobs";
    public string ConsumerGroup { get; set; } = "minute-takers";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Config for the in-process action-extraction worker (runs actions as part of the pipeline,
/// alongside the summary + minutes). The LLM endpoint/model/key come from the per-user summarisation config.</summary>
public class ActionsOptions
{
    public const string Section = "Actions";

    public string StreamKey { get; set; } = "actions-jobs";
    public string ConsumerGroup { get; set; } = "actions-extractors";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Config for the folder (section) summary worker: rolls up the included recordings' summaries into
/// one folder summary. LLM endpoint/model/key come from the per-user summarisation config.</summary>
public class SectionSummaryOptions
{
    public const string Section = "SectionSummary";

    /// <summary>Upper bound on the combined per-recording summaries sent to the model.</summary>
    public int CombineCharBudget { get; set; } = 24000;

    public string StreamKey { get; set; } = "section-summary-jobs";
    public string ConsumerGroup { get; set; } = "section-summarizers";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Config for the folder (section) minutes worker: reshapes the included recordings' minutes through
/// a chosen meeting-type template. LLM endpoint/model/key come from the per-user summarisation config.</summary>
public class SectionMinutesOptions
{
    public const string Section = "SectionMinutes";

    /// <summary>Upper bound on the combined per-recording minutes sent to the model.</summary>
    public int CombineCharBudget { get; set; } = 32000;

    public string StreamKey { get; set; } = "section-minutes-jobs";
    public string ConsumerGroup { get; set; } = "section-minute-takers";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Config for the async formula-run worker: runs a saved formula over a recording or a folder
/// (section), reducing per-source outputs into one result. LLM endpoint/model/key come from the per-user
/// summarisation config.</summary>
public class FormulaRunOptions
{
    public const string Section = "FormulaRun";

    /// <summary>Upper bound on the combined per-source outputs sent to the model in the reduce step.</summary>
    public int CombineCharBudget { get; set; } = 32000;

    public string StreamKey { get; set; } = "formula-run-jobs";
    public string ConsumerGroup { get; set; } = "formula-runners";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Config for the in-process tag-cloud extraction worker (runs alongside the summary/actions in
/// the pipeline). The LLM endpoint/model/key come from the per-user summarisation config.</summary>
public class TagsOptions
{
    public const string Section = "Tags";

    public string StreamKey { get; set; } = "tag-cloud-jobs";
    public string ConsumerGroup { get; set; } = "tag-extractors";
    public string ConsumerName { get; set; } = "api-1";
}

/// <summary>Semantic-search (RAG, Milestone 3) embeddings. Unlike the chat/summarisation endpoint (free
/// per-user), the vector index requires every chunk and every query to use the <b>same model and dimension</b>,
/// so the model is a server-level, dimension-pinned setting. The endpoint/key fall back to the recording
/// owner's summarisation endpoint (self-hosters usually run one server, e.g. Ollama, serving both) - see
/// <c>EmbeddingSettingsResolver</c>. Empty everywhere disables RAG: search stays lexical (unchanged) and
/// nothing breaks.</summary>
public class EmbeddingOptions
{
    public const string Section = "Embedding";

    /// <summary>Base URL of the OpenAI-compatible embeddings API. Empty = fall back to the owner's
    /// summarisation endpoint; empty there too disables embedding.</summary>
    public string ApiBase { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// <summary>The embedding model. Its output dimension MUST equal <see cref="Dimension"/> and the
    /// <c>vector(N)</c> column; changing it means re-embedding + a migration.</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Embedding dimension - pinned to the <c>TranscriptChunk.Embedding vector(768)</c> column.</summary>
    public int Dimension { get; set; } = 768;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Max inputs per /embeddings request (chunks are embedded in batches).</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Task-instruction prefix prepended to a <b>search query</b> before embedding. The nomic models
    /// (the default) were trained with <c>search_query:</c> / <c>search_document:</c> prefixes and retrieve
    /// noticeably better with them. Set both prefixes empty for models that don't use them (e.g. OpenAI
    /// <c>text-embedding-3-*</c>). Query and document sides must be paired consistently.</summary>
    public string QueryPrefix { get; set; } = "search_query: ";

    /// <summary>Task-instruction prefix prepended to each <b>chunk</b> before embedding (see <see cref="QueryPrefix"/>).</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";

    public string StreamKey { get; set; } = "embedding-jobs";
    public string ConsumerGroup { get; set; } = "embedders";
    public string ConsumerName { get; set; } = "api-1";
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
    /// is on when tools are enabled. Per-user overridable in Settings. Empty by default — even
    /// <c>send_email</c> is on, since it can only ever email the signed-in user their own address.</summary>
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

/// <summary>The MCP server (Streamable-HTTP <c>/mcp</c> endpoint that lets Claude connect to the user's own
/// transcripts). The public web origin used to make transcript deep-links absolute is reused from
/// <see cref="AppPublicOptions.PublicUrl"/> (falling back to the request origin).</summary>
public class McpOptions
{
    public const string Section = "Mcp";
    /// <summary>Master switch. When false the <c>/mcp</c> endpoint is not mapped.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>OAuth 2.1 authorization server for the MCP web connector (claude.ai) - lets a user add Diariz as
/// a remote connector without pasting a token (the browser runs a consent handshake). Built on OpenIddict; the
/// issuer origin is <see cref="AppPublicOptions.PublicUrl"/> (falling back to the request origin). The static
/// <c>dz_mcp_</c> personal token stays available alongside this for Desktop/Code/CI.</summary>
public class McpOAuthOptions
{
    public const string Section = "McpOAuth";

    /// <summary>The OAuth scope an MCP client requests, and the resource/audience bound to issued access
    /// tokens. Access tokens whose audience is not <see cref="Resource"/> are rejected by the <c>/mcp</c>
    /// resource server. These are wire constants, not user-configurable.</summary>
    public const string Scope = "mcp";
    public const string Resource = "diariz-mcp";

    /// <summary>Master switch. When false the OAuth endpoints and resource-server validation are not
    /// registered (the static personal-token path is unaffected).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Explicit issuer URL (must be the public HTTPS origin clients reach). Empty = derive from
    /// <see cref="AppPublicOptions.PublicUrl"/>, else the request origin. OpenIddict requires HTTPS in
    /// production; on http/localhost dev the transport requirement is relaxed automatically.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Where to persist the OpenIddict signing/encryption certificates so tokens survive a container
    /// recreate. Empty = reuse the Data Protection keyring volume (<c>DataProtection:KeysPath</c>); if that is
    /// also unset (local dev), ephemeral development certificates are used instead.</summary>
    public string KeysPath { get; set; } = "";

    /// <summary>Hosts allowed to register a client via Dynamic Client Registration (the client's
    /// <c>redirect_uri</c> host must be one of these). Defaults cover the claude.ai/claude.com web connector
    /// plus loopback for Desktop/Code's local OAuth callback. Comma/space tolerant via config binding.</summary>
    public string[] AllowedRedirectHosts { get; set; } = ["claude.ai", "claude.com", "localhost", "127.0.0.1"];
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

/// <summary>OpenAI-compatible speech-to-text endpoint used for chat voice dictation - the server-fallback
/// path for environments where the browser SpeechRecognition API is unavailable (the Electron desktop
/// shell, Safari, Firefox). Empty <see cref="ApiBase"/> disables the server path; the browser API is still
/// used where present. This is deliberately server-level only (dictation is infrastructure, not a per-user
/// bring-your-own-key concern like summarisation).</summary>
public class DictationOptions
{
    public const string Section = "Dictation";
    /// <summary>Base URL of the OpenAI-compatible API, e.g. http://whisper:8000/v1. Empty disables the server path.</summary>
    public string ApiBase { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "whisper-1";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>True when an STT endpoint is configured; otherwise the transcribe endpoint returns 400.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
}
