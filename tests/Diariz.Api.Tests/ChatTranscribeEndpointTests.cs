using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatTranscribeEndpointTests
{
    private sealed class FakeDictationClient : IDictationClient
    {
        private readonly string _text;
        public FakeDictationClient(string text) => _text = text;
        public Task<string> TranscribeAsync(
            DictationRequestConfig config, Stream audio, string contentType, string fileName,
            CancellationToken ct = default) => Task.FromResult(_text);
    }

    private static ChatController BuildController(IDictationClient dictation, DictationOptions opts, Guid userId)
    {
        var controller = new ChatController(
            db: null!, chat: null!, settings: null!, contextResolver: null!, extractor: null!,
            storage: null!, urlFetcher: null!, toolSettings: null!, orchestrator: null!, rooms: null!,
            dictation: dictation, dictationOptions: Options.Create(opts));
        controller.ControllerContext = Http.Context(userId);
        return controller;
    }

    private static IFormFile WebmFile()
    {
        var bytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "utterance.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };
    }

    [Fact]
    public async Task Transcribe_ReturnsText_WhenConfigured()
    {
        var controller = BuildController(
            new FakeDictationClient("Hello world."),
            new DictationOptions { ApiBase = "http://stt.test/v1", Model = "whisper-1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        var dto = Assert.IsType<ChatTranscriptionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Hello world.", dto.Text);
    }

    [Fact]
    public async Task Transcribe_Returns400_WhenServerPathNotConfigured()
    {
        var controller = BuildController(
            new FakeDictationClient("ignored"),
            new DictationOptions { ApiBase = "" },
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Transcribe_Returns400_WhenNoFile()
    {
        var controller = BuildController(
            new FakeDictationClient("ignored"),
            new DictationOptions { ApiBase = "http://stt.test/v1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
