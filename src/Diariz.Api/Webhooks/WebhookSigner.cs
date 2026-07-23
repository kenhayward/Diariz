using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Webhooks;

/// <summary>Standard-Webhooks signing: <c>v1,base64(HMAC-SHA256(secret, "id.timestamp.body"))</c>.</summary>
public static class WebhookSigner
{
    public static string Sign(string secret, string webhookId, long timestampUnix, string body)
    {
        var signed = $"{webhookId}.{timestampUnix}.{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return $"v1,{Convert.ToBase64String(mac)}";
    }
}
