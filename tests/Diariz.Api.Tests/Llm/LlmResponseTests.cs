using System.Net;
using System.Text;
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

/// <summary>What a failed model call says for itself.
///
/// The whole point of this type is that the endpoint's own words survive into the message, because that
/// message is what reaches GlitchTip, the usage log, and the Error shown on the failed recording. Every
/// assertion here is really the same assertion: the explanation must not be thrown away.</summary>
public class LlmResponseTests
{
    [Fact]
    public void Describe_CarriesTheEndpointsExplanation()
    {
        var message = LlmResponse.Describe(
            400, "Bad Request", """{"error":"Model is not loaded. Load it first."}""");

        Assert.Contains("400 (Bad Request)", message);
        Assert.Contains("Model is not loaded", message);
    }

    [Fact]
    public void Describe_NamesTheParameterTheEndpointBlamed()
    {
        var message = LlmResponse.Describe(
            400, "Bad Request", """{"error":"Unsupported parameter: 'top_k' is not supported."}""");

        // Same diagnosis the model editor offers a one-click fix for - see LlmErrorDiagnosis.
        Assert.Contains("top_k", message);
    }

    [Fact]
    public void Describe_SaysSoWhenTheEndpointExplainedNothing()
    {
        var message = LlmResponse.Describe(500, "Internal Server Error", "   ");

        Assert.Contains("500 (Internal Server Error)", message);
        Assert.Contains("no explanation", message);
    }

    [Fact]
    public void Describe_BoundsAVeryLongBody()
    {
        var message = LlmResponse.Describe(400, "Bad Request", new string('x', 50_000));

        // A model that echoes the whole prompt back in its error must not put the whole prompt into
        // Recording.Error, which is rendered in the UI.
        Assert.True(
            message.Length < LlmResponse.MaxErrorChars + 200,
            $"message was {message.Length} chars");
    }

    [Fact]
    public void Describe_CollapsesTheBodyToOneLine()
    {
        var message = LlmResponse.Describe(400, "Bad Request", "line one\n\nline two\r\n  line three");

        Assert.DoesNotContain('\n', message);
        Assert.Contains("line one line two line three", message);
    }

    [Fact]
    public async Task EnsureSuccessAsync_IsSilentOnSuccess()
    {
        using var ok = Response(HttpStatusCode.OK, """{"choices":[]}""");

        await LlmResponse.EnsureSuccessAsync(ok);
    }

    [Fact]
    public async Task EnsureSuccessAsync_ThrowsWithTheBody_AndKeepsTheStatusCode()
    {
        using var bad = Response(HttpStatusCode.BadRequest, """{"error":"context overflow"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => LlmResponse.EnsureSuccessAsync(bad));

        Assert.Contains("context overflow", ex.Message);
        // Kept so callers (and any future retry policy) can still classify by status.
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
