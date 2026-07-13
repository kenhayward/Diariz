using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The built-in-formula markdown loader: a pure frontmatter parser (no I/O) plus a resilient
/// directory loader. The shipped formulas/ folder must parse to the four Diariz-provided formulas.</summary>
public class BuiltInFormulaCatalogTests
{
    private const string Valid =
        "---\n" +
        "name: Follow-up email\n" +
        "description: Draft a follow-up email.\n" +
        "context: Transcript, Summary, Actions\n" +
        "---\n" +
        "Write a concise follow-up email.\n\nSecond paragraph.";

    [Fact]
    public void Parse_reads_all_fields()
    {
        var spec = BuiltInFormulaCatalog.Parse(Valid, "follow-up-email.md");
        Assert.Equal("Follow-up email", spec.Name);
        Assert.Equal("Draft a follow-up email.", spec.Description);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions, spec.Context);
        Assert.Equal("Write a concise follow-up email.\n\nSecond paragraph.", spec.Prompt);
    }

    [Fact]
    public void Parse_handles_crlf_and_bom()
    {
        var withCrlf = "﻿" + Valid.Replace("\n", "\r\n");
        var spec = BuiltInFormulaCatalog.Parse(withCrlf, "x.md");
        Assert.Equal("Follow-up email", spec.Name);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions, spec.Context);
    }

    [Fact]
    public void Parse_missing_context_defaults_to_none()
    {
        var text = "---\nname: X\n---\nBody.";
        var spec = BuiltInFormulaCatalog.Parse(text, "x.md");
        Assert.Equal(FormulaContext.None, spec.Context);
        Assert.Null(spec.Description);
    }

    [Theory]
    [InlineData("no frontmatter at all")]                       // missing opening ---
    [InlineData("---\nname: X\nBody without closing fence")]    // unterminated frontmatter
    [InlineData("---\ndescription: no name\n---\nBody.")]       // missing required name
    [InlineData("---\nname: X\ncontext: Bogus\n---\nBody.")]    // invalid context flag
    [InlineData("---\nname: X\n---\n   \n")]                    // empty body
    public void Parse_rejects_malformed(string text)
    {
        Assert.Throws<FormatException>(() => BuiltInFormulaCatalog.Parse(text, "bad.md"));
    }

    [Fact]
    public void LoadFrom_missing_directory_returns_empty()
    {
        Assert.Empty(BuiltInFormulaCatalog.LoadFrom(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())));
    }

    [Fact]
    public void LoadFrom_skips_a_malformed_file_but_keeps_the_good_ones()
    {
        var dir = Path.Combine(Path.GetTempPath(), "formulas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.md"), Valid);
            File.WriteAllText(Path.Combine(dir, "bad.md"), "not a formula");
            var specs = BuiltInFormulaCatalog.LoadFrom(dir);
            Assert.Single(specs);
            Assert.Equal("Follow-up email", specs[0].Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The real shipped folder (copied into the test output via the csproj content link) must parse
    /// to exactly the four Diariz-provided formulas with their expected context masks - behaviour-preserving
    /// vs the old C# literals.</summary>
    [Fact]
    public void Shipped_folder_parses_to_the_four_builtins()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "formulas");
        var specs = BuiltInFormulaCatalog.LoadFrom(dir);
        Assert.Equal(4, specs.Count);
        Assert.All(specs, s => Assert.False(string.IsNullOrWhiteSpace(s.Prompt)));

        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions,
            specs.Single(s => s.Name == "Follow-up email").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary,
            specs.Single(s => s.Name == "Meeting recap").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions,
            specs.Single(s => s.Name == "Decisions & risks").Context);
        Assert.Equal(FormulaContext.Transcript,
            specs.Single(s => s.Name == "Tone & sentiment read").Context);
    }
}
