using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Llm;

/// <summary>The factory's whole job is to be indistinguishable from the real pipeline. Every test here
/// compares its output against the SAME pure builder the production path calls, rather than against a
/// hand-written expected string - a copy of the prompt text would pass while the pipeline moved on.</summary>
public class LlmTestPromptFactoryTests
{
    private const int Budget = 20000;

    /// <summary>Returns the fallback for every template, which is what a deployment with no prompts/ volume
    /// gets. Named templates are asserted separately.</summary>
    private sealed class PassthroughTemplates : IPromptTemplateProvider
    {
        public List<string> Requested { get; } = [];

        public string Get(string name, string fallback)
        {
            Requested.Add(name);
            return fallback;
        }
    }

    private static (Guid OwnerId, Guid RecordingId) SeedRecording(
        DiarizDbContext db, string? name = null, bool withSegments = true)
    {
        var ownerId = Guid.NewGuid();
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = "2026-08-20 team sync", Name = name,
            Status = RecordingStatus.Transcribed, CreatedAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            StartedAt = new DateTimeOffset(2026, 8, 20, 8, 55, 0, TimeSpan.Zero),
        };
        db.Recordings.Add(rec);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Priya",
        });

        var transcription = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1, CreatedAt = rec.CreatedAt,
        };
        db.Transcriptions.Add(transcription);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = transcription.Id, Ordinal = 0,
                SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 4000,
                Original = "The Q3 forecast needs revising before Friday.",
            });
        db.SaveChanges();
        return (ownerId, rec.Id);
    }

    private static LlmTestPromptFactory Build(DiarizDbContext db, IPromptTemplateProvider templates) =>
        new(db, templates);

    [Fact]
    public async Task Tags_build_the_same_messages_as_the_real_tags_call()
    {
        using var db = TestDb.Create();
        var (ownerId, recordingId) = SeedRecording(db);
        var templates = new PassthroughTemplates();

        var prompt = await Build(db, templates).BuildAsync(LlmCallGroup.Tags, recordingId, ownerId, Budget);

        var segments = SegmentMapper.ToDtos(
            db.Segments.ToList(), new Dictionary<string, string> { ["SPEAKER_00"] = "Priya" });
        var expected = TagsPrompt.BuildMessages(TagsPrompt.DefaultTemplate, segments, Budget);

        Assert.NotNull(prompt);
        Assert.Equal("Tags", prompt!.ParsedKind);
        Assert.Equal(expected.Select(m => (m.Role, m.Content)), prompt.Messages.Select(m => (m.Role, m.Content)));
        Assert.Contains("tagcloud", templates.Requested);
    }

    [Fact]
    public async Task Actions_carry_the_recordings_own_date_so_deadlines_resolve()
    {
        // ActionsPrompt substitutes {calendar_date}; the pipeline passes StartedAt ?? CreatedAt. A test that
        // fed today's date would silently change every relative deadline the model resolves.
        using var db = TestDb.Create();
        var (ownerId, recordingId) = SeedRecording(db);

        var prompt = await Build(db, new PassthroughTemplates())
            .BuildAsync(LlmCallGroup.Actions, recordingId, ownerId, Budget);

        Assert.NotNull(prompt);
        Assert.Equal("Actions", prompt!.ParsedKind);
        Assert.Contains("2026-08-20", prompt.Messages[0].Content);
    }

    [Fact]
    public async Task Summaries_ask_for_a_title_only_when_the_recording_has_no_name()
    {
        // Faithful to the pipeline: the summariser names a recording only when the user has not.
        using var db = TestDb.Create();
        var (unnamedOwner, unnamed) = SeedRecording(db, name: null);
        var (namedOwner, named) = SeedRecording(db, name: "Weekly sync");
        var factory = Build(db, new PassthroughTemplates());

        var forUnnamed = await factory.BuildAsync(LlmCallGroup.Summaries, unnamed, unnamedOwner, Budget);
        var forNamed = await factory.BuildAsync(LlmCallGroup.Summaries, named, namedOwner, Budget);

        Assert.Contains("concise title", forUnnamed!.Messages[0].Content);
        Assert.DoesNotContain("concise title", forNamed!.Messages[0].Content);
        Assert.Equal("Summary", forUnnamed.ParsedKind);
    }

    [Fact]
    public async Task Speaker_display_names_reach_the_transcript()
    {
        using var db = TestDb.Create();
        var (ownerId, recordingId) = SeedRecording(db);

        var prompt = await Build(db, new PassthroughTemplates())
            .BuildAsync(LlmCallGroup.Tags, recordingId, ownerId, Budget);

        Assert.Contains("Priya", prompt!.Messages[1].Content);
    }

    [Fact]
    public async Task Refuses_a_recording_belonging_to_someone_else()
    {
        // The panel is ManagePlatform-gated, so without this an administrator could send any user's meeting
        // to a third-party endpoint and read the reply.
        using var db = TestDb.Create();
        var (_, recordingId) = SeedRecording(db);

        var prompt = await Build(db, new PassthroughTemplates())
            .BuildAsync(LlmCallGroup.Tags, recordingId, ownerId: Guid.NewGuid(), Budget);

        Assert.Null(prompt);
    }

    [Fact]
    public async Task Refuses_a_recording_with_no_segments()
    {
        using var db = TestDb.Create();
        var (ownerId, recordingId) = SeedRecording(db, withSegments: false);

        Assert.Null(await Build(db, new PassthroughTemplates())
            .BuildAsync(LlmCallGroup.Tags, recordingId, ownerId, Budget));
    }

    [Theory]
    [InlineData(LlmCallGroup.Tags, true)]
    [InlineData(LlmCallGroup.Actions, true)]
    [InlineData(LlmCallGroup.Summaries, true)]
    [InlineData(LlmCallGroup.ModelBase, false)]
    [InlineData(LlmCallGroup.MinutesAndFormulas, false)]
    [InlineData(LlmCallGroup.Translation, false)]
    [InlineData(LlmCallGroup.Chat, false)]
    public void Only_three_groups_need_a_recording(LlmCallGroup group, bool needs)
    {
        using var db = TestDb.Create();
        Assert.Equal(needs, Build(db, new PassthroughTemplates()).NeedsRecording(group));
    }
}
