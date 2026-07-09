namespace Diariz.Api.Services;

/// <summary>Shared rule for "is this attachment editable Markdown" - true for a <c>text/markdown</c> content
/// type or a <c>.md/.markdown/.mdown</c> name. Used by both attachment controllers to gate the in-place
/// content-overwrite endpoint (the web mirror lives in <c>apps/web/src/lib/attachments.ts</c>).</summary>
public static class MarkdownAttachments
{
    private static readonly string[] Extensions = [".md", ".markdown", ".mdown"];

    public static bool IsMarkdown(string name, string? contentType) =>
        string.Equals(contentType, "text/markdown", StringComparison.OrdinalIgnoreCase)
        || Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
