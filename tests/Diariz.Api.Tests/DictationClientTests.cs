using System.Net;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class DictationClientTests
{
    private static Stream Wav() => new MemoryStream(Encoding.ASCII.GetBytes("RIFFfake-wav-bytes"));

    private static string TranscriptionResponse(string text) =>
        JsonSerializer.Serialize(new { text });

    [Fact]
    public async Task TranscribeAsync_PostsMultipartToAudioTranscriptions_WithBearerAndModel_AndParsesText()
    {
        var handler = new FakeHttpMessageHandler(TranscriptionResponse("Hello world."));
        var client = new DictationClient(new HttpClient(handler));
        var config = new DictationRequestConfig("http://stt.test/v1", "sk-secret", "whisper-1", 30);

        var text = await client.TranscribeAsync(config, Wav(), "audio/webm", "utterance.webm");

        Assert.Equal("Hello world.", text);
        Assert.Equal("http://stt.test/v1/audio/transcriptions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("whisper-1", handler.LastRequestBody);
        Assert.Contains("utterance.webm", handler.LastRequestBody);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsAuthorization_WhenNoKeyConfigured()
    {
        var handler = new FakeHttpMessageHandler(TranscriptionResponse("x"));
        var client = new DictationClient(new HttpClient(handler));
        var config = new DictationRequestConfig("http://stt.test/v1", "", "whisper-1", 30);

        await client.TranscribeAsync(config, Wav(), "audio/webm", "utterance.webm");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }
}
