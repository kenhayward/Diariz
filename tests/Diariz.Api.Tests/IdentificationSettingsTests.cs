using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The identification knobs' defaults. They are asserted rather than left implicit because a
/// deployment that never touches them runs on exactly these numbers. 0.30 is what deployments were
/// already running under the environment variable these replace, so upgrading changes no behaviour.</summary>
public class IdentificationSettingsTests
{
    [Fact]
    public void Defaults_match_the_measured_operating_point()
    {
        var s = new PlatformSettings();

        Assert.Equal(0.30, s.IdentificationThreshold, 3);
        Assert.Equal(0.40, s.IdentificationConfirmBand, 3);
        Assert.Equal(0.05, s.IdentificationMargin, 3);
        Assert.Equal(3000, s.IdentificationMinSpeechMs);
    }

    [Fact]
    public void The_confirm_band_is_looser_than_the_threshold()
    {
        // Inverted, every match would be a suggestion and nothing would ever auto-apply. They are independent
        // columns, so nothing but this and the settings endpoint's validation stops that.
        var s = new PlatformSettings();

        Assert.True(s.IdentificationConfirmBand > s.IdentificationThreshold);
    }
}
