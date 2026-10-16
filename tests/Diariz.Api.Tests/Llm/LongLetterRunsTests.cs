using System.Text;
using System.Text.Json;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests.Llm;

/// <summary>Breaking unbroken letter runs before a prompt leaves for the model (issue #841).
///
/// <para>llama.cpp's gpt-oss tokenizer, as served by LM Studio, refuses a whole request with
/// "Failed to process regex" when any single run of letters is longer than about 200 characters - measured at
/// 190 passing and 200 failing for ASCII, ~220 for real Japanese. Japanese and Chinese have no spaces between
/// words, and Whisper's repetition loops emit 250-letter "words", so ordinary transcripts reach that.</para></summary>
public class LongLetterRunsTests
{
    private static string Letters(int n) =>
        string.Concat(Enumerable.Range(0, n).Select(i => (char)('a' + i % 11)));

    private static int LongestLetterRun(string s)
    {
        int best = 0, run = 0;
        foreach (var r in s.EnumerateRunes())
        {
            run = LongLetterRuns.IsRunCharacter(r) ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
    }

    [Fact]
    public void OrdinaryTextIsReturnedUnchanged()
    {
        const string text = "Ada: Let's move the launch to Thursday - the vendor needs two more days.";

        Assert.Same(text, LongLetterRuns.Break(text));
    }

    [Fact]
    public void ARunAtTheLimitIsLeftAlone()
    {
        var text = Letters(LongLetterRuns.MaxRun);

        Assert.Same(text, LongLetterRuns.Break(text));
    }

    [Fact]
    public void ALongAsciiRunIsSplitSoNoRunExceedsTheLimit()
    {
        // The shape of the Whisper loop seen in production: 258 letters drawn from 11, no space anywhere.
        var text = "Grace: " + Letters(258) + " and then we stopped.";

        var broken = LongLetterRuns.Break(text);

        Assert.True(LongestLetterRun(broken) <= LongLetterRuns.MaxRun);
        // Only spaces are added, and nothing is lost or reordered.
        Assert.Equal(text.Replace(" ", ""), broken.Replace(" ", ""));
        Assert.StartsWith("Grace: ", broken);
        Assert.EndsWith(" and then we stopped.", broken);
    }

    [Fact]
    public void ALongJapaneseRunIsSplit()
    {
        // Japanese is written without spaces, so one long sentence is one letter run. Kana and kanji mixed,
        // as Whisper writes it.
        var sentence = string.Concat(Enumerable.Repeat("最近のミーティングでは新しい計画について話しました", 12));

        var broken = LongLetterRuns.Break(sentence);

        Assert.True(LongestLetterRun(broken) <= LongLetterRuns.MaxRun);
        Assert.Equal(sentence, broken.Replace(" ", ""));
    }

    [Theory]
    // Anything that is not a letter already ends the run, so none of these needs touching - the tokenizer
    // splits there itself. Measured against the real endpoint: 250 letters with a '-', '.', digit or '、'
    // every 50 all pass, as do 300 digits and a 300-character URL.
    [InlineData('-')]
    [InlineData('.')]
    [InlineData('7')]
    [InlineData('、')]
    public void PunctuationAndDigitsAlreadyEndARun(char separator)
    {
        var text = string.Join(separator, Enumerable.Repeat(Letters(60), 5));

        Assert.Same(text, LongLetterRuns.Break(text));
    }

    [Fact]
    public void DigitsAndUrlsAreNeverTouched()
    {
        var digits = new string('7', 400);
        var url = "https://example.test/" + string.Concat(Enumerable.Repeat("ab/cd_ef-12", 30));

        Assert.Same(digits, LongLetterRuns.Break(digits));
        Assert.Same(url, LongLetterRuns.Break(url));
    }

    [Fact]
    public void ASurrogatePairIsNeverSplit()
    {
        // CJK Extension B ideographs are outside the BMP: two UTF-16 units, one letter. A space between the
        // halves would leave two lone surrogates, which is not valid text.
        var text = string.Concat(Enumerable.Repeat("\U00020000", 250));

        var broken = LongLetterRuns.Break(text);

        Assert.Equal(text, broken.Replace(" ", ""));
        foreach (var part in broken.Split(' '))
            Assert.True(part.Length % 2 == 0 && part.EnumerateRunes().All(r => r.Value == 0x20000));
    }

    [Fact]
    public void ASpaceIsNeverPutBeforeACombiningMark()
    {
        // 'e' + U+0301 COMBINING ACUTE: a space between them would detach the accent from its letter.
        var text = string.Concat(Enumerable.Repeat("é", 150));

        var broken = LongLetterRuns.Break(text);

        Assert.DoesNotContain(" ́", broken);
        Assert.Equal(text, broken.Replace(" ", ""));
    }

    // ---- the handler: where every LLM client's request body passes through ----

    private static async Task<string> SendThrough(string json)
    {
        var capture = new CapturingHandler("""{"choices":[]}""");
        var http = new HttpClient(new LongLetterRunHandler { InnerHandler = capture });
        using var _ = await http.PostAsync("http://llm.test/v1/chat/completions",
            new StringContent(json, Encoding.UTF8, "application/json"));
        return capture.LastBodyRaw!;
    }

    private static string Chat(string content) =>
        JsonSerializer.Serialize(new { model = "m", messages = new[] { new { role = "user", content } } });

    [Fact]
    public async Task Handler_BreaksLongRunsInMessageContent()
    {
        var body = await SendThrough(Chat("Summarise: " + Letters(258)));

        var content = JsonDocument.Parse(body).RootElement.GetProperty("messages")[0]
            .GetProperty("content").GetString()!;
        Assert.True(LongestLetterRun(content) <= LongLetterRuns.MaxRun);
        Assert.StartsWith("Summarise: ", content);
    }

    [Fact]
    public async Task Handler_BreaksLongRunsInTextParts_AndLeavesImagePartsAlone()
    {
        // Vision prompts send content as an array of parts. A data URL is one long run of base64 letters and
        // must arrive intact, or the image is corrupt.
        var image = "data:image/png;base64," + new string('A', 400);
        var json = JsonSerializer.Serialize(new
        {
            model = "m",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Letters(300) },
                        new { type = "image_url", image_url = new { url = image } },
                    },
                },
            },
        });

        var body = await SendThrough(json);

        var parts = JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.True(LongestLetterRun(parts[0].GetProperty("text").GetString()!) <= LongLetterRuns.MaxRun);
        Assert.Equal(image, parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Handler_LeavesABodyWithNothingToBreakByteForByte()
    {
        // Re-serialising would be harmless but not free, and would reorder nothing only by luck. A body with
        // no long run is sent exactly as the client built it.
        var json = """{"model":"m","temperature":0.20,"messages":[{"role":"user","content":"Hello there"}]}""";

        var body = await SendThrough(json);

        Assert.Equal(json, body);
    }

    [Fact]
    public async Task Handler_LeavesEmbeddingInputAlone()
    {
        // Only chat messages are rewritten. The embedding model has its own tokenizer, which has never refused
        // these inputs, and an embedding of altered text would not match what search later compares it with.
        var json = JsonSerializer.Serialize(new { model = "e", input = new[] { Letters(300) } });

        var body = await SendThrough(json);

        Assert.Equal(json, body);
    }

    [Fact]
    public async Task Handler_KeepsTheContentTypeAndPassesNonJsonThrough()
    {
        var capture = new CapturingHandler("""{"choices":[]}""");
        HttpRequestMessage? seen = null;
        var spy = new SpyHandler(r => seen = r) { InnerHandler = capture };
        var http = new HttpClient(new LongLetterRunHandler { InnerHandler = spy });

        using (await http.PostAsync("http://llm.test/v1/chat/completions",
                   new StringContent(Chat(Letters(300)), Encoding.UTF8, "application/json"))) { }
        Assert.Equal("application/json", seen!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", seen.Content.Headers.ContentType.CharSet);

        var text = Letters(300);
        using (await http.PostAsync("http://llm.test/v1/audio", new StringContent(text, Encoding.UTF8, "text/plain"))) { }
        Assert.Equal(text, capture.LastBodyRaw);
    }

    private sealed class SpyHandler(Action<HttpRequestMessage> onSend) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onSend(request);
            return base.SendAsync(request, ct);
        }
    }
}
