using System.Text.Json;
using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

/// <summary>The outbound envelope is a public contract, so its property names must be uniformly camelCase.
/// The sibling fields are anonymous-object literals already written in camelCase, but a nested C# record
/// (<see cref="WebhookLinks"/>) serialises with its PascalCase property names unless a naming policy is set -
/// which is how <c>data.links.Api</c> shipped alongside <c>data.recordingId</c>.</summary>
public class WebhookPayloadTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 8, 36, 9, TimeSpan.Zero);

    [Fact]
    public void Build_NestedLinks_UseCamelCaseNames()
    {
        var recordingId = Guid.Parse("368d5df5-3f1e-48df-89e2-4e3ddfb8bdc2");
        var json = WebhookPayload.Build("evt_1", WebhookEventTypes.RecordingSummarized, Created, new
        {
            recordingId,
            name = "May Calendar Arbitrary",
            links = WebhookPayload.For("https://diariz.example.com", recordingId),
        });

        var links = JsonDocument.Parse(json).RootElement.GetProperty("data").GetProperty("links");

        Assert.Equal($"https://diariz.example.com/api/recordings/{recordingId}", links.GetProperty("api").GetString());
        Assert.Equal($"https://diariz.example.com/recordings/{recordingId}", links.GetProperty("web").GetString());
    }

    /// <summary>Guards the whole envelope rather than just today's offender: any future nested record or DTO
    /// added to a payload would otherwise reintroduce PascalCase without a test noticing.</summary>
    [Fact]
    public void Build_EveryPropertyName_IsCamelCase()
    {
        var recordingId = Guid.NewGuid();
        var json = WebhookPayload.Build("evt_1", WebhookEventTypes.RecordingActionItemsReady, Created, new
        {
            recordingId,
            status = "Summarized",
            actionItems = new[] { new { id = Guid.NewGuid(), text = "Do the thing", assignee = "Ada" } },
            links = WebhookPayload.For("https://diariz.example.com", recordingId),
        });

        var offenders = new List<string>();
        Collect(JsonDocument.Parse(json).RootElement, "$", offenders);

        Assert.Empty(offenders);
    }

    private static void Collect(JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (char.IsUpper(property.Name[0])) offenders.Add($"{path}.{property.Name}");
                    Collect(property.Value, $"{path}.{property.Name}", offenders);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) Collect(item, $"{path}[{index++}]", offenders);
                break;
        }
    }
}
