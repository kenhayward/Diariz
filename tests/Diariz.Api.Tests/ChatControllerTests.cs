using Diariz.Api.Services.Llm;
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
    private static (ChatController controller, DiarizDbContext db, FakeChatStreamClient chat,
        FakeLlmSettingsResolver settings) Build(
        Guid userId, bool llmEnabled = true, FakeAudioStorage? storage = null, FakeUrlFetcher? urlFetcher = null,
        FakeChatToolSettingsResolver? toolSettings = null, FakeChatStreamClient? chat = null,
        bool imagesSupported = false)
    {
        var db = TestDb.Create();
        Users.Ensure(db, userId); // create paths mint the owner's personal room, which needs a real user row
        chat ??= new FakeChatStreamClient();
        var settings = new FakeLlmSettingsResolver
        {
            Config = llmEnabled
                ? new LlmRequestConfig("https://llm.test/v1", "sk-test", "test-model",
                    new LlmParameters { TimeoutSeconds = 60, ImagesSupported = imagesSupported })
                : new LlmRequestConfig("", "", "test-model", new LlmParameters { TimeoutSeconds = 60 }),
        };
        var ctxResolver = new ChatContextResolver(db, Options.Create(new ChatOptions { ContextLength = 40000 }), new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())));
        var orchestrator = new ChatToolOrchestrator(chat);
        var blobs = storage ?? new FakeAudioStorage();
        var controller = new ChatController(db, chat, settings, ctxResolver, new AttachmentExtractor(),
            blobs, urlFetcher ?? new FakeUrlFetcher(),
            toolSettings ?? new FakeChatToolSettingsResolver(), orchestrator, new RoomScope(db),
            null!, Options.Create(new DictationOptions()), new VisionImageEncoder(blobs),
            new AttachmentTextResolver(new AttachmentExtractor(), blobs, urlFetcher ?? new FakeUrlFetcher()))
        {
            ControllerContext = Http.Context(userId),
        };
        return (controller, db, chat, settings);
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
        var (controller, db, _, _) = Build(userId);

        var res = await controller.CreateConversation(Convo(("user", "What did we decide?"), ("assistant", "To ship Friday.")), default);

        var saved = Assert.IsType<SaveChatConversationResult>(res.Value);
        Assert.Equal("Project Kickoff Recap", saved.Title); // from the fake stream client
        var row = await db.ChatSessions.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Contains("What did we decide?", row.MessagesJson);
    }

    [Fact]
    public async Task Create_AttributesTitleGeneration_AsChatTitle()
    {
        // Title generation is its own call shape (short/cheap/automatic) - a distinct kind so it doesn't
        // get averaged into interactive chat latency.
        Guid? seenOperation = null;
        LlmCallKind? seenKind = null;
        var chat = new FakeChatStreamClient(onCall: () =>
        {
            seenOperation = LlmCallScope.Active?.OperationId;
            seenKind = LlmCallScope.Active?.Kind;
        });
        var (controller, _, _, _) = Build(Guid.NewGuid(), chat: chat);

        await controller.CreateConversation(
            Convo(("user", "What did we decide?"), ("assistant", "To ship Friday.")), default);

        Assert.NotNull(seenOperation);
        Assert.Equal(LlmCallKind.ChatTitle, seenKind);
    }

    [Fact]
    public async Task Create_Empty_ReturnsBadRequest()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());
        var res = await controller.CreateConversation(new SaveChatConversationRequest([], new SavedChatContextDto([], null, null)), default);
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Create_WhenLlmDisabled_TitleFallsBackToFirstUserMessage()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid(), llmEnabled: false);

        var res = await controller.CreateConversation(Convo(("user", "Summarise the standup")), default);

        Assert.Equal("Summarise the standup", Assert.IsType<SaveChatConversationResult>(res.Value).Title);
    }

    // ---- List / get / update / delete + ownership ----

    [Fact]
    public async Task List_ReturnsOnlyOwn_NewestFirst()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
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
        var (controller, db, _, _) = Build(me);
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
        var (controller, _, _, _) = Build(me);
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
        var (controller, db, _, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>((await controller.GetConversation(foreignId)).Result);
    }

    [Fact]
    public async Task Update_ChangesMessages_AndBumpsUpdatedAt()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
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
        var (controller, db, _, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>((await controller.UpdateConversation(foreignId, Convo(("user", "x")), default)).Result);
    }

    [Fact]
    public async Task Delete_Owned_Removes()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
        var save = await controller.CreateConversation(Convo(("user", "hi")), default);
        var id = Assert.IsType<SaveChatConversationResult>(save.Value).Id;

        var res = await controller.DeleteConversation(id);

        Assert.IsType<NoContentResult>(res);
        Assert.Empty(db.ChatSessions);
    }

    [Fact]
    public async Task Delete_OtherUsers_Returns404()
    {
        var (controller, db, _, _) = Build(Guid.NewGuid());
        var foreignId = Guid.NewGuid();
        db.ChatSessions.Add(new ChatSession { Id = foreignId, UserId = Guid.NewGuid(), Title = "theirs" });
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await controller.DeleteConversation(foreignId));
    }

    // ---- Streaming ----

    [Fact]
    public async Task Stream_WhenLlmDisabled_ReturnsBadRequest()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid(), llmEnabled: false);
        var res = await controller.Stream(new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hi")]), default);
        Assert.IsType<BadRequestObjectResult>(res);
    }

    [Fact]
    public async Task Stream_ForeignRecording_Returns404()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());
        var notMine = Guid.NewGuid();
        var res = await controller.Stream(new ChatStreamRequest([notMine], null, null, [new ChatTurnDto("user", "hi")]), default);
        Assert.IsType<NotFoundResult>(res);
    }

    [Fact]
    public async Task Stream_EmitsMetaTokensAndDone()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
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
        var (controller, db, chat, _) = Build(me);
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
        var (controller, db, chat, _) = Build(me); // seeds the user row
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
    public async Task Stream_AttributesEveryModelRoundTrip_ToOneChatOperation()
    {
        // ChatToolOrchestrator loops without pushing its own scope, so all of a turn's round-trips share
        // this operation - which is what makes MAX(Sequence) the turn count.
        var seenOperations = new List<Guid>();
        var seenKinds = new List<LlmCallKind>();
        var chat = new FakeChatStreamClient(onCall: () =>
        {
            seenOperations.Add(LlmCallScope.Active!.OperationId);
            seenKinds.Add(LlmCallScope.Active!.Kind);
        })
        {
            // Round 1: the model calls a tool. Round 2: it answers in text - forcing a second round-trip.
            ChunkRounds =
            [
                [new ChatStreamDelta(null, [new ToolCallFragment(0, "c1", "who_said_that", "{}")], "tool_calls")],
                [new ChatStreamDelta("Alice said it.", null, null)],
            ],
        };
        var (controller, _, _, _) = Build(Guid.NewGuid(), chat: chat);
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "Who said budget?")]), default);

        Assert.Equal(2, seenOperations.Count);              // the tool call forced a second round-trip
        Assert.Single(seenOperations.Distinct());            // both belong to ONE operation
        Assert.All(seenKinds, k => Assert.Equal(LlmCallKind.ChatMessage, k));
    }

    [Fact]
    public async Task Stream_WithUnownedSection_Returns404()
    {
        var (controller, db, _, _) = Build(Guid.NewGuid());
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
        var (controller, _, _, _) = Build(Guid.NewGuid());
        var file = TextFile("notes.txt", "text/plain", "The widget must be blue.");

        var res = await controller.Attachment(file, default);

        var dto = Assert.IsType<ChatAttachmentDto>(res.Value);
        Assert.Equal("notes.txt", dto.Name);
        Assert.Contains("widget must be blue", dto.Text);
    }

    [Fact]
    public async Task Attachment_Unsupported_ReturnsBadRequest()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());
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
        var (controller, db, chat, _) = Build(me, storage: storage, urlFetcher: fetcher);
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

    /// <summary>One unreadable attachment must never fail the whole chat turn. Characterised BEFORE the
    /// resolver extraction, so the refactor cannot quietly change it.</summary>
    [Fact]
    public async Task Stream_IncludeAttachments_SkipsAnAttachmentThatCannotBeRead()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, chat, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);

        storage.Objects["good"] = Encoding.UTF8.GetBytes("The widget must be blue.");
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "spec.txt", ContentType = "text/plain", BlobKey = "good", SizeBytes = 10, Ordinal = 0,
        });
        // Its blob is not in storage at all - FakeAudioStorage throws for an unknown key, exactly as a
        // deleted object would.
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.File,
            Name = "missing.txt", ContentType = "text/plain", BlobKey = "gone", SizeBytes = 10, Ordinal = 1,
        });
        await db.SaveChangesAsync();

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "What colour?")], IncludeAttachments: true),
            default);

        var system = chat.LastMessages![0].Content;
        Assert.Contains("The widget must be blue.", system);
        Assert.DoesNotContain("missing.txt", system);
    }

    [Fact]
    public async Task Stream_AllMeetings_AddsSearchLibraryInstruction_WhenToolsActive()
    {
        var me = Guid.NewGuid();
        var (controller, _, chat, _) = Build(me, toolSettings: WithTools());
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
        var (controller, _, chat, _) = Build(me, toolSettings: WithTools());
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
        var (controller, db, chat, _) = Build(me, storage: storage);
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

    // ---- The user's chosen chat model ----

    [Fact]
    public async Task Passes_the_requested_model_to_the_settings_resolver()
    {
        // Proves the request's ModelId actually reaches the resolver. Whether to HONOUR it is the
        // resolver's decision - a controller-side check would be a second copy of that rule, and the two
        // would drift.
        var (controller, _, _, settings) = Build(Guid.NewGuid());
        var chosen = Guid.NewGuid();

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hello")], false, false, null, chosen),
            default);

        Assert.Equal(chosen, settings.LastModelOverride);
    }

    [Fact]
    public async Task Sends_no_override_when_the_request_names_no_model()
    {
        var (controller, _, _, settings) = Build(Guid.NewGuid());

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();
        await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "hello")]), default);

        Assert.Null(settings.LastModelOverride);
    }

    // ---- Vision: screenshots attached to a turn ----

    private static async Task<(Guid RecordingId, Guid ShotId)> SeedScreenshot(
        DiarizDbContext db, FakeAudioStorage storage, Guid ownerId, int width = 800, int height = 600)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = "Demo", Status = RecordingStatus.Transcribed,
        };
        db.Recordings.Add(rec);
        var key = $"screenshots/{Guid.NewGuid()}.png";
        storage.Objects[key] = MakePng(width, height);
        var shot = new MeetingScreenshot
        {
            Id = Guid.NewGuid(), UserId = ownerId, RecordingId = rec.Id, CapturedAtMs = 1000,
            BlobKey = key, ThumbBlobKey = key + ".thumb", Width = width, Height = height,
            SizeBytes = storage.Objects[key].Length, Ordinal = 0,
        };
        db.MeetingScreenshots.Add(shot);
        await db.SaveChangesAsync();
        return (rec.Id, shot.Id);
    }

    private static byte[] MakePng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);
        canvas.Flush();
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Stream_ScreenshotsWithATextOnlyModel_ReturnsBadRequest()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage, imagesSupported: false);
        var (rid, sid) = await SeedScreenshot(db, storage, me);

        var res = await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "what is this?")],
                Screenshots: [new ChatScreenshotRefDto(rid, sid)]),
            default);

        var bad = Assert.IsType<BadRequestObjectResult>(res);
        Assert.Contains("test-model", bad.Value?.ToString());
        // Rejected BEFORE any blob was read. A version that loads first and refuses afterwards satisfies
        // every assertion about the response while doing the expensive thing anyway.
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task Stream_ScreenshotOnARecordingICannotRead_Returns404()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage, imagesSupported: true);
        var (theirRec, theirShot) = await SeedScreenshot(db, storage, Guid.NewGuid());

        var res = await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "what is this?")],
                Screenshots: [new ChatScreenshotRefDto(theirRec, theirShot)]),
            default);

        Assert.IsType<NotFoundResult>(res);
        Assert.Empty(storage.Reads);
    }

    /// <summary>Pairing a real shot id with a different recording must be refused outright, not silently
    /// skipped - skipping would let a caller probe which ids exist by watching what comes back.</summary>
    [Fact]
    public async Task Stream_ScreenshotIdNotOnThePairedRecording_Returns404()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage, imagesSupported: true);
        var (mineA, shotA) = await SeedScreenshot(db, storage, me);
        var (mineB, _) = await SeedScreenshot(db, storage, me);

        var res = await controller.Stream(
            new ChatStreamRequest([], null, null, [new ChatTurnDto("user", "what is this?")],
                Screenshots: [new ChatScreenshotRefDto(mineB, shotA)]),
            default);

        Assert.IsType<NotFoundResult>(res);
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task Stream_ScreenshotsWithAVisionModel_ReachTheModelOnTheLastUserMessage()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var chat = new FakeChatStreamClient { Tokens = ["ok"] };
        var (controller, db, _, _) = Build(me, storage: storage, chat: chat, imagesSupported: true);
        var (rid, sid) = await SeedScreenshot(db, storage, me);
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        var res = await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "what is this?")],
                Screenshots: [new ChatScreenshotRefDto(rid, sid)]),
            default);

        Assert.IsType<EmptyResult>(res);
        var sent = System.Text.Json.JsonSerializer.Serialize(chat.ChunkCallMessages[0]);
        Assert.Contains("\"image_url\"", sent);
        Assert.Contains("data:image/png;base64,", sent);
        // On the user turn, never the system prompt.
        Assert.DoesNotContain("\"role\":\"system\",\"content\":[", sent);
    }

    [Fact]
    public async Task Stream_NoScreenshots_SendsNoImagePartsAndReadsNoBlobs()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var chat = new FakeChatStreamClient { Tokens = ["ok"] };
        var (controller, db, _, _) = Build(me, storage: storage, chat: chat, imagesSupported: true);
        var rid = await SeedTranscribedRecording(db, me);
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "Who spoke?")]), default);

        Assert.DoesNotContain("image_url", System.Text.Json.JsonSerializer.Serialize(chat.ChunkCallMessages[0]));
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task Stream_ScreenshotsBillTheContextMeter()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage, imagesSupported: true);
        var (rid, sid) = await SeedScreenshot(db, storage, me, 800, 600);
        var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;

        await controller.Stream(
            new ChatStreamRequest([rid], null, null, [new ChatTurnDto("user", "hi")],
                Screenshots: [new ChatScreenshotRefDto(rid, sid)]),
            default);

        body.Position = 0;
        var text = new StreamReader(body).ReadToEnd();
        var meta = text.Split("\n\n").First(l => l.Contains("\"type\":\"meta\""));
        var used = int.Parse(System.Text.RegularExpressions.Regex.Match(meta, @"""contextUsed"":(\d+)").Groups[1].Value);
        // 800x600 is 640 tokens on its own - a turn this short could not reach that on text alone.
        Assert.True(used >= 640, $"expected the image to be billed, got contextUsed={used}");
    }

    // ---- One existing attachment, read into chat context (the drag-and-drop endpoint) ----

    /// <summary>Seeds a File attachment on a recording, with its blob in fake storage.</summary>
    private static async Task<Guid> SeedFileAttachment(
        DiarizDbContext db, FakeAudioStorage storage, Guid recordingId, string name, string contentType, string body)
    {
        var key = Guid.NewGuid().ToString("N");
        storage.Objects[key] = Encoding.UTF8.GetBytes(body);
        var a = new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, Kind = AttachmentKind.File,
            Name = name, ContentType = contentType, BlobKey = key, SizeBytes = body.Length, Ordinal = 0,
        };
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    [Fact]
    public async Task LibraryAttachment_ReturnsTheTextOfARecordingAttachment()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "plan.txt", "text/plain", "Quarterly plan text.");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.Equal("plan.txt", res.Value!.Name);
        Assert.Contains("Quarterly plan text.", res.Value.Text);
    }

    [Fact]
    public async Task LibraryAttachment_ReturnsAUrlAttachmentsFetchedText()
    {
        var me = Guid.NewGuid();
        var fetcher = new FakeUrlFetcher();
        var (controller, db, _, _) = Build(me, urlFetcher: fetcher);
        var rid = await SeedTranscribedRecording(db, me);
        var a = new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rid, Kind = AttachmentKind.Url,
            Name = "Roadmap", Url = "https://example.com/roadmap", Ordinal = 0,
        };
        db.Attachments.Add(a);
        await db.SaveChangesAsync();
        fetcher.Texts["https://example.com/roadmap"] = "Ship in Q3.";

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(a.Id, RecordingId: rid), default);

        Assert.Equal("Ship in Q3.", res.Value!.Text);
    }

    /// <summary>Ownership is the access rule, and a stranger must not be able to tell a recording that is not
    /// theirs from one that does not exist - both are 404.</summary>
    [Fact]
    public async Task LibraryAttachment_NotFound_WhenTheRecordingBelongsToSomeoneElse()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, Guid.NewGuid()); // someone else's recording
        var aid = await SeedFileAttachment(db, storage, rid, "plan.txt", "text/plain", "Secret.");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<NotFoundResult>(res.Result);
        Assert.Empty(storage.Reads); // rejected before any blob was read
    }

    [Fact]
    public async Task LibraryAttachment_NotFound_WhenTheAttachmentDoesNotExist()
    {
        var me = Guid.NewGuid();
        var (controller, db, _, _) = Build(me);
        var rid = await SeedTranscribedRecording(db, me);

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(Guid.NewGuid(), RecordingId: rid), default);

        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenNeitherRecordingNorSectionIsGiven()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(Guid.NewGuid()), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenBothRecordingAndSectionAreGiven()
    {
        var (controller, _, _, _) = Build(Guid.NewGuid());

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(Guid.NewGuid(), RecordingId: Guid.NewGuid(), SectionId: Guid.NewGuid()),
            default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    /// <summary>The bulk path skips these silently, which is right when a whole turn is at stake. Here the
    /// user dropped this one document and is waiting on it, so it says why.</summary>
    [Fact]
    public async Task LibraryAttachment_BadRequest_ForAnUnsupportedFileType()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "bundle.zip", "application/zip", "PK...");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task LibraryAttachment_BadRequest_WhenNothingCouldBeExtracted()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var rid = await SeedTranscribedRecording(db, me);
        var aid = await SeedFileAttachment(db, storage, rid, "blank.txt", "text/plain", "   ");

        var res = await controller.LibraryAttachment(new ChatLibraryAttachmentRequest(aid, RecordingId: rid), default);

        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    /// <summary>A folder attachment is gated on being able to VIEW the folder, mirroring
    /// SectionAttachmentsController rather than inventing a second rule.</summary>
    [Fact]
    public async Task LibraryAttachment_ResolvesAFolderAttachmentForAViewerOfThatFolder()
    {
        var me = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(me);
        var section = new Section { Id = Guid.NewGuid(), UserId = me, RoomId = roomId, Name = "Folder" };
        db.Sections.Add(section);
        storage.Objects["fk"] = Encoding.UTF8.GetBytes("Folder brief text.");
        var a = new SectionAttachment
        {
            Id = Guid.NewGuid(), SectionId = section.Id, UploadedByUserId = me, Kind = AttachmentKind.File,
            Name = "brief.txt", ContentType = "text/plain", BlobKey = "fk", SizeBytes = 18, Ordinal = 0,
        };
        db.SectionAttachments.Add(a);
        await db.SaveChangesAsync();

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(a.Id, SectionId: section.Id), default);

        Assert.Contains("Folder brief text.", res.Value!.Text);
    }

    [Fact]
    public async Task LibraryAttachment_NotFound_ForAFolderTheCallerCannotView()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var (controller, db, _, _) = Build(me, storage: storage);
        Users.Ensure(db, other);
        var otherRoom = await new RoomScope(db).PersonalRoomIdAsync(other);
        var section = new Section { Id = Guid.NewGuid(), UserId = other, RoomId = otherRoom, Name = "Theirs" };
        db.Sections.Add(section);
        storage.Objects["fk"] = Encoding.UTF8.GetBytes("Private brief.");
        var a = new SectionAttachment
        {
            Id = Guid.NewGuid(), SectionId = section.Id, UploadedByUserId = other, Kind = AttachmentKind.File,
            Name = "brief.txt", ContentType = "text/plain", BlobKey = "fk", SizeBytes = 14, Ordinal = 0,
        };
        db.SectionAttachments.Add(a);
        await db.SaveChangesAsync();

        var res = await controller.LibraryAttachment(
            new ChatLibraryAttachmentRequest(a.Id, SectionId: section.Id), default);

        Assert.IsType<NotFoundResult>(res.Result);
        Assert.Empty(storage.Reads);
    }
}
