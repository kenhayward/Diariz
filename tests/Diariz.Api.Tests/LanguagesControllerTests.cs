using Diariz.Api.Controllers;
using Diariz.Api.Localization;

namespace Diariz.Api.Tests;

public class LanguagesControllerTests
{
    [Fact]
    public void Get_ReturnsTheSupportedLanguages_IncludingEnglish()
    {
        var langs = new LanguagesController().Get();

        Assert.NotEmpty(langs);
        var en = Assert.Single(langs, l => l.Code == "en");
        Assert.Equal("English", en.EnglishName);
        Assert.False(en.Rtl);
    }

    [Fact]
    public void Get_MarksRightToLeftScripts()
    {
        var langs = new LanguagesController().Get();

        Assert.True(Assert.Single(langs, l => l.Code == "ar").Rtl);   // Arabic
        Assert.True(Assert.Single(langs, l => l.Code == "he").Rtl);   // Hebrew
        Assert.False(Assert.Single(langs, l => l.Code == "fr").Rtl);  // French is LTR
    }

    [Fact]
    public void Codes_AreUnique_AndCarryNativeNames()
    {
        var langs = new LanguagesController().Get();

        Assert.Equal(langs.Count, langs.Select(l => l.Code).Distinct().Count());
        Assert.All(langs, l => Assert.False(string.IsNullOrWhiteSpace(l.NativeName)));
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("EN", true)]      // case-insensitive
    [InlineData("pt-BR", true)]
    [InlineData("klingon", false)]
    [InlineData(null, false)]
    public void IsSupported_ValidatesCodes(string? code, bool expected)
    {
        Assert.Equal(expected, SupportedLanguages.IsSupported(code));
    }
}
