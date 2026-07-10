using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatControllerTests
{
    private static (ChatController controller, DiarizDbContext db, FakeChatStreamClient chat) Build(
        Guid userId, bool llmEnabled = true, FakeAudioStorage? storage = null, FakeUrlFetcher? urlFetcher = null,
        FakeChatToolSettingsResolver? toolSettings = null)
    {
        var db = TestDb.Create();
        Users.Ensure(db, userId); // create paths mint the owner's personal room, which needs a real user row
        var chat = new FakeChatStreamClient();
        var settings = new FakeSummarizationSettingsResolver
        {
            Config = llmEnabled
                ? new SummarizationRequestConfig("https://llm.test/v1", "sk-test", "test-model", 60)
                : new SummarizationRequestConfig("", "", "test-model", 60),
        };
        var ctxResolver = new ChatContextResolver(db, Options.Create(new ChatOptions { ContextLength = 40000 }));
        var orchestrator = new ChatToolOrchestrator(chat);
        var controller = new ChatController(db, chat, settings, ctxResolver, new AttachmentExtractor(),
            storage ?? new FakeAudioStorage(), urlFetcher ?? new FakeUrlFetcher(),
            toolSettings ?? new FakeChatToolSettingsResolver(), orchestrator, new RoomScope(db))
        {
            ControllerContext = Http.Context(userId),
        };
        return (controller, db, chat);
    }

    /// <summary>A tool resolver with one active tool, so the tool-usage instructions are appended.</summary>
    private static FakeChatToolSettingsResolver WithTools() =>
        new()
        {
            MasterEnabled = true,
            ActiveTools = [new Diariz.Api.Tools.WhoSaidThatTool(new FakeTranscriptSearch())],
        };

    private static async Task<Guid> SeedTranscribedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Standup", Status = RecordingStatus.Transcribed };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hello team", Ordinal = 0,
        });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static SaveChatConversationRequest Convo(params (string role, string content)[] turns) =>
        new(turns.Select(t => new ChatTurnDto(t.role, t.content)).ToList(), new SavedChatContextDto([], null, null));

    // ---- Save / title ----

    [Fact]
    public async Task Create_PersistsConversation_WithLlmTitle()
    {
        var userId = Guid.NewGuid();
        var (controller, db, _) = Build(userId);

        var res = await controller.CreateConversation(Convo(("user", "What did we decide?"), ("assistant", "To ship Friday.")), default);

        var saved = Assert.IsType<SaveChatConversationResult>(res.Value);
        Assert.Equal("Project Kickoff Recap", saved.Title); // from the fake stream client
        var row = await db.ChatSessions.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Contains("What did we decide?", row.MessagesJson);
    }

    [Fact]
    public async Task Create_Empty_ReturnsBadRequest()
    {
        var (controller, _, _) = Build(Guid.NewGuid());
        var res = await controller.CreateConversation(new SaveChatConversationRequest([], new SavedChatContextDto([], null, null)), default);
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Create_WhenLlmDisabled_TitleFallsBackToFirstUserMessage()
    {
        var (controller, _, _) = Build(Guid.NewGuid(), llmEnabled: false);

        var res = await controller.CreateConversation(Convo(("user", "Summarise the standup")), default);

        Assert.Equal("Summarise the standup", Assert.IsType<SaveChatConversationResult>(res.Value).Title);
    }

    // ---- List / get / update / delete + ownership ----

    [Fact]
    public async Task List_ReturnsOnlyOwn_NewestFirst()
    {
        var me = Guid.NewGuid();
        var (controller, db, _) = Build(me);
        var meRoom = await new RoomScope(db).PersonalRoomIdAsync(me); // chats are room-scoped now
        db.ChatSessions.Add(new ChatSession { Id = Guid.NewGuid(), UserId = me, RoomId = meRoom, Title = "Older", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        db.ChatSessions.Add(new ChatSession { Id = Guid.NewGuid(), UserId = me, RoomId = meRoom, Title = "Newer", UpdatedAt = DateTimeOffset.UtcNow });
        db.ChatSessions.Add(new ChatSession { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Title = "Someone else" });
        await db.SaveChangesAsync();

        var list = await controller.ListConversations();

        Assert.Equal(["Newer", "Older"], list.Select(c => c.Title));
    }

    [Fact]
    public async Task Get_Owned_RoundTripsMessagesAndContext()
    {
        var me = Guid.NewGuid();
        var (controller, db, _) = Build(me);
        var rid = Guid.NewGuid();
        var save = await controller.CreateConversation(
            new SaveChatConversationRequest(
                [new ChatTurnDto("user", "hi"), new ChatTurnDto("assistant", "hello")],
                new SavedChatContextDto([rid], "spec.pdf", "doc text")),
            default);
        var id = Assert.IsType<SaveChatConversationResult>(save.Value).Id;

        var got = await controller.GetConversation(id);
        var dto = Assert.IsType<ChatConversationDto>(got.Value);

        Assert.Equal(2, dto.Messages.Count);
        Assert.Equal("hello", dto.Messages[1].Content);
        Assert.Equal([rid], dto.Context.RecordingIds);
        Assert.Equal("spec.pdf", dto.Context.AttachmentName);
    }

    [Fact]
    public async Task Get_RoundTripsAFolderChatSectionId()
    {
        var me = Guid.NewGuid();
        var (controller, _, _) = Build(me);
        var sectionId = Guid.NewGuid();
        var save = await controller.CreateConversation(
            new SaveChatConversationRequest(
                [new ChatTurnDto("user", "summarise the folder")],
                new SavedChatContextDto([], null, null, SectionId: sectionId)),
            default);
        var id = Assert.IsType<SaveChatConversationResult>(save.Value).Id;

        var dto = Assert.IsType<ChatConversationDto>((await controller.GetConversation(id)).Value);
        Assert.Equal(sectionId, dto.Context.SectionId); // folder chat restores its folder
    }

    [Fact]
    public async Task Get_OtherUsers_Returns404()
    {
        var (controller, db, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>((await controller.GetConversation(foreignId)).Result);
    }

    [Fact]
    public async Task Update_ChangesMessages_AndBumpsUpdatedAt()
    {
        var me = Guid.NewGuid();
        var (controller, db, _) = Build(me);
        var save = await controller.CreateConversation(Convo(("user", "first")), default);
        var id = Assert.IsType<SaveChatConversationResult>(save.Value).Id;
        var before = (await db.ChatSessions.AsNoTracking().SingleAsync()).UpdatedAt;

        var res = await controller.UpdateConversation(id,
            new SaveChatConversationRequest(
                [new ChatTurnDto("user", "first"), new ChatTurnDto("assistant", "answer"), new ChatTurnDto("user", "second")],
                new SavedChatContextDto([], null, null)),
            default);

        Assert.IsType<SaveChatConversationResult>(res.Value);
        var row = await db.ChatSessions.AsNoTracking().SingleAsync();
        Assert.Contains("second", row.MessagesJson);
        Assert.True(row.UpdatedAt >= before);
    }

    [Fact]
    public async Task Update_OtherUsers_Returns404()
    {
        var (controller, db, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>((await controller.UpdateConversation(foreignId, Convo(("user", "x")), default)).Result);
    }

    [Fact]
    public async Task Delete_Owned_Removes()
    {
        var me = Guid.NewGuid();
        var (controller, db, _) = Build(me);
        var save = await controller.CreateConversation(Convo(("user", "hi")), default);
        var id = Assert.IsType<SaveChatConversationResult>(save.Value).Id;

        var res = await controller.DeleteConversation(id);

        Assert.IsType<NoContentResult>(res);
        Assert.Empty(db.ChatSessions);
    }

    [Fact]
    public async Task Delete_OtherUsers_Returns404()
    {
        var (controller, db, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await controller.DeleteConversation(foreignId));
    }

    // ---- Streaming ----

    [Fact]
    public async Task Stream_WhenLlmDisabled_ReturnsBadRequest()
    {
        var (controller, _, _) = Build(Guid.NewGuid(), llmEnabled: false);
        var res = await controller.Stream(new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hi")]), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Stream_ForeignRecording_Returns404()
    {
        var (controller, _, _) = Build(Guid.NewGuid());
        var notMine = Guid.NewGuid();
        var res = await controller.Stream(new ChatStreamRequest([notMine], null, null, [new ChatTurnDto("user", "hi")]), default);
        Assert.IsType<NotFoundResult>(res);
    }

    [Fact]
    public async Task Stream_EmitsMetaTokensAndDone()
    {
        var me = Guid.NewGuid();
        var (controller, db, _) = Build(me);
        var rid = await SeedTranscribedRecording(db, me);

        var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;

        var res = await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "Who spoke?")]), default);

        Assert.IsType<EmptyResult>(res);
        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();

        Assert.Contains("\"type\":\"meta\"", sse);
        Assert.Contains("\"contextTotal\":40000", sse);
        Assert.Contains("\"model\":\"test-model\"", sse);
        Assert.Contains("\"type\":\"token\",\"value\":\"Project\"", sse);
        Assert.Contains("\"type\":\"done\"", sse);
        Assert.Equal("text/event-stream", controller.ControllerContext.HttpContext.Response.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Stream_IncludesExtractedActions_InTheSystemContext()
    {
        var me = Guid.NewGuid();
        var (controller, db, chat) = Build(me);
        var rid = await SeedTranscribedRecording(db, me);
        db.RecordingActions.Add(new RecordingAction
        {
            Id = Guid.NewGuid(), RecordingId = rid, Text = "Ship the widget", Actor = "Alice", Deadline = "Friday", Ordinal = 0,
        });
        await db.SaveChangesAsync();

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "What are the actions?")]), default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("Actions:", system);
        Assert.Contains("Ship the widget (Actor: Alice; Deadline: Friday)", system);
    }

    [Fact]
    public async Task Stream_WithFolder_LoadsSummaryMinutesAndActionsIntoContext()
    {
        var me = Guid.NewGuid();
        var (controller, db, chat) = Build(me); // seeds the user row
        var meRoom = await new RoomScope(db).PersonalRoomIdAsync(me); // folders are room-scoped now
        var section = new Section { Id = Guid.NewGuid(), UserId = me, RoomId = meRoom, Name = "Q3 Planning" };
        db.Sections.Add(section);
        db.SectionSummaries.Add(new SectionSummary { Id = Guid.NewGuid(), SectionId = section.Id, Text = "Overall Q3 theme." });
        db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = section.Id, Text = "# Minutes\nHire two engineers." });
        var rec = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Kickoff" };
        db.Recordings.Add(rec);
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Draft the roadmap", Ordinal = 0 });
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, me, section.Id); // filed under the folder

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "summarise the folder")], SectionId: section.Id), default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("Overall Q3 theme.", system);      // folder summary
        Assert.Contains("Hire two engineers.", system);    // folder minutes
        Assert.Contains("Draft the roadmap", system);      // aggregated folder actions
    }

    [Fact]
    public async Task Stream_WithUnownedSection_Returns404()
    {
        var (controller, db, _) = Build(Guid.NewGuid());
        var theirs = new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(theirs);
        await db.SaveChangesAsync();

        var res = await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hi")], SectionId: theirs.Id), default);
        Assert.IsType<NotFoundResult>(res);
    }

    // ---- Attachment ----

    [Fact]
    public async Task Attachment_Text_ReturnsExtractedText()
    {
        var (controller, _, _) = Build(Guid.NewGuid());
        var file = TextFile("notes.txt", "text/plain", "The widget must be blue.");

        var res = await controller.Attachment(file, default);

        var dto = Assert.IsType<ChatAttachmentDto>(res.Value);
        Assert.Equal("notes.txt", dto.Name);
        Assert.Contains("widget must be blue", dto.Text);
    }

    [Fact]
    public async Task Attachment_Unsupported_ReturnsBadRequest()
    {
        var (controller, _, _) = Build(Guid.NewGuid());
        var file = TextFile("photo.png", "image/png", "not really an image");

        Assert.IsType<BadRequestObjectResult>((await controller.Attachment(file, default)).Result);
    }

    private static IFormFile TextFile(string name, string contentType, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    // ---- Attachments as chat context ----

    [Fact]
    public async Task Stream_IncludeAttachments_AddsFileAndUrlTextToTheSystemPrompt()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var fetcher = new FakeUrlFetcher();
        var (controller, db, chat) = Build(me, storage: storage, urlFetcher: fetcher);
        var rid = await SeedTranscribedRecording(db, me);

        storage.Objects["k1"] = Encoding.UTF8.GetBytes("The widget must be blue.");
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "spec.txt", ContentType = "text/plain", BlobKey = "k1", SizeBytes = 10, Ordinal = 0,
        });
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.Url,
            Name = "Roadmap", Url = "https://example.com/roadmap", Ordinal = 1,
        });
        await db.SaveChangesAsync();
        fetcher.Texts["https://example.com/roadmap"] = "Ship in Q3.";

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "What colour?")], IncludeAttachments: true),
            default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("The widget must be blue.", system);
        Assert.Contains("Ship in Q3.", system);
        Assert.Contains("Roadmap", system);
        Assert.Contains("spec.txt", system);
    }

    [Fact]
    public async Task Stream_AllMeetings_AddsSearchLibraryInstruction_WhenToolsActive()
    {
        var me = Guid.NewGuid();
        var (controller, _, chat) = Build(me, toolSettings: WithTools());
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "What did we decide about pricing?")],
                SearchAllMeetings: true),
            default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("ENTIRE library", system);      // the all-meetings instruction
    }

    [Fact]
    public async Task Stream_NotAllMeetings_OmitsSearchLibraryInstruction()
    {
        var me = Guid.NewGuid();
        var (controller, _, chat) = Build(me, toolSettings: WithTools());
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "Hi")]), default);

        var system = chat.LastMessages![0].Content;
        Assert.DoesNotContain("ENTIRE library", system); // tool instruction present, but not the all-meetings one
    }

    [Fact]
    public async Task Stream_WithoutIncludeAttachments_OmitsAttachmentText()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, chat) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        storage.Objects["k1"] = Encoding.UTF8.GetBytes("The widget must be blue.");
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "spec.txt", ContentType = "text/plain", BlobKey = "k1", SizeBytes = 10, Ordinal = 0,
        });
        await db.SaveChangesAsync();

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "What colour?")]), default);

        Assert.DoesNotContain("The widget must be blue.", chat.LastMessages![0].Content);
    }
}
