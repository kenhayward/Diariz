using Diariz.Api.Webhooks;

namespace Diariz.Api.Tests;

public class WebhookBackoffTests
{
    [Fact]
    public void Schedule_is_monotonic_and_bounded()
    {
        Assert.Equal(8, WebhookBackoff.MaxAttempts);
        var delays = Enumerable.Range(1, WebhookBackoff.MaxAttempts).Select(WebhookBackoff.NextDelay).ToList();
        for (var i = 1; i < delays.Count; i++)
            Assert.True(delays[i] >= delays[i - 1], "delays must be non-decreasing");
        Assert.True(delays[0] <= TimeSpan.FromSeconds(30));   // first retry is soon
        Assert.True(delays[^1] >= TimeSpan.FromHours(1));     // last retry is far out
    }
}
