namespace Diariz.Domain.Entities;

/// <summary>Coarse capability of a personal API token. Append only - values persist as ints.
/// New tokens choose one at mint time; existing tokens default to <see cref="ReadWrite"/>.</summary>
public enum ApiTokenScope
{
    /// <summary>Safe verbs only (GET/HEAD). Unsafe verbs (POST/PUT/PATCH/DELETE) are rejected with 403.</summary>
    ReadOnly = 0,

    /// <summary>Full access - the historical behaviour of every token.</summary>
    ReadWrite = 1,
}
