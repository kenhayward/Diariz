using Markdig;

namespace Diariz.Api.Services;

/// <summary>Renders our own (LLM-generated or user-edited) Markdown to HTML for emails, using Markdig with
/// the advanced extensions on so GitHub-flavoured tables/pipes render. The Markdown is first-party (not
/// third-party user input injected into our DOM), so it is emitted as-is for the email body.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string ToHtml(string? markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);
}
