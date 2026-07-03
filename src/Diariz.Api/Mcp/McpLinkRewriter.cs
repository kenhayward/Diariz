namespace Diariz.Api.Mcp;

/// <summary>Rewrites the in-app relative deep-links the transcript tools emit (e.g.
/// <c>[Budget @ 04:12](/recordings/…?t=…)</c>) into absolute URLs against the public web origin, so they are
/// clickable from an MCP client (Claude) rather than resolving relative to nothing. Pure/static for testing.</summary>
public static class McpLinkRewriter
{
    public static string Rewrite(string? text, string? webBaseUrl)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(webBaseUrl))
            return text ?? "";
        var baseUrl = webBaseUrl.TrimEnd('/');
        // The tool helpers always render recording links as markdown with an href beginning "/recordings".
        return text.Replace("](/recordings", $"]({baseUrl}/recordings");
    }
}
