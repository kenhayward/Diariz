using Diariz.Api.Localization;

namespace Diariz.Api.Tests;

/// <summary>The BCP-47 -> Whisper mapping that lets a user pin the spoken language of a recording.
/// Whisper takes ISO-639-1 codes ("zh", "pt"), while the platform's language list carries regional and
/// script tags ("zh-CN", "pt-BR", "sr-Cyrl"). Sending one of those through unmapped would pin nothing:
/// Whisper would reject it or fall back to detecting, which is the failure being fixed.</summary>
public class TranscriptionLanguageTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh")]
    [InlineData("pt-BR", "pt")]
    [InlineData("pt-PT", "pt")]
    [InlineData("sr-Cyrl", "sr")]
    public void Maps_a_supported_code_to_its_whisper_base_subtag(string code, string expected) =>
        Assert.Equal(expected, SupportedLanguages.ToWhisperCode(code));

    [Fact]
    public void Is_case_insensitive_like_the_rest_of_the_language_lookups() =>
        Assert.Equal("pt", SupportedLanguages.ToWhisperCode("PT-br"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_an_unset_language_as_auto_detect(string? code) =>
        Assert.Null(SupportedLanguages.ToWhisperCode(code));

    /// <summary>An unrecognised code must fall back to auto-detection rather than being forwarded. A
    /// stale client sending "cy" would otherwise pin the very language this feature exists to stop.</summary>
    [Theory]
    [InlineData("cy")]
    [InlineData("klingon")]
    [InlineData("en-GB-oxendict")]
    public void Falls_back_to_auto_detect_for_a_code_the_platform_does_not_support(string code) =>
        Assert.Null(SupportedLanguages.ToWhisperCode(code));

    /// <summary>Every language the platform offers must map to something, or the picker would list an
    /// option that silently does nothing.</summary>
    [Fact]
    public void Every_supported_language_maps_to_a_whisper_code()
    {
        foreach (var lang in SupportedLanguages.All)
            Assert.False(string.IsNullOrEmpty(SupportedLanguages.ToWhisperCode(lang.Code)),
                $"{lang.Code} has no Whisper code");
    }
}
