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

    /// <summary>The bug this fixes, from production. Lizzie Mcneil had been given the email address that
    /// belongs to Ada's account person, so those two were reported as an email duplicate - and that
    /// <b>consumed</b> Ada's account person, making the genuine "Ada Lovelace" / "Ada lovelace" name pair
    /// impossible to report. The user only found it by editing Lizzie's email to break the coincidence.
    ///
    /// A weak email coincidence must not suppress a strong name match. One person may therefore appear in
    /// more than one suggestion: they are different claims about different pairs, and each deserves its own
    /// answer.</summary>
    [Fact]
    public void Find_AnEmailCoincidence_DoesNotSuppressAGenuineNameMatch()
    {
        var enrolled = P("Ada Lovelace");
        var account = P("Ada lovelace", "ada@example.com");
        var unrelated = P("Lizzie Mcneil", "ada@example.com");

        var groups = PersonDuplicates.Find([enrolled, account, unrelated]);

        var byEmail = Assert.Single(groups, g => g.Reason == PersonDuplicates.ReasonEmail);
        Assert.Equal(
            new[] { account.Id, unrelated.Id }.Order(), byEmail.People.Select(x => x.Id).Order());

        var byName = Assert.Single(groups, g => g.Reason == PersonDuplicates.ReasonName);
        Assert.Equal(
            new[] { enrolled.Id, account.Id }.Order(), byName.People.Select(x => x.Id).Order());
    }

    /// <summary>Suppression is by the pair, not by the people in it: the same two people caught twice is one
    /// suggestion, but a person caught with two different people is two.</summary>
    [Fact]
    public void Find_ReportsEveryDistinctPair_EvenWhenAPersonIsInSeveral()
    {
        var groups = PersonDuplicates.Find([
            P("Sam Poole", "sam@example.com"),
            P("Samantha Poole", "sam@example.com"),
            P("Sam Poole", "different@example.com"),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Reason == PersonDuplicates.ReasonEmail);
        Assert.Contains(groups, g => g.Reason == PersonDuplicates.ReasonName);
    }

    [Fact]
    public void Find_OnAnEmptyDirectory_ReturnsNothing()
    {
        Assert.Empty(PersonDuplicates.Find([]));
    }
}
