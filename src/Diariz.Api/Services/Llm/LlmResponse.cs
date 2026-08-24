using System.Text.RegularExpressions;

namespace Diariz.Api.Services.Llm;

/// <summary>Replaces <c>EnsureSuccessStatusCode()</c> on every pipeline LLM call, so a failed call says
/// what the endpoint objected to instead of only which status it returned.
///
/// <para>WHY THIS EXISTS: <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> reads the status and
/// throws away the body. An OpenAI-compatible endpoint puts the whole reason in that body - the model is
/// not loaded, the prompt overflowed the context, this parameter is not supported - and all eight pipeline
/// clients discarded it. The message that survived was <i>"Response status code does not indicate success:
/// 400 (Bad Request)."</i>, and that string is what reached GlitchTip, <c>LlmCalls</c>, and
/// <c>Recording.Error</c>, which is rendered to the user on the failed recording. An intermittent 400 was
/// therefore undiagnosable from the logs alone.</para>
///
/// <para>The admin model editor has always done this properly - <see cref="LlmTestProbe"/> reads the error
/// body and runs <see cref="LlmErrorDiagnosis"/> over it to name the offending parameter. This is the same
/// treatment for the calls that run unattended, where it matters more, because nobody is watching.</para></summary>
public static class LlmResponse
{
    /// <summary>Cap on the endpoint's words. A model that echoes the entire prompt back inside its error
    /// would otherwise put a whole transcript into <c>Recording.Error</c> and onto the screen.</summary>
    public const int MaxErrorChars = 1000;

    /// <summary>Throws when the call failed, with the endpoint's explanation in the message. A successful
    /// response is left completely untouched - including its body, which the caller still has to read.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await SafeReadAsync(response, ct);
        throw new HttpRequestException(
            Describe((int)response.StatusCode, response.ReasonPhrase, body), null, response.StatusCode);
    }

    /// <summary>The message itself, pure so the wording is testable without a live endpoint.</summary>
    public static string Describe(int status, string? reasonPhrase, string? body)
    {
        var code = string.IsNullOrWhiteSpace(reasonPhrase) ? $"{status}" : $"{status} ({reasonPhrase})";
        var said = Flatten(body);

        if (said.Length == 0) return $"The AI model endpoint returned {code} with no explanation.";

        // When the endpoint blamed one of the parameters Diariz sends, say which - that is the one detail
        // an administrator can act on directly, and it is the same diagnosis the model editor offers a
        // one-click fix for.
        var offender = LlmErrorDiagnosis.OffendingParameter(said);
        var blame = offender is null ? "" : $" It objected to the \"{offender}\" parameter.";

        return $"The AI model endpoint returned {code}: {said}{blame}";
    }

    /// <summary>Trimmed, collapsed to a single line, and bounded. One line because this lands in a database
    /// column and on the screen, where a pretty-printed JSON error would take over the panel.</summary>
    private static string Flatten(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        var flat = Regex.Replace(body, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
        return flat.Length <= MaxErrorChars ? flat : flat[..MaxErrorChars] + "...";
    }

    /// <summary>Reading the error body must never replace the real failure with a different one, so a read
    /// that throws yields nothing and lets the status alone describe the call.</summary>
    private static async Task<string?> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
