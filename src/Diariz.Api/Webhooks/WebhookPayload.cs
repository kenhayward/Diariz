using System.Text.Json;

namespace Diariz.Api.Webhooks;

/// <summary>Absolute links included in a webhook payload's <c>data.links</c>.</summary>
public sealed record WebhookLinks(string Api, string Web);

/// <summary>Builds the thin outbound envelope <c>{ id, type, created, data }</c> as a compact JSON string.
/// The returned string is the EXACT body that gets signed and stored - do not re-serialize it downstream.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Build(string eventId, string type, DateTimeOffset createdUtc, object data) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type,
            created = createdUtc.ToUniversalTime().ToString("o"),
            data,
        }, Options);

    public static WebhookLinks For(string publicUrl, Guid recordingId)
    {
        var baseUrl = publicUrl.TrimEnd('/');
        return new WebhookLinks($"{baseUrl}/api/recordings/{recordingId}", $"{baseUrl}/recordings/{recordingId}");
    }
}
