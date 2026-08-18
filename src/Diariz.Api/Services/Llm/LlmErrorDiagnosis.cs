using System.Text.RegularExpressions;

namespace Diariz.Api.Services.Llm;

/// <summary>Works out which parameter an endpoint rejected, from whatever it said about it.
///
/// This is what turns an opaque 400 into a one-click fix in the model editor: an endpoint that refuses
/// <c>top_k</c> is telling the administrator exactly which row to omit, and without this they have to read
/// the raw error and know that omitting is even an option.
///
/// Deliberately a LOOKUP over the thirteen names Diariz can send, not a parser for anyone's error format.
/// Every endpoint words its errors differently and changes them without notice, but the parameter it is
/// complaining about is always one of ours - so matching on our own vocabulary is both simpler and more
/// durable than trying to understand theirs.</summary>
public static class LlmErrorDiagnosis
{
    /// <summary>The first parameter name the error mentions, or null when it blames nothing Diariz sends.
    ///
    /// Word-bounded so "the temperatures reported by the GPU" is not read as a complaint about
    /// <c>temperature</c> - offering to omit a parameter the endpoint never mentioned would be a "fix" that
    /// changes the request for no reason. First-mentioned rather than any, because an error commonly names
    /// what it rejected and then lists what it accepts instead.</summary>
    public static string? OffendingParameter(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return null;

        string? best = null;
        var bestAt = int.MaxValue;

        foreach (var name in LlmParameterLayers.ParameterNames)
        {
            var match = Regex.Match(
                errorBody,
                $@"(?<![0-9a-z_]){Regex.Escape(name)}(?![0-9a-z_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            if (match.Success && match.Index < bestAt)
            {
                best = name;
                bestAt = match.Index;
            }
        }

        return best;
    }
}
