using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

/// <summary>Parsing and filtering for model discovery, with no HTTP involved.
///
/// The interesting part of discovery is deciding what counts as a chat model, and it is separated from the
/// fetch precisely so it can be tested without a server to talk to - the same split the Python worker uses
/// for its segment shaping.</summary>
public class LlmModelDiscoveryTests
{
    private const string LmStudioJson = """
        {"data":[
          {"id":"qwen3.8-27b@q4_k_xl","type":"llm","max_context_length":200000},
          {"id":"text-embedding-nomic-embed-text-v2","type":"embeddings","max_context_length":2048},
          {"id":"gemma-3-27b-vision","type":"vlm","max_context_length":8192}
        ]}
        """;

    private const string OpenAiJson = """
        {"object":"list","data":[
          {"id":"gpt-4o","object":"model"},
          {"id":"text-embedding-3-large","object":"model"},
          {"id":"whisper-1","object":"model"}
        ]}
        """;

    [Fact]
    public void Reads_lm_studios_type_and_context_length()
    {
        var models = LlmModelDiscovery.ParseLmStudio(LmStudioJson);

        Assert.Equal(3, models.Count);
        Assert.Equal(200_000, models[0].ContextLength);
        Assert.Equal("llm", models[0].Kind);
        Assert.Equal("embeddings", models[1].Kind);
    }

    [Fact]
    public void Keeps_a_vision_model_it_is_still_a_chat_model()
    {
        var vlm = LlmModelDiscovery.ParseLmStudio(LmStudioJson).Single(m => m.Kind == "vlm");
        Assert.True(LlmModelDiscovery.IsChatModel(vlm));
    }

    [Fact]
    public void Drops_an_embeddings_model_by_its_declared_type()
    {
        var embed = LlmModelDiscovery.ParseLmStudio(LmStudioJson).Single(m => m.Kind == "embeddings");
        Assert.False(LlmModelDiscovery.IsChatModel(embed));
    }

    [Fact]
    public void Reads_an_openai_listing_with_no_type_or_context_length()
    {
        var models = LlmModelDiscovery.ParseOpenAi(OpenAiJson);

        Assert.Equal(["gpt-4o", "text-embedding-3-large", "whisper-1"], models.Select(m => m.Id));
        Assert.All(models, m => Assert.Null(m.ContextLength));
        Assert.All(models, m => Assert.Null(m.Kind));
    }

    [Theory]
    [InlineData("text-embedding-3-large")]
    [InlineData("nomic-embed-text-v2")]
    [InlineData("bge-reranker-base")]
    [InlineData("whisper-1")]
    [InlineData("kokoro-tts")]
    [InlineData("clip-vit-base")]
    public void Drops_a_non_chat_model_by_name_when_no_type_is_reported(string id)
    {
        // The OpenAI-compatible listing reports only ids, so a name heuristic is the only signal there is.
        Assert.False(LlmModelDiscovery.IsChatModel(new DiscoveredModel(id, null, null)));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("qwen3.8-27b@q4_k_xl")]
    [InlineData("llama-3.3-70b-instruct")]
    [InlineData("mistral-small-3.2-24b")]
    public void Keeps_a_chat_model(string id)
    {
        Assert.True(LlmModelDiscovery.IsChatModel(new DiscoveredModel(id, null, null)));
    }

    [Fact]
    public void Matches_the_name_heuristic_case_insensitively()
    {
        Assert.False(LlmModelDiscovery.IsChatModel(new DiscoveredModel("Text-Embedding-3-Large", null, null)));
    }

    [Fact]
    public void A_declared_type_beats_the_name_heuristic()
    {
        // A chat model whose name happens to contain "embed" must survive when the server says it is an llm.
        Assert.True(LlmModelDiscovery.IsChatModel(new DiscoveredModel("embedder-chat-7b", 4096, "llm")));
    }

    [Fact]
    public void An_unknown_declared_type_is_not_a_chat_model()
    {
        // The type is the server's own answer. Falling back to the name heuristic when it says something
        // unrecognised would override a statement of fact with a guess.
        Assert.False(LlmModelDiscovery.IsChatModel(new DiscoveredModel("something-7b", 4096, "diffusion")));
    }

    [Fact]
    public void Malformed_json_yields_nothing_rather_than_throwing()
    {
        // A wrong URL is the main thing an administrator will hit. Returning empty lets the caller say
        // "no models found"; throwing would surface a 500 from OUR api that says nothing about theirs.
        Assert.Empty(LlmModelDiscovery.ParseOpenAi("not json"));
        Assert.Empty(LlmModelDiscovery.ParseLmStudio("{}"));
        Assert.Empty(LlmModelDiscovery.ParseOpenAi("""{"data":"not an array"}"""));
    }

    [Fact]
    public void Skips_an_entry_with_no_id()
    {
        var models = LlmModelDiscovery.ParseOpenAi("""{"data":[{"object":"model"},{"id":"gpt-4o"}]}""");
        Assert.Equal(["gpt-4o"], models.Select(m => m.Id));
    }

    [Fact]
    public void Defaults_an_unreported_context_length_to_the_minimum_useful_window()
    {
        // 16k, not the editor's 8k: this number drives both the chat dial and the real context budget, and
        // an import that silently under-sizes a model truncates transcript text the user believed was in
        // scope.
        Assert.Equal(16384, LlmModelDiscovery.DefaultContextLength);
    }
}
