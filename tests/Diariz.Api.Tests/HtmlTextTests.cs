using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class HtmlTextTests
{
    [Fact]
    public void ToPlainText_StripsTags_DropsScriptStyle_DecodesEntities()
    {
        const string html =
            "<html><head><style>.x{color:red}</style></head>" +
            "<body><h1>Title</h1><p>Hello &amp; welcome</p>" +
            "<script>alert('x')</script><div>Line&nbsp;two</div></body></html>";

        var text = HtmlText.ToPlainText(html);

        Assert.Contains("Title", text);
        Assert.Contains("Hello & welcome", text);
        Assert.Contains("Line two", text);
        Assert.DoesNotContain("color:red", text); // style dropped
        Assert.DoesNotContain("alert", text);     // script dropped
        Assert.DoesNotContain("<", text);          // no markup survives
    }

    [Fact]
    public void ToPlainText_Empty_ReturnsEmpty() => Assert.Equal("", HtmlText.ToPlainText(null));
}
