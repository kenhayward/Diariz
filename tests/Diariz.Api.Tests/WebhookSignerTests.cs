using System.Security.Cryptography;
using System.Text;
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookSignerTests
{
    [Fact]
    public void Sign_matches_standard_webhooks_hmac_vector()
    {
        // v1,<base64(HMAC-SHA256(secret, "id.timestamp.body"))>
        const string secret = "s3cr3t";
        const string id = "evt_abc";
        const long ts = 1700000000;
        const string body = "{\"hello\":\"world\"}";
        var expectedMac = Convert.ToBase64String(
            new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(
                Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));

        Assert.Equal($"v1,{expectedMac}", WebhookSigner.Sign(secret, id, ts, body));
    }
}
