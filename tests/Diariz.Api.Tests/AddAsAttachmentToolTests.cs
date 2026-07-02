using System;
using System.Text.Json;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The add_as_attachment tool: it queues a Markdown-attachment draft for the client to save to one of
/// the selected recordings (single → auto, several → the user picks).</summary>
public class AddAsAttachmentToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Guid SeedRecording(DiarizDbContext db, Guid userId, string name)
    {
        var id = Guid.NewGuid();
        db.Recordings.Add(new Recording
        {
            Id = id, UserId = userId, Title = name, Name = name, BlobKey = "k", Status = RecordingStatus.Transcribed,
        });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Execute_SingleSelected_QueuesDraftForThatRecording()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = SeedRecording(db, me, "Standup");
        var effects = new ChatToolEffects();

        var result = await new AddAsAttachmentTool(db).ExecuteAsync(
            Args("""{"name":"Summary","content":"# Notes"}"""),
            new ChatToolContext(me, [rec], effects), default);

        var draft = Assert.Single(effects.AttachmentDrafts);
        Assert.Equal("Summary", draft.Name);
        Assert.Equal("# Notes", draft.Content);
        var target = Assert.Single(draft.Recordings);
        Assert.Equal(rec, target.Id);
        Assert.Equal("Standup", target.Title);
        Assert.Contains("Standup", result); // tells the model where it's going
    }

    [Fact]
    public async Task Execute_MultipleSelected_QueuesDraftWithAllCandidates()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var a = SeedRecording(db, me, "Standup");
        var b = SeedRecording(db, me, "Retro");
        var effects = new ChatToolEffects();

        var result = await new AddAsAttachmentTool(db).ExecuteAsync(
            Args("""{"name":"Summary","content":"x"}"""),
            new ChatToolContext(me, [a, b], effects), default);

        var draft = Assert.Single(effects.AttachmentDrafts);
        Assert.Equal(2, draft.Recordings.Count);
        Assert.Contains("choose", result, StringComparison.OrdinalIgnoreCase); // the user will pick
    }

    [Fact]
    public async Task Execute_OnlyCandidatesTheUserOwns_AreOffered()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = SeedRecording(db, me, "Mine");
        var theirs = SeedRecording(db, Guid.NewGuid(), "Theirs");
        var effects = new ChatToolEffects();

        await new AddAsAttachmentTool(db).ExecuteAsync(
            Args("""{"name":"n","content":"c"}"""),
            new ChatToolContext(me, [mine, theirs], effects), default);

        var draft = Assert.Single(effects.AttachmentDrafts);
        Assert.Equal(mine, Assert.Single(draft.Recordings).Id); // the other user's recording is not a candidate
    }

    [Fact]
    public async Task Execute_NoRecordingSelected_QueuesNothing_ReturnsGuidance()
    {
        using var db = TestDb.Create();
        var effects = new ChatToolEffects();

        var result = await new AddAsAttachmentTool(db).ExecuteAsync(
            Args("""{"name":"n","content":"c"}"""),
            new ChatToolContext(Guid.NewGuid(), [], effects), default);

        Assert.Empty(effects.AttachmentDrafts);
        Assert.Contains("select", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_MissingContent_QueuesNothing_ReturnsError()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = SeedRecording(db, me, "Standup");
        var effects = new ChatToolEffects();

        var result = await new AddAsAttachmentTool(db).ExecuteAsync(
            Args("""{"name":"n"}"""), new ChatToolContext(me, [rec], effects), default);

        Assert.Empty(effects.AttachmentDrafts);
        Assert.Contains("content", result, StringComparison.OrdinalIgnoreCase);
    }
}
