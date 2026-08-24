using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Prompt-time token substitution for a formula's template. Today there is exactly one token,
/// <c>$USERNAME</c>: the name the running user appears under in their own transcripts.
///
/// <para><b>Why this is not a <c>{{field}}</c>.</b> The template system already has placeholders, resolved by
/// <see cref="TemplateFields"/> - but those are deliberately OUTPUT-only: a field is stamped into the produced
/// document and never enters a prompt, which is what makes <c>{{transcript}}</c> cost no tokens. This token
/// has to reach the model, so it cannot be a field. The two conventions coexist on purpose.</para>
///
/// <para>Pure - no database, no configuration - so the substitution rules are unit-testable on their own and
/// the run pipeline only has to decide WHOSE name to pass.</para></summary>
public static partial class PromptTokens
{
    /// <summary>The word boundary is load-bearing: without it a literal <c>$USERNAMES</c> in someone's prompt
    /// would silently become "Ken HaywardS". Case-sensitive, so ordinary prose containing "$username" is left
    /// alone.</summary>
    [GeneratedRegex(@"\$USERNAME\b")]
    private static partial Regex UserNameToken();

    /// <summary>Replace <c>$USERNAME</c> in one string. A null or blank name leaves the token in place rather
    /// than deleting it: "What role did $USERNAME play" is an obvious fault, whereas "What role did  play"
    /// reads as the model having failed.</summary>
    public static string Substitute(string? text, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (string.IsNullOrWhiteSpace(userName)) return text;
        return UserNameToken().Replace(text, userName.Trim());
    }

    /// <summary>Apply <see cref="Substitute"/> across a whole template - every section title, and every
    /// block's text. Field blocks and horizontal rules carry no text and are copied through untouched.
    /// Returns the input unchanged when there is no name to substitute, so the common path allocates
    /// nothing.</summary>
    public static TemplateContent Apply(TemplateContent content, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return content;

        return content with
        {
            Sections = (content.Sections ?? []).Select(section => section with
            {
                Title = Substitute(section.Title, userName),
                Blocks = (section.Blocks ?? []).Select(block =>
                    block.Text is null ? block : block with { Text = Substitute(block.Text, userName) }).ToList(),
            }).ToList(),
        };
    }
}
