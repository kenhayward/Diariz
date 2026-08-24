using System.Net.Http;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatTranscribeEndpointTests
{
    private sealed class FakeDictationClient : IDictationClient
    {
        private readonly string _text;
        private readonly Action? _onCall;
        public FakeDictationClient(string text, Action? onCall = null)
        {
            _text = text;
            _onCall = onCall;
        }

        public DictationRequestConfig? LastConfig { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastFileName { get; private set; }

        public Task<string> TranscribeAsync(
            DictationRequestConfig config, Stream audio, string contentType, string fileName,
            CancellationToken ct = default)
        {
            _onCall?.Invoke();
            LastConfig = config;
            LastContentType = contentType;
            LastFileName = fileName;
            return Task.FromResult(_text);
        }
    }

    private sealed class ThrowingDictationClient : IDictationClient
    {
        public Task<string> TranscribeAsync(
            DictationRequestConfig config, Stream audio, string contentType, string fileName,
            CancellationToken ct = default) => throw new HttpRequestException("stt down");
    }

    private static ChatController BuildController(IDictationClient dictation, DictationOptions opts, Guid userId)
    {
        // Real (in-memory) db + seeded user row: the dictation action reads the caller's email to attribute
        // the LlmCallScope it pushes.
        var db = TestDb.Create();
        Users.Ensure(db, userId);
        var controller = new ChatController(
            db: db, chat: null!, settings: null!, contextResolver: null!, extractor: null!,
            storage: null!, urlFetcher: null!, toolSettings: null!, orchestrator: null!, rooms: null!,
            dictation: dictation, dictationOptions: Options.Create(opts), vision: null!,
            attachmentText: null!);
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
        var fake = new FakeDictationClient("Hello world.");
        var controller = BuildController(
            fake,
            new DictationOptions { ApiBase = "http://stt.test/v1", Model = "whisper-1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        var dto = Assert.IsType<ChatTranscriptionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Hello world.", dto.Text);

        // The action must assemble the request config from the server options and pass the upload metadata through.
        Assert.Equal("http://stt.test/v1", fake.LastConfig!.ApiBase);
        Assert.Equal("whisper-1", fake.LastConfig.Model);
        Assert.Equal("audio/webm", fake.LastContentType);
    }

    [Fact]
    public async Task Transcribe_AttributesCall_AsDictation()
    {
        Guid? seenOperation = null;
        LlmCallKind? seenKind = null;
        var fake = new FakeDictationClient("Hello world.", onCall: () =>
        {
            seenOperation = LlmCallScope.Active?.OperationId;
            seenKind = LlmCallScope.Active?.Kind;
        });
        var controller = BuildController(
            fake, new DictationOptions { ApiBase = "http://stt.test/v1", Model = "whisper-1" }, Guid.NewGuid());

        await controller.Transcribe(WebmFile(), CancellationToken.None);

        Assert.NotNull(seenOperation);
        Assert.Equal(LlmCallKind.Dictation, seenKind);
    }

    [Fact]
    public async Task Transcribe_Returns502_WhenTranscriptionFails()
    {
        var controller = BuildController(
            new ThrowingDictationClient(),
            new DictationOptions { ApiBase = "http://stt.test/v1" },
            Guid.NewGuid());

        var result = await controller.Transcribe(WebmFile(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
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
