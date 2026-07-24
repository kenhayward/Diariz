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

    [Fact]
    public void Subscribable_contains_the_ai_output_events()
    {
        Assert.Contains("recording.summarized", WebhookEventTypes.Subscribable);
        Assert.Contains("recording.minutes_ready", WebhookEventTypes.Subscribable);
        Assert.Contains("recording.action_items_ready", WebhookEventTypes.Subscribable);
        Assert.Contains("recording.tags_ready", WebhookEventTypes.Subscribable);
    }

    [Fact]
    public void Subscribable_excludes_ping_and_has_no_duplicates()
    {
        Assert.DoesNotContain(WebhookEventTypes.Ping, WebhookEventTypes.Subscribable);
        Assert.Equal(WebhookEventTypes.Subscribable.Count, WebhookEventTypes.Subscribable.Distinct().Count());
        Assert.Equal(9, WebhookEventTypes.Subscribable.Count);
    }
}
