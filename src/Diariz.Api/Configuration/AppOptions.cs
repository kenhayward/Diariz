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
    /// <summary>Browser-reachable base URL used when signing presigned URLs handed to clients.
    /// Defaults to <see cref="Endpoint"/>; in Docker the internal `minio:9000` host isn't reachable
    /// from the browser, so set this to the host-mapped address (e.g. http://localhost:9002).</summary>
    public string PublicEndpoint { get; set; } = "";
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
