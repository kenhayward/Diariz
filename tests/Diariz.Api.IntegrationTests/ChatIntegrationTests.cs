using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class ChatIntegrationTests(ContainersFixture fx)
{
    private static ChatController BuildController(
        DiarizDbContext db, Guid userId, IChatStreamClient? chat = null, bool llmEnabled = true)
    {
        var settings = new FakeSummarizationSettingsResolver
        {
            Config = llmEnabled
                ? new SummarizationRequestConfig("https://llm.test/v1", "sk", "test-model", 60)
                : new SummarizationRequestConfig("", "", "test-model", 60),
        };
        return new ChatController(
            db, chat ?? new FakeChatStreamClient(), settings,
            new ChatContextResolver(db, Options.Create(new ChatOptions { ContextLength = 50000 })),
            new AttachmentExtractor())
        {
            ControllerContext = Http.Context(userId),
        };
    }

    private async Task<ApplicationUser> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Conversation_SavedAsJsonb_RoundTripsAcrossContexts()
    {
        var user = await SeedUser();
        var recId = Guid.NewGuid();

        Guid convId;
        await using (var db = fx.CreateDbContext())
        {
            var res = await BuildController(db, user.Id, llmEnabled: false).CreateConversation(
                new SaveChatConversationRequest(
                    [new ChatTurnDto("user", "What did we decide?"), new ChatTurnDto("assistant", "Ship Friday.")],
                    new SavedChatContextDto([recId], "spec.pdf", "blue widget")),
                default);
            convId = Assert.IsType<SaveChatConversationResult>(res.Value).Id;
        }

        // Read back on a fresh context — proves the jsonb columns persisted and deserialize.
        await using (var verify = fx.CreateDbContext())
        {
            var got = await BuildController(verify, user.Id).GetConversation(convId);
            var dto = Assert.IsType<ChatConversationDto>(got.Value);
            Assert.Equal(2, dto.Messages.Count);
            Assert.Equal("Ship Friday.", dto.Messages[1].Content);
            Assert.Equal([recId], dto.Context.RecordingIds);
            Assert.Equal("blue widget", dto.Context.AttachmentText);
        }
    }

    [Fact]
    public async Task DeletingUser_CascadesChatSessions()
    {
        var user = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await BuildController(db, user.Id, llmEnabled: false).CreateConversation(
                new SaveChatConversationRequest([new ChatTurnDto("user", "hi")], new SavedChatContextDto([], null, null)),
                default);

        await using (var db = fx.CreateDbContext())
        {
            var u = await db.Users.FindAsync(user.Id);
            db.Users.Remove(u!);
            await db.SaveChangesAsync();
        }

        await using var check = fx.CreateDbContext();
        Assert.False(await check.ChatSessions.AnyAsync(c => c.UserId == user.Id));
    }

    [Fact]
    public async Task Stream_OverRealTranscript_EmitsTokens()
    {
        var user = await SeedUser();
        var recId = Guid.NewGuid();

        await using (var seed = fx.CreateDbContext())
        {
            seed.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k", Title = "Standup" });
            // Two versions: the stream must use the highest (v2) via the filtered Include under Postgres.
            var v1 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "m", Version = 1 };
            var v2 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "m", Version = 2 };
            seed.Transcriptions.AddRange(v1, v2);
            seed.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = v2.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Hello team", Ordinal = 0,
            });
            seed.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice" });
            await seed.SaveChangesAsync();
        }

        // A streaming chat client backed by a canned SSE body from the "LLM".
        var llmSse = "data: {\"choices\":[{\"delta\":{\"content\":\"Alice\"}}]}\n\n" +
                     "data: {\"choices\":[{\"delta\":{\"content\":\" spoke\"}}]}\n\n" +
                     "data: [DONE]\n\n";
        var chat = new ChatStreamClient(new HttpClient(new FakeHttpMessageHandler(llmSse)));

        await using var db = fx.CreateDbContext();
        var controller = BuildController(db, user.Id, chat);
        var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;

        var result = await controller.Stream(
            new ChatStreamRequest([recId], null, null, [new ChatTurnDto("user", "Who spoke?")]), default);

        Assert.IsType<EmptyResult>(result);
        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        Assert.Contains("\"type\":\"meta\"", sse);
        Assert.Contains("\"type\":\"token\",\"value\":\"Alice\"", sse);
        Assert.Contains("\"type\":\"done\"", sse);
        Assert.Contains("\"contextTotal\":50000", sse);
    }
}
