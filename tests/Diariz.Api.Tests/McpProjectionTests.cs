using System.Text.Json;
using Diariz.Api.Mcp;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests;

public class McpLinkRewriterTests
{
    [Fact]
    public void Rewrite_MakesRecordingLinksAbsolute()
    {
        const string text = "See [Budget @ 04:12](/recordings/abc?t=252000) for details.";
        var result = McpLinkRewriter.Rewrite(text, "https://diariz.example.com");
        Assert.Equal("See [Budget @ 04:12](https://diariz.example.com/recordings/abc?t=252000) for details.", result);
    }

    [Fact]
    public void Rewrite_RewritesEveryLink_AndNormalisesTrailingSlash()
    {
        const string text = "[A](/recordings/1) and [B](/recordings/2?t=1000)";
        var result = McpLinkRewriter.Rewrite(text, "https://host/");
        Assert.Equal("[A](https://host/recordings/1) and [B](https://host/recordings/2?t=1000)", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rewrite_NoBase_LeavesTextUnchanged(string? baseUrl)
    {
        const string text = "[A](/recordings/1)";
        Assert.Equal(text, McpLinkRewriter.Rewrite(text, baseUrl));
    }

    [Fact]
    public void Rewrite_LeavesNonRecordingContentAlone()
    {
        const string text = "No links here, just prose about /recordings in passing.";
        Assert.Equal(text, McpLinkRewriter.Rewrite(text, "https://host"));
    }
}

public class McpToolProjectionTests
{
    [Fact]
    public void Expose_ExcludesAddAsAttachment_AndPreservesOrderOfTheRest()
    {
        IChatTool[] tools =
        [
            new StubChatTool("search_transcripts"),
            new StubChatTool("add_as_attachment"),
            new StubChatTool("send_email"),
        ];

        var exposed = McpToolProjection.Expose(tools);

        Assert.Equal(["search_transcripts", "send_email"], exposed.Select(t => t.Name));
    }

    [Fact]
    public void InputSchema_SerialisesTheToolsParametersSchema()
    {
        var schema = McpToolProjection.InputSchema(new StubChatTool("x"));
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }
}
