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

    /// <summary>Groups of two or more likely-duplicate people. Email is the stronger signal, so a pair caught
    /// by email is not reported again under name.</summary>
    public static IReadOnlyList<PersonDuplicateGroup> Find(IEnumerable<Person> people)
    {
        var all = people.ToList();
        var groups = new List<PersonDuplicateGroup>();
        var grouped = new HashSet<Guid>();

        foreach (var group in all
                     .Where(p => !string.IsNullOrWhiteSpace(p.Email))
                     .GroupBy(p => p.Email!.Trim().ToLowerInvariant())
                     .Where(g => g.Count() > 1))
        {
            groups.Add(new PersonDuplicateGroup(ReasonEmail, group.ToList()));
            foreach (var p in group) grouped.Add(p.Id);
        }

        foreach (var group in all
                     .Where(p => !grouped.Contains(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                     .GroupBy(p => NormalizeName(p.Name))
                     .Where(g => g.Count() > 1))
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
