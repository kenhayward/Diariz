using Diariz.Api.Services;
using Sentry;   // SentryEvent; reaches the test project transitively via the API project reference

namespace Diariz.Api.Tests;

public class SentryScrubberTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Worker-Secret")]
    [InlineData("Cookie")]
    [InlineData("password")]
    [InlineData("ApiKey")]
    [InlineData("Summarization__ApiKey")]
    public void IsSensitiveKey_MatchesCredentials(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("transcript")]
    [InlineData("segments")]
    [InlineData("summary")]
    [InlineData("minutes")]
    public void IsSensitiveKey_MatchesMeetingContent(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("recordingId")]
    [InlineData("transcriptionId")]
    [InlineData("blobKey")]
    [InlineData("userId")]
    [InlineData("model")]
    public void IsSensitiveKey_KeepsTheIdentifiersNeededToDiagnose(string key)
    {
        // A scrubber that redacts everything is useless.
        Assert.False(SentryScrubber.IsSensitiveKey(key));
    }

    [Fact]
    public void Scrub_RedactsSensitiveExtras_AndKeepsIdentifiers()
    {
        var e = new SentryEvent();
        e.SetExtra("transcriptionId", "tid-1");
        e.SetExtra("text", "the confidential meeting content");

        var cleaned = SentryScrubber.Scrub(e);

        Assert.NotNull(cleaned);
        Assert.Equal("tid-1", cleaned!.Extra["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Extra["text"]);
    }
}
