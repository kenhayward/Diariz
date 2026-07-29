using System.Text.Json;

namespace Diariz.Api.Webhooks;

/// <summary>Absolute links included in a webhook payload's <c>data.links</c>.</summary>
public sealed record WebhookLinks(string Api, string Web);

/// <summary>Builds the thin outbound envelope <c>{ id, type, created, data }</c> as a compact JSON string.
/// The returned string is the EXACT body that gets signed and stored - do not re-serialize it downstream.</summary>
public static class WebhookPayload
{
    /// <summary>The camelCase policy is what keeps the envelope's names uniform. The payload sites write
    /// anonymous objects whose members are already camelCase (the policy is a no-op on those), but a nested
    /// record like <see cref="WebhookLinks"/> would otherwise serialise as <c>Api</c>/<c>Web</c> beside its
    /// camelCase siblings - so the policy, not per-property attributes, is the fix that also covers whatever
    /// nested type a future event adds.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
