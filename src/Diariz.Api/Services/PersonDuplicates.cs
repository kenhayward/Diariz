using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>A set of people who look like the same human, and why they were grouped.</summary>
public record PersonDuplicateGroup(string Reason, IReadOnlyList<Person> People);

/// <summary>Finds people who are probably the same human. Going platform-wide surfaces this: two users who
/// each enrolled the same colleague privately now both appear in one directory.
///
/// <para><b>Never merges anything.</b> It reports candidates and stops. Merge destroys the source row and has
/// no undo, and under a shared directory a bad merge damages everyone's recordings rather than one person's -
/// so folding two users' independent enrolments together silently would be exactly the wrong default.</para>
///
/// <para>Pure and static so it can be unit-tested on a list, with no database.</para></summary>
public static class PersonDuplicates
{
    public const string ReasonEmail = "email";
    public const string ReasonName = "name";

    /// <summary>Groups of two or more likely-duplicate people. Email is the stronger signal, so a set of
    /// people caught by email is not reported a second time under name.
    ///
    /// <para><b>Suppression is by the set, not by the people in it</b>, and the difference is the whole point.
    /// Excluding people from the name pass once they appeared in an email group meant a weak email
    /// coincidence silently destroyed a strong name match: in production, one person had been given an email
    /// address belonging to someone else's account, which grouped those two and thereby made a genuine
    /// same-name pair impossible to report at all. It was only found by editing the email to break the
    /// coincidence.</para>
    ///
    /// <para>So one person may appear in several suggestions. Each is a separate claim about a separate pair
    /// and deserves its own answer; the UI lets a suggestion be dismissed for exactly this reason.</para>
    /// </summary>
    public static IReadOnlyList<PersonDuplicateGroup> Find(IEnumerable<Person> people)
    {
        var all = people.ToList();
        var groups = new List<PersonDuplicateGroup>();
        var reported = new HashSet<string>();

        static string SetKey(IEnumerable<Person> group) =>
            string.Join(",", group.Select(p => p.Id).Order());

        foreach (var group in all
                     .Where(p => !string.IsNullOrWhiteSpace(p.Email))
                     .GroupBy(p => p.Email!.Trim().ToLowerInvariant())
                     .Where(g => g.Count() > 1))
        {
            groups.Add(new PersonDuplicateGroup(ReasonEmail, group.ToList()));
            reported.Add(SetKey(group));
        }

        foreach (var group in all
                     .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                     .GroupBy(p => NormalizeName(p.Name))
                     .Where(g => g.Count() > 1 && !reported.Contains(SetKey(g))))
        {
            groups.Add(new PersonDuplicateGroup(ReasonName, group.ToList()));
        }

        return groups;
    }

    /// <summary>Casefold, trim, and collapse internal runs of whitespace, so "Ada  Lovelace " and
    /// "ada lovelace" are the same person.</summary>
    private static string NormalizeName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
