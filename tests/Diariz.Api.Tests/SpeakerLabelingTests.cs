using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Pgvector;

namespace Diariz.Api.Tests;

public class SpeakerLabelingTests
{
    private static Speaker Spk(string label, string display, bool auto, Vector? embedding) =>
        new()
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(),
            Label = label, DisplayName = display, IdentifiedAuto = auto, Embedding = embedding,
        };

    [Fact]
    public async Task Applies_Match_To_Anonymous_Speaker()
    {
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, new Vector(new[] { 0.1f }));
        var profileId = Guid.NewGuid();
        var id = new FakeSpeakerIdentifier { Match = new SpeakerMatch(profileId, "Alice", 0.1) };

        await SpeakerLabeling.ApplyAsync([sp], Guid.NewGuid(), id);

        Assert.Equal(profileId, sp.ProfileId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.True(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Reverts_Stale_Auto_When_No_Match()
    {
        var sp = Spk("SPEAKER_00", "Alice", auto: true, new Vector(new[] { 0.1f }));
        sp.ProfileId = Guid.NewGuid();
        var id = new FakeSpeakerIdentifier { Match = null };

        await SpeakerLabeling.ApplyAsync([sp], Guid.NewGuid(), id);

        Assert.Null(sp.ProfileId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Skips_Manually_Named_Speaker()
    {
        var sp = Spk("SPEAKER_00", "Bob", auto: false, new Vector(new[] { 0.1f }));
        var id = new FakeSpeakerIdentifier { Match = new SpeakerMatch(Guid.NewGuid(), "Alice", 0.1) };

        await SpeakerLabeling.ApplyAsync([sp], Guid.NewGuid(), id);

        Assert.Equal("Bob", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Skips_Speaker_Without_Embedding()
    {
        var sp = Spk("SPEAKER_00", "SPEAKER_00", auto: false, embedding: null);
        var id = new FakeSpeakerIdentifier { Match = new SpeakerMatch(Guid.NewGuid(), "Alice", 0.1) };

        await SpeakerLabeling.ApplyAsync([sp], Guid.NewGuid(), id);

        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.Equal(0, id.Calls); // not even queried
    }
}
