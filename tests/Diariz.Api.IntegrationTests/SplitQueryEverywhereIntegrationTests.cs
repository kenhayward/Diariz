using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Mcp;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>The same invariant `SplitQueryIntegrationTests` pins for `RecordingsController`, for every OTHER
/// place that loads a recording's segments alongside a sibling collection.
///
/// This file exists because the first fix (0.228.2) was scoped to `RecordingsController.cs` and missed five
/// more files. The miss was only found by running real traffic through the deployed API and reading
/// `pg_stat_statements`: `get_recording_details` was returning **517,825 rows** and spilling **1.97 GB** to
/// disk in a single call, because it multiplies Speakers x Actions x Segments across EVERY transcription
/// version rather than just the current one.
///
/// A grep over `RecordingsController` could never have found these. Two of them (`FormulaRunProcessor`) do
/// not even read as an Include chain - the query is assembled across several statements behind `if` guards,
/// so a source-level scan would miss them too. Only executing each path and reading the SQL finds them all.</summary>
[Collection(IntegrationCollection.Name)]
public class SplitQueryEverywhereIntegrationTests(ContainersFixture fx)
{
    private static void AssertNoCartesianJoin(IReadOnlyList<string> statements, string path)
    {
        var offenders = statements
            .Where(s => (s.Contains("JOIN \"Segments\"") || s.Contains("FROM \"Segments\""))
                        && (s.Contains("JOIN \"Speakers\"") || s.Contains("JOIN \"RecordingActions\"")))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{path} emitted {offenders.Count} statement(s) joining Segments to a sibling collection, " +
            $"which returns their cartesian product:\n\n{string.Join("\n\n", offenders)}");
    }

    /// <summary>Also asserts the query does not drag in superseded transcription versions. Splitting alone
    /// removes the product but still loads every version's segments, which keeps growing with re-transcribes -
    /// and all four MCP/resource paths pick the newest version in memory and discard the rest.</summary>
    private static void AssertOnlyCurrentVersionSegments(IReadOnlyList<string> statements, string path)
    {
        var segmentReads = statements
            .Where(s => s.Contains("FROM \"Segments\"") || s.Contains("JOIN \"Segments\""))
            .Where(s => s.TrimStart().StartsWith("SELECT"))
            .ToList();

        Assert.True(segmentReads.Count > 0, $"{path} read no segments at all - the test proves nothing.");
        Assert.True(
            segmentReads.All(s => s.Contains("LIMIT") || s.Contains("ROW_NUMBER")),
            $"{path} reads segments across every transcription version, not just the current one:\n\n" +
            string.Join("\n\n", segmentReads));
    }

    /// <summary>Three segments, two speakers, two actions - and a SECOND, superseded transcription version, so
    /// an all-versions read is a different shape from a current-version one.</summary>
    private async Task<(Guid UserId, Guid RecordingId)> SeedAsync()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test",
        };
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}", Title = "Workshop",
            Status = RecordingStatus.Transcribed, DurationMs = 60_000,
        };
        var old = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        var cur = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 2 };
        db.AddRange(user, rec, old, cur,
            new Segment { Id = Guid.NewGuid(), TranscriptionId = old.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "superseded", StartMs = 0, EndMs = 10 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = cur.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "one", StartMs = 0, EndMs = 10 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = cur.Id, Ordinal = 1, SpeakerLabel = "SPEAKER_01", Original = "two", StartMs = 10, EndMs = 20 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = cur.Id, Ordinal = 2, SpeakerLabel = "SPEAKER_00", Original = "three", StartMs = 20, EndMs = 30 },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada" },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Grace" },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "ship it", Ordinal = 0 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "write it up", Ordinal = 1 });
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    private (DiarizDbContext Db, RecordsSql Sql) Recording()
    {
        var sql = new RecordsSql();
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(sql)
            .Options;
        return (new DiarizDbContext(options), sql);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------- MCP tools + resources

    [Fact]
    public async Task GetRecordingDetailsTool_DoesNotJoinOrReadEveryVersion()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        string result;
        await using (db)
            result = await new GetRecordingDetailsTool(db)
                .ExecuteAsync(Args("{}"), new ChatToolContext(userId, [recId]), default);

        Assert.Contains("Segments: 3", result);          // the CURRENT version's three, not all four
        Assert.Contains("Action items: 2", result);
        AssertNoCartesianJoin(sql.Statements, "MCP get_recording_details");
        AssertOnlyCurrentVersionSegments(sql.Statements, "MCP get_recording_details");
    }

    [Fact]
    public async Task GetTranscriptTool_DoesNotJoinOrReadEveryVersion()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        string result;
        await using (db)
            result = await new GetTranscriptTool(db)
                .ExecuteAsync(Args("{}"), new ChatToolContext(userId, [recId]), default);

        Assert.Contains("one", result);
        Assert.DoesNotContain("superseded", result);
        AssertNoCartesianJoin(sql.Statements, "MCP get_transcript");
        AssertOnlyCurrentVersionSegments(sql.Statements, "MCP get_transcript");
    }

    [Fact]
    public async Task GetSegmentContextTool_DoesNotJoinOrReadEveryVersion()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db)
            await new GetSegmentContextTool(db)
                .ExecuteAsync(Args("{\"time\":\"00:00\"}"), new ChatToolContext(userId, [recId]), default);

        AssertNoCartesianJoin(sql.Statements, "MCP get_segment_context");
        AssertOnlyCurrentVersionSegments(sql.Statements, "MCP get_segment_context");
    }

    [Fact]
    public async Task McpResourceService_DoesNotJoinOrReadEveryVersion()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        McpResourceContent? content;
        await using (db)
            content = await new McpResourceService(db)
                .ReadAsync(userId, McpResources.TranscriptUri(recId), default);

        Assert.NotNull(content);
        Assert.DoesNotContain("superseded", content!.Text);
        AssertNoCartesianJoin(sql.Statements, "MCP transcript resource");
        AssertOnlyCurrentVersionSegments(sql.Statements, "MCP transcript resource");
    }

    // ---------------------------------------------------------------- controllers

    [Fact]
    public async Task ChatStream_DoesNotJoinSegmentsToSiblings()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db)
        {
            var settings = new FakeLlmSettingsResolver
            {
                Config = new LlmRequestConfig("https://llm.test/v1", "sk", "m", new LlmParameters { TimeoutSeconds = 60 }),
            };
            var controller = new ChatController(
                db, new FakeChatStreamClient(), settings,
                new ChatContextResolver(db, Options.Create(new ChatOptions { ContextLength = 50000 }), new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions()))),
                new AttachmentExtractor(), new FakeAudioStorage(), new FakeUrlFetcher(),
                new FakeChatToolSettingsResolver(), new ChatToolOrchestrator(new FakeChatStreamClient()),
                new RoomScope(db), null!, Options.Create(new DictationOptions()),
                new VisionImageEncoder(new FakeAudioStorage()))
            {
                ControllerContext = Http.Context(userId),
            };
            await controller.Stream(
                new ChatStreamRequest([recId], null, null, [new ChatTurnDto("user", "hello")]), default);
        }

        AssertNoCartesianJoin(sql.Statements, "POST /api/chat/stream");
    }

    [Fact]
    public async Task ExtractActions_DoesNotJoinSegmentsToSiblings()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db)
        {
            var controller = new RecordingActionsController(
                db, new FakeActionsClient(), new FakeLlmSettingsResolver(),
                new FilePromptTemplateProvider("nonexistent"))
            {
                ControllerContext = Http.Context(userId),
            };
            await controller.Extract(recId);
        }

        AssertNoCartesianJoin(sql.Statements, "POST /api/recordings/{id}/actions/extract");
    }

    [Fact]
    public async Task TranslateRecording_DoesNotJoinSegmentsToSiblings()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db)
        {
            var controller = new RecordingTranslationController(
                db, new FakeTranslationClient(), new FakeLlmSettingsResolver())
            {
                ControllerContext = Http.Context(userId),
            };
            await controller.TranslateRecording(recId, new TranslateRequest("fr"));
        }

        AssertNoCartesianJoin(sql.Statements, "POST /api/recordings/{id}/translate");
    }

    // ---------------------------------------------------------------- formula runs

    /// <summary>Covers BOTH FormulaRunProcessor query sites in one run: the field resolver (Speakers +
    /// Actions + Segments) and the context builder (Speakers + Segments). Neither reads as a single Include
    /// chain in the source - both are assembled across statements behind `if` guards on what the template
    /// asks for - so the template here deliberately requests the transcript AND a substituted field.</summary>
    [Fact]
    public async Task FormulaRun_DoesNotJoinSegmentsToSiblings()
    {
        var (userId, recId) = await SeedAsync();
        Guid formulaId, resultId;
        await using (var seed = fx.CreateDbContext())
        {
            var formula = new Formula
            {
                Id = Guid.NewGuid(),
                Scope = FormulaScope.Personal,
                OwnerUserId = userId,
                Name = "Notes",
                Context = FormulaContext.Transcript,
                ContentJson = """
                    [{"kind":"heading","level":1,"text":"{{attendees}}"},
                     {"kind":"prompt","text":"Summarise the transcript."}]
                    """,
            };
            var result = new FormulaResult
            {
                Id = Guid.NewGuid(), RecordingId = recId, FormulaId = formula.Id,
                Status = FormulaRunStatus.Generating, Name = "Notes",
            };
            seed.AddRange(formula, result);
            await seed.SaveChangesAsync();
            (formulaId, resultId) = (formula.Id, result.Id);
        }

        var (db, sql) = Recording();
        await using (db)
        {
            var settings = new FakeLlmSettingsResolver
            {
                Config = new LlmRequestConfig("https://llm.test/v1", "sk", "m", new LlmParameters { TimeoutSeconds = 60 }),
            };
            await FormulaRunProcessor.ProcessAsync(
                db, new FakeChatStreamClient(), settings, new FakeHubContext(),
                new FormulaRunJob(recId, null, resultId, formulaId, userId),
                NullLogger.Instance, new CapturingWebhookPublisher(), "http://test");
        }

        AssertNoCartesianJoin(sql.Statements, "formula run over a recording");
    }
}
