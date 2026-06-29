using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ExportLocalizationTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello."),
    ];

    private static readonly IReadOnlyList<RecordingActionDto> Actions =
    [
        new RecordingActionDto(Guid.NewGuid(), "Send the report", "Bob", "Friday", 0),
    ];

    // ---- JsonExportLocalizer ----

    /// <summary>Writes a partial Spanish catalog (only summary + transcript) so per-key fallback is exercised.</summary>
    private static string WriteCatalogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "diariz-exports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "es"));
        File.WriteAllText(Path.Combine(root, "es", "exports.json"),
            "{\"summary\":\"Resumen\",\"transcript\":\"Transcripción\",\"subject\":\"Transcripción de {name}\"}");
        return root;
    }

    [Fact]
    public void For_ReturnsLocalizedValues_FallingBackPerKeyToEnglish()
    {
        var root = WriteCatalogs();
        try
        {
            var es = new JsonExportLocalizer(root).For("es");
            Assert.Equal("Resumen", es.Summary);
            Assert.Equal("Transcripción", es.Transcript);
            Assert.Equal("Actions", es.Actions); // absent from the catalog → English
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void For_MatchesByBaseSubtag()
    {
        var root = WriteCatalogs();
        try { Assert.Equal("Resumen", new JsonExportLocalizer(root).For("es-ES").Summary); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void For_UnknownOrNullCulture_IsEnglish()
    {
        var root = WriteCatalogs();
        try
        {
            var loc = new JsonExportLocalizer(root);
            Assert.Equal(ExportStrings.English.Summary, loc.For("klingon").Summary);
            Assert.Equal(ExportStrings.English, loc.For(null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingRoot_IsAllEnglish()
    {
        var loc = new JsonExportLocalizer(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(ExportStrings.English, loc.For("es"));
    }

    // ---- Formatters honour the supplied labels ----

    private static readonly ExportStrings Es = ExportStrings.English with
    {
        TranscriptName = "Nombre de la transcripción",
        Summary = "Resumen",
        Transcript = "Transcripción",
        Subject = "Transcripción de {name}",
        SentFromDiariz = "Enviado desde Diariz",
    };

    [Fact]
    public void ToText_UsesLocalizedHeadings()
    {
        var text = TranscriptFormatter.ToText("Team Sync", "We discussed the API.", Segments, Actions, Es);
        Assert.Contains("Nombre de la transcripción\nTeam Sync", text);
        Assert.Contains("Resumen\nWe discussed the API.", text);
        Assert.Contains("Transcripción\n", text);
    }

    [Fact]
    public void ToMarkdown_UsesLocalizedHeadings()
    {
        var md = TranscriptFormatter.ToMarkdown("Team Sync", null, Segments, null, Es);
        Assert.Contains("## Resumen", md);
        Assert.Contains("## Transcripción", md);
    }

    [Fact]
    public void ToText_DefaultsToEnglish_WhenNoStrings()
    {
        // Backward-compatible: omitting the strings argument keeps English headings.
        Assert.Contains("Summary\n", TranscriptFormatter.ToText("X", "Y", Segments));
    }

    [Fact]
    public void BuildHtml_AndSubject_UseLocalizedStrings()
    {
        var html = TranscriptEmail.BuildHtml("X", "A summary.", Segments, null, Es);
        Assert.Contains("<strong>Resumen</strong>", html);
        Assert.Contains("Enviado desde Diariz", html);
        Assert.Equal("Transcripción de X", TranscriptEmail.Subject("X", Es));
    }
}
