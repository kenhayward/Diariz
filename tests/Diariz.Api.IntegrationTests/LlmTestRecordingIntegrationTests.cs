using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The recording an administrator tests AI models against: the stored choice, and the prompt built
/// from that recording.
///
/// <para>These live here rather than in the unit project for two reasons the in-memory provider cannot
/// cover: a nullable column is accepted by that provider whether or not a migration exists, and it ignores
/// ordering inside a filtered query, so a factory that forgot its OrderBy would pass there while handing
/// the model a shuffled meeting.</para></summary>
[Collection(IntegrationCollection.Name)]
public class LlmTestRecordingIntegrationTests(ContainersFixture fx)
{
    private static async Task<Guid> SeedUserAsync(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task The_remembered_recording_round_trips_through_real_postgres()
    {
        // A nullable Guid column is trivial in C# and still has to exist in the database: the in-memory
        // provider would accept this property with no migration at all.
        await using var db = fx.CreateDbContext();
        var userId = await SeedUserAsync(db);
        var recordingId = Guid.NewGuid();

        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTestRecordingId = recordingId });
        await db.SaveChangesAsync();

        await using var fresh = fx.CreateDbContext();
        var stored = await fresh.UserSettings.AsNoTracking().FirstAsync(s => s.UserId == userId);
        Assert.Equal(recordingId, stored.LlmTestRecordingId);
    }

    [Fact]
    public async Task Defaults_to_null_for_an_administrator_who_has_never_chosen()
    {
        await using var db = fx.CreateDbContext();
        var userId = await SeedUserAsync(db);

        db.UserSettings.Add(new UserSettings { UserId = userId });
        await db.SaveChangesAsync();

        await using var fresh = fx.CreateDbContext();
        Assert.Null((await fresh.UserSettings.AsNoTracking().FirstAsync(s => s.UserId == userId)).LlmTestRecordingId);
    }

    /// <summary>Creates a recording with one transcription (version 1) and one segment per text, inserted in
    /// the order given rather than in ordinal order.</summary>
    private static async Task<Guid> SeedRecordingWithSegmentsAsync(
        DiarizDbContext db, Guid ownerId, string[] texts, int[] ordinals)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownerId, BlobKey = $"k-{Guid.NewGuid():N}", Title = "Team sync",
            Status = RecordingStatus.Transcribed, CreatedAt = DateTimeOffset.UtcNow,
        };
        var transcription = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(transcription);
        for (var i = 0; i < texts.Length; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = transcription.Id, Ordinal = ordinals[i],
                SpeakerLabel = "SPEAKER_00", StartMs = ordinals[i] * 1000, EndMs = ordinals[i] * 1000 + 900,
                Original = texts[i],
            });
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static async Task AddTranscriptionVersionAsync(
        DiarizDbContext db, Guid recordingId, int version, string text)
    {
        var transcription = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, Version = version, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Transcriptions.Add(transcription);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = transcription.Id, Ordinal = 0,
            SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 900, Original = text,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FallbackTemplates : IPromptTemplateProvider
    {
        public string Get(string name, string fallback) => fallback;
    }

    [Fact]
    public async Task Builds_the_transcript_in_segment_order_against_real_postgres()
    {
        // Inserted out of order on purpose. The unit tests cannot prove this: the in-memory provider ignores
        // ordering inside a filtered query, so a factory that forgot OrderBy would still pass there and hand
        // the model a shuffled meeting.
        await using var db = fx.CreateDbContext();
        var ownerId = await SeedUserAsync(db);
        var recordingId = await SeedRecordingWithSegmentsAsync(
            db, ownerId, ["third", "first", "second"], ordinals: [2, 0, 1]);

        var factory = new LlmTestPromptFactory(db, new FallbackTemplates());
        var prompt = await factory.BuildAsync(LlmCallGroup.Tags, recordingId, ownerId, 20000);

        var transcript = prompt!.Messages[1].Content;
        Assert.True(
            transcript.IndexOf("first", StringComparison.Ordinal)
            < transcript.IndexOf("second", StringComparison.Ordinal),
            $"Segments came out of order:\n{transcript}");
        Assert.True(
            transcript.IndexOf("second", StringComparison.Ordinal)
            < transcript.IndexOf("third", StringComparison.Ordinal),
            $"Segments came out of order:\n{transcript}");
    }

    [Fact]
    public async Task Will_not_build_a_prompt_from_another_users_recording()
    {
        await using var db = fx.CreateDbContext();
        var owner = await SeedUserAsync(db);
        var admin = await SeedUserAsync(db);
        var recordingId = await SeedRecordingWithSegmentsAsync(db, owner, ["hello"], ordinals: [0]);

        var factory = new LlmTestPromptFactory(db, new FallbackTemplates());

        Assert.Null(await factory.BuildAsync(LlmCallGroup.Tags, recordingId, admin, 20000));
    }

    [Fact]
    public async Task Uses_the_highest_transcription_version()
    {
        // Re-transcribing bumps the version. Testing against a superseded transcript would show an admin
        // output for text the platform no longer holds.
        await using var db = fx.CreateDbContext();
        var ownerId = await SeedUserAsync(db);
        var recordingId = await SeedRecordingWithSegmentsAsync(db, ownerId, ["old text"], ordinals: [0]);
        await AddTranscriptionVersionAsync(db, recordingId, version: 2, text: "new text");

        var factory = new LlmTestPromptFactory(db, new FallbackTemplates());
        var prompt = await factory.BuildAsync(LlmCallGroup.Tags, recordingId, ownerId, 20000);

        Assert.Contains("new text", prompt!.Messages[1].Content);
        Assert.DoesNotContain("old text", prompt.Messages[1].Content);
    }
}
