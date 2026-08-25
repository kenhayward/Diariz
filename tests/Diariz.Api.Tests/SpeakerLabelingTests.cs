using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Pgvector;

namespace Diariz.Api.Tests;

/// <summary>Applying a verdict to a <see cref="Speaker"/> row. The policy itself lives in
/// <see cref="IdentificationRules"/> and has its own tests; this covers the part that decides which speakers
/// are eligible at all, and what a verdict does to the row.</summary>
public class SpeakerLabelingTests
{
    private static readonly IdentificationThresholds T = new(0.30, 0.40, 0.05, 3000);

    /// <summary>Plenty of speech, so the minimum-speech gate never confuses one of these tests.</summary>
    private static readonly Dictionary<string, long> Speech = new() { ["SPEAKER_00"] = 30_000 };

    private static Speaker Spk(string label, string display, bool auto, Vector? embedding) =>
        new()
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(),
            Label = label, DisplayName = display, IdentifiedAuto = auto, Embedding = embedding,
        };

    private static FakeSpeakerIdentifier Near(Guid personId, double distance = 0.1)
    {
        var id = new FakeSpeakerIdentifier();
        id.Nearest(personId, "Alice", distance);
        return id;
    }

    [Fact]
    public async Task Applies_Match_To_Anonymous_Speaker()
    {
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, new Vector(new[] { 0.1f }));
        var profileId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync([sp], Near(profileId), T, Speech);

        Assert.Equal(profileId, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.True(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Reverts_Stale_Auto_When_No_Match()
    {
        var sp = Spk("SPEAKER_00", "Alice", auto: true, new Vector(new[] { 0.1f }));
        sp.PersonId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync([sp], new FakeSpeakerIdentifier(), T, Speech);

        Assert.Null(sp.PersonId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Reverts_Stale_Auto_When_The_Match_Only_Reaches_A_Suggestion()
    {
        // A name applied at full confidence must not be kept alive by a borderline distance. Leaving it would
        // let a label decay quietly from "recognised" to "probably", with nothing saying so.
        var sp = Spk("SPEAKER_00", "Alice", auto: true, new Vector(new[] { 0.1f }));
        sp.PersonId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync([sp], Near(Guid.NewGuid(), distance: 0.35), T, Speech);

        Assert.Null(sp.PersonId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Skips_Manually_Named_Speaker()
    {
        var sp = Spk("SPEAKER_00", "Bob", auto: false, new Vector(new[] { 0.1f }));

        await SpeakerLabeling.ApplyAsync([sp], Near(Guid.NewGuid()), T, Speech);

        Assert.Equal("Bob", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Skips_Speaker_Without_Embedding()
    {
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, embedding: null);
        var id = Near(Guid.NewGuid());

        await SpeakerLabeling.ApplyAsync([sp], id, T, Speech);

        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.Equal(0, id.Calls); // not even queried
    }

    [Fact]
    public async Task Skips_MultiSpeaker_EvenWithEmbedding()
    {
        // "Multiple Speakers" is overlapping audio — never match it against a single-person voiceprint.
        var sp = Spk("SPEAKER_00", Speaker.MultiSpeakerName, auto: false, new Vector(new[] { 0.1f }));
        sp.IsMultiSpeaker = true;
        var id = Near(Guid.NewGuid());

        await SpeakerLabeling.ApplyAsync([sp], id, T, Speech);

        Assert.Equal(Speaker.MultiSpeakerName, sp.DisplayName);
        Assert.Null(sp.PersonId);
        Assert.Equal(0, id.Calls); // not even queried
    }

    [Fact]
    public async Task Leaves_A_Speaker_With_Too_Little_Speech_Anonymous()
    {
        // The gate is measured per label, so this is where a barely-speaking participant stops being scored.
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, new Vector(new[] { 0.1f }));

        await SpeakerLabeling.ApplyAsync(
            [sp], Near(Guid.NewGuid()), T, new Dictionary<string, long> { ["SPEAKER_00"] = 1_200 });

        Assert.Null(sp.PersonId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
    }

    [Fact]
    public async Task Treats_A_Speaker_Missing_From_The_Speech_Map_As_Silent()
    {
        // A speaker with no segments in the current transcription has said nothing measurable. Reading that
        // as zero rather than throwing keeps one odd row from failing a whole recording's identification.
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, new Vector(new[] { 0.1f }));

        await SpeakerLabeling.ApplyAsync([sp], Near(Guid.NewGuid()), T, new Dictionary<string, long>());

        Assert.Null(sp.PersonId);
    }
}
