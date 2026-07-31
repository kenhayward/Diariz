using Diariz.Api.Configuration;

namespace Diariz.Api.Tests;

public class TelemetryOptionsTests
{
    [Fact]
    public void Enabled_IsFalse_WhenDsnIsEmpty()
    {
        Assert.False(new TelemetryOptions().Enabled);
    }

    [Fact]
    public void Enabled_IsFalse_WhenDsnIsOnlyWhitespace()
    {
        Assert.False(new TelemetryOptions { Dsn = "   " }.Enabled);
    }

    [Fact]
    public void Enabled_IsTrue_WhenDsnIsSet()
    {
        Assert.True(new TelemetryOptions { Dsn = "https://key@errors.example/1" }.Enabled);
    }

    [Fact]
    public void TracesSampleRate_DefaultsToCapturingEverything()
    {
        // This deployment's volume is small; the SDK docs' 1% advice targets high-traffic sites.
        Assert.Equal(1.0, new TelemetryOptions().TracesSampleRate);
    }
}
