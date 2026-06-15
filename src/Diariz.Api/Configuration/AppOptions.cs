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
}

public class WorkerOptions
{
    public const string Section = "Worker";
    /// <summary>Shared secret the Python worker presents on the internal callback endpoint.</summary>
    public string CallbackSecret { get; set; } = "change-me";
}
