using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Duplicate detection only ever reports; the merge is always a human's decision, because it
/// destroys the source row, has no undo, and under a shared directory damages everyone's recordings.</summary>
public class PersonDuplicatesTests
{
    private static Person P(string name, string? email = null) =>
        new() { Id = Guid.NewGuid(), Name = name, Email = email };

    [Fact]
    public void Find_GroupsByEmail_IgnoringCaseAndSurroundingSpace()
    {
        var groups = PersonDuplicates.Find([
            P("Ada Lovelace", "ada@example.com"),
            P("A. Lovelace", " ADA@Example.com "),
            P("Someone Else", "else@example.com"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(PersonDuplicates.ReasonEmail, group.Reason);
        Assert.Equal(2, group.People.Count);
    }

    [Fact]
    public void Find_GroupsByName_IgnoringCaseAndCollapsingWhitespace()
    {
        var groups = PersonDuplicates.Find([
            P("Ada  Lovelace "),
            P("ada lovelace"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(PersonDuplicates.ReasonName, group.Reason);
        Assert.Equal(2, group.People.Count);
    }

    /// <summary>A missing email is not a shared one. Most of the directory has no email, so grouping on null
    /// would report the whole thing as one enormous duplicate.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_NeverGroupsOnAMissingEmail(string? email)
    {
        var groups = PersonDuplicates.Find([P("Ada Lovelace", email), P("Grace Hopper", email)]);

        Assert.Empty(groups);
    }

    [Fact]
    public void Find_ASinglePerson_IsNotAGroup()
    {
        Assert.Empty(PersonDuplicates.Find([P("Ada Lovelace", "ada@example.com")]));
    }

    /// <summary>Email is the stronger signal, so a pair already caught by it is not reported a second time
    /// under name - otherwise the UI shows the same merge twice.</summary>
    [Fact]
    public void Find_DoesNotReportTheSamePairTwice()
    {
        var groups = PersonDuplicates.Find([
            P("Ada Lovelace", "ada@example.com"),
            P("Ada Lovelace", "ADA@example.com"),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(PersonDuplicates.ReasonEmail, group.Reason);
    }

    [Fact]
    public void Find_OnAnEmptyDirectory_ReturnsNothing()
    {
        Assert.Empty(PersonDuplicates.Find([]));
    }
}
