using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranslationPromptTests
{
    private static string Response(string content)
    {
        // Shape an OpenAI-style chat completion whose message content is `content`.
        var payload = new { choices = new[] { new { message = new { content } } } };
        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void BuildMessages_NamesTheLanguage_AndEmitsIndexedItems()
    {
        var msgs = TranslationPrompt.BuildMessages("Spanish", [(0, "Hello"), (1, "World")]);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("Spanish", msgs[0].Content);
        // The user message is a JSON array of {i,t} items carrying the global indices.
        Assert.Contains("\"i\":0", msgs[1].Content);
        Assert.Contains("\"t\":\"Hello\"", msgs[1].Content);
        Assert.Contains("\"i\":1", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_EscapesText_AsValidJson()
    {
        var msgs = TranslationPrompt.BuildMessages("French", [(0, "She said \"hi\"")]);

        // The user message is well-formed JSON whose item text round-trips exactly (quotes escaped safely).
        using var doc = JsonDocument.Parse(msgs[1].Content);
        var item = doc.RootElement[0];
        Assert.Equal(0, item.GetProperty("i").GetInt32());
        Assert.Equal("She said \"hi\"", item.GetProperty("t").GetString());
    }

    [Fact]
    public void ParseTranslations_MapsByIndex()
    {
        var map = TranslationPrompt.ParseTranslations(Response("[{\"i\":0,\"t\":\"Hola\"},{\"i\":1,\"t\":\"Mundo\"}]"));

        Assert.Equal("Hola", map[0]);
        Assert.Equal("Mundo", map[1]);
    }

    [Fact]
    public void ParseTranslations_ToleratesCodeFencesAndProse()
    {
        var content = "Sure!\n```json\n[{\"i\":0,\"t\":\"Bonjour\"}]\n```";
        var map = TranslationPrompt.ParseTranslations(Response(content));

        Assert.Equal("Bonjour", map[0]);
    }

    [Fact]
    public void ParseTranslations_SkipsMalformedEntries_ButKeepsValidOnes()
    {
        var map = TranslationPrompt.ParseTranslations(
            Response("[{\"i\":0,\"t\":\"ok\"},{\"i\":\"x\",\"t\":\"bad\"},{\"nope\":1}]"));

        Assert.Single(map);
        Assert.Equal("ok", map[0]);
    }

    [Fact]
    public void ParseTranslations_ReturnsEmpty_WhenNoArray()
    {
        Assert.Empty(TranslationPrompt.ParseTranslations(Response("I cannot translate that.")));
    }
}
