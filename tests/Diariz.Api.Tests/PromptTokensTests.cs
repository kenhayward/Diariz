using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The $USERNAME token: a prompt-time substitution, deliberately separate from the {{field}}
/// mechanism, which is output-only and never enters a prompt (see TemplateFields).</summary>
public class PromptTokensTests
{
    [Fact]
    public void Substitute_ReplacesTheToken()
    {
        Assert.Equal(
            "What role did Ada Lovelace play?",
            PromptTokens.Substitute("What role did $USERNAME play?", "Ada Lovelace"));
    }

    [Fact]
    public void Substitute_ReplacesEveryOccurrence()
    {
        Assert.Equal(
            "Ken said. Ken listened.",
            PromptTokens.Substitute("$USERNAME said. $USERNAME listened.", "Ken"));
    }

    /// <summary>The word boundary is the whole reason this is a regex and not a string replace: a literal
    /// $USERNAMES in someone's prompt must survive rather than becoming "KenS".</summary>
    [Fact]
    public void Substitute_LeavesALongerTokenAlone()
    {
        Assert.Equal("$USERNAMES and $USERNAME_ID", PromptTokens.Substitute("$USERNAMES and $USERNAME_ID", "Ken"));
    }

    [Fact]
    public void Substitute_IsCaseSensitive()
    {
        Assert.Equal("$username", PromptTokens.Substitute("$username", "Ken"));
    }

    /// <summary>Leaving the token in place reads as an obvious fault. Substituting an empty string would
    /// produce "What role did  play?", which reads as the model having failed instead.</summary>
    [Fact]
    public void Substitute_LeavesTheTokenWhenThereIsNoName()
    {
        Assert.Equal("Ask $USERNAME", PromptTokens.Substitute("Ask $USERNAME", null));
        Assert.Equal("Ask $USERNAME", PromptTokens.Substitute("Ask $USERNAME", "   "));
    }

    [Fact]
    public void Substitute_LeavesUnrelatedDollarTextAlone()
    {
        Assert.Equal("Costs $500 and $USER stuff", PromptTokens.Substitute("Costs $500 and $USER stuff", "Ken"));
    }

    [Fact]
    public void Substitute_HandlesNullText()
    {
        Assert.Equal("", PromptTokens.Substitute(null, "Ken"));
    }

    /// <summary>Prompt blocks AND literal blocks AND section titles, in one pass - so the token means the
    /// same thing wherever it appears in a formula.</summary>
    [Fact]
    public void Apply_SubstitutesTitlesPromptsAndBoilerplate()
    {
        var content = new TemplateContent([
            new TemplateSection(1, "Notes for $USERNAME", [
                new TemplateBlock(TemplateBlock.Prompt, Text: "What did $USERNAME decide?"),
                new TemplateBlock(TemplateBlock.Boilerplate, Text: "Prepared for $USERNAME."),
                new TemplateBlock(TemplateBlock.FieldKind, Field: "date"),
                new TemplateBlock(TemplateBlock.HorizontalLine),
            ]),
        ]);

        var applied = PromptTokens.Apply(content, "Ada Lovelace");

        Assert.Equal("Notes for Ada Lovelace", applied.Sections[0].Title);
        Assert.Equal("What did Ada Lovelace decide?", applied.Sections[0].Blocks[0].Text);
        Assert.Equal("Prepared for Ada Lovelace.", applied.Sections[0].Blocks[1].Text);
        Assert.Equal("date", applied.Sections[0].Blocks[2].Field); // untouched
        Assert.Null(applied.Sections[0].Blocks[3].Text);           // hr carries no text
    }

    [Fact]
    public void Apply_ReturnsTheSameContent_WhenThereIsNoName()
    {
        var content = TemplateContent.FromPrompt("Ask $USERNAME");

        Assert.Same(content, PromptTokens.Apply(content, null));
    }

    /// <summary>A template with no sections at all (a brand-new formula) must not blow up on the walk.</summary>
    [Fact]
    public void Apply_HandlesEmptyContent()
    {
        Assert.Empty(PromptTokens.Apply(TemplateContent.Empty, "Ken").Sections);
    }
}
