using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

/// <summary>Reading which parameter an endpoint rejected out of its own error text.
///
/// This is what turns a 400 into a one-click fix. It is deliberately a lookup against the thirteen names
/// Diariz can actually send rather than a general parser: an endpoint's error format is its own business
/// and changes without notice, but the parameter it names is always one of ours.</summary>
public class LlmErrorDiagnosisTests
{
    [Theory]
    // vLLM / OpenAI-compatible
    [InlineData(
        """{"error":{"message":"Unrecognized request argument supplied: top_k","type":"invalid_request_error"}}""",
        "top_k")]
    // Ollama
    [InlineData("""{"error":"invalid option provided: repeat_penalty"}""", "repeat_penalty")]
    // Plain text, no JSON at all
    [InlineData("Bad Request: 'presence_penalty' is not supported by this model", "presence_penalty")]
    public void Names_the_parameter_an_endpoint_rejected(string body, string expected) =>
        Assert.Equal(expected, LlmErrorDiagnosis.OffendingParameter(body));

    [Theory]
    [InlineData("""{"error":{"message":"Model not found","code":404}}""")]
    [InlineData("Internal Server Error")]
    [InlineData("")]
    [InlineData(null)]
    public void Names_nothing_when_the_error_blames_nothing_we_send(string? body) =>
        Assert.Null(LlmErrorDiagnosis.OffendingParameter(body));

    [Fact]
    public void Ignores_a_name_that_is_only_part_of_a_longer_word()
    {
        // "temperatures" is not "temperature", and offering to omit a parameter the endpoint never
        // mentioned would be a fix that makes the call worse for no reason.
        Assert.Null(LlmErrorDiagnosis.OffendingParameter("the temperatures reported by the GPU are too high"));
    }

    [Fact]
    public void Reads_a_name_the_endpoint_shouted()
    {
        // Some servers upper-case the field in their diagnostics. The parameter is still ours.
        Assert.Equal("top_p", LlmErrorDiagnosis.OffendingParameter("TOP_P out of range"));
    }

    [Fact]
    public void Prefers_the_parameter_the_error_names_first()
    {
        // An error listing what it accepts after naming what it rejected must not report the last one.
        Assert.Equal(
            "top_k",
            LlmErrorDiagnosis.OffendingParameter("top_k is not supported; use temperature or top_p instead"));
    }
}
