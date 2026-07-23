using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookEventTypesTests
{
    [Fact]
    public void Matches_finds_a_type_in_the_csv()
    {
        var csv = WebhookEventTypes.Join(new[]
            { WebhookEventTypes.RecordingTranscribed, WebhookEventTypes.FormulaResultCompleted });
        Assert.True(WebhookEventTypes.Matches(csv, WebhookEventTypes.RecordingTranscribed));
        Assert.False(WebhookEventTypes.Matches(csv, WebhookEventTypes.RecordingCreated));
    }
}
