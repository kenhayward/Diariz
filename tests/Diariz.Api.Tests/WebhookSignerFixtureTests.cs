using System.Text.Json;
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

/// <summary>Writes the signing vectors the TypeScript n8n node verifies against, and asserts the committed
/// file still matches what this signer produces. The delivery signature is a cross-language contract: if
/// .NET signing and TypeScript verification drift apart, every n8n delivery is silently rejected in
/// production with nothing failing here. Either side changing now breaks one of the two suites.
/// The fix for a failure is to commit the regenerated file, never to edit it by hand.</summary>
public class WebhookSignerFixtureTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "integrations", "n8n-nodes-diariz", "test", "fixtures", "signing-vectors.json");

    private static readonly (string Secret, string Id, long Ts, string Body)[] Cases =
    {
        ("whsec_test", "evt_1", 1750000000L, """{"id":"evt_1"}"""),
        ("dz_whsec_abc123", "evt_2", 1750000123L,
            """{"id":"evt_2","type":"recording.transcribed","created":"2026-07-24T10:00:00.0000000Z","data":{"recordingId":"6f1b6f0e-6d5f-4a1d-9d3f-2b7c1a5e8f42","name":"Weekly sync"}}"""),
        // Non-ASCII on purpose: UTF-8 byte handling is exactly where a cross-language HMAC usually diverges.
        ("dz_whsec_unicode", "evt_3", 1750000456L, """{"id":"evt_3","data":{"name":"Réunion hebdomadaire ✅"}}"""),
    };

    [Fact]
    public void CommittedVectors_MatchTheSigner()
    {
        var expected = Cases.Select(c => new
        {
            secret = c.Secret,
            webhookId = c.Id,
            timestamp = c.Ts,
            body = c.Body,
            signature = WebhookSigner.Sign(c.Secret, c.Id, c.Ts, c.Body),
        }).ToArray();

        var json = JsonSerializer.Serialize(new { vectors = expected }, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep the non-ASCII case readable in the committed file rather than \uXXXX-escaped.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var full = Path.GetFullPath(FixturePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var current = File.Exists(full) ? File.ReadAllText(full).ReplaceLineEndings("\n") : null;
        if (current != json.ReplaceLineEndings("\n"))
        {
            File.WriteAllText(full, json);
            Assert.Fail($"Signing vectors regenerated at {full}. Commit the file and re-run.");
        }
    }
}
