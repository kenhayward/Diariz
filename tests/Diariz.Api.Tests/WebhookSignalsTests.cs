using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookSignalsTests
{
    [Fact]
    public void IsEmpty_true_for_null_or_blank()
    {
        Assert.True(WebhookSignals.IsEmpty(null));
        Assert.True(WebhookSignals.IsEmpty("  "));
        Assert.False(WebhookSignals.IsEmpty("a"));
    }

    [Fact]
    public void Intersects_true_when_a_filter_key_is_in_the_event_signals()
    {
        var filter = WebhookSignals.Join(new[] { "post-to-slack", "file-to-crm" });
        Assert.True(WebhookSignals.Intersects(filter, new[] { "file-to-crm" }));
        Assert.False(WebhookSignals.Intersects(filter, new[] { "other" }));
        Assert.False(WebhookSignals.Intersects(filter, Array.Empty<string>()));
        Assert.False(WebhookSignals.Intersects(null, new[] { "post-to-slack" }));
    }
}
