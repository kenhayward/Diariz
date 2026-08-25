using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Turning a person's attributed speakers into the rows the Voiceprint tab renders.
///
/// <para>The point of this projection is that the tab used to list only <c>ProfileContributions</c> - what
/// was enrolled by hand - which made the set look arbitrary, because auto-identification links a speaker
/// without ever creating one. Every attributed speaker appears here; training is a flag on the row, not the
/// reason the row exists.</para></summary>
public class PersonAttributionsTests
{
    private static readonly Guid Rec = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> Names = new() { [Rec] = "Standup" };

    private static AttributionInput Speaker(Guid id, bool auto = false, long speechMs = 30000) =>
        new(id, Rec, "SPEAKER_00", auto, IsMultiSpeaker: false, speechMs, StillLinked: true);

    [Fact]
    public void Build_includes_a_speaker_with_no_voice_sample()
    {
        // The case that made the old list look random: auto-identification links a speaker without ever
        // creating a contribution row, so it was invisible in the tab.
        var id = Guid.NewGuid();

        var rows = PersonAttributions.Build([Speaker(id, auto: true)], [], Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec });

        var row = Assert.Single(rows);
        Assert.Equal(id, row.SpeakerId);
        Assert.False(row.IsTraining);
        Assert.Null(row.VoiceSampleId);
        Assert.Equal("auto", row.LinkedBy);
    }

    [Fact]
    public void Build_marks_a_speaker_with_a_sample_as_training()
    {
        var id = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        List<VoiceSample> samples = [new() { Id = sampleId, SpeakerId = id, RecordingId = Rec }];

        var row = Assert.Single(
            PersonAttributions.Build([Speaker(id)], samples, Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));

        Assert.True(row.IsTraining);
        Assert.Equal(sampleId, row.VoiceSampleId);
        Assert.Equal("manual", row.LinkedBy);
    }

    [Fact]
    public void Build_treats_an_excluded_sample_as_not_training_but_keeps_its_id()
    {
        // Excluded is not deleted: the row still points at the sample, so re-including it is a toggle rather
        // than a fresh enrolment, and the original assertion is not lost.
        var id = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        List<VoiceSample> samples =
            [new() { Id = sampleId, SpeakerId = id, RecordingId = Rec, ExcludedAt = DateTimeOffset.UtcNow }];

        var row = Assert.Single(
            PersonAttributions.Build([Speaker(id)], samples, Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));

        Assert.False(row.IsTraining);
        Assert.Equal(sampleId, row.VoiceSampleId);
    }

    [Fact]
    public void Build_flags_a_recording_the_caller_cannot_read()
    {
        // The directory is platform-wide but recordings are ownership-filtered, and this is live, not
        // theoretical. The row must still appear - it is genuinely part of what trained the voiceprint - but
        // the UI has to know not to offer a transcript or a play button.
        var id = Guid.NewGuid();

        var row = Assert.Single(PersonAttributions.Build([Speaker(id)], [], Names, new HashSet<Guid>(), new HashSet<Guid>()));

        Assert.False(row.CanAccessRecording);
    }

    [Fact]
    public void Build_names_a_recording_that_no_longer_exists()
    {
        // VoiceSample deliberately has no FK to its recording, so a deleted recording leaves the sample
        // behind rather than taking it with it.
        var id = Guid.NewGuid();

        var row = Assert.Single(
            PersonAttributions.Build([Speaker(id)], [], new Dictionary<Guid, string>(), new HashSet<Guid>(), new HashSet<Guid>()));

        Assert.Equal("(deleted recording)", row.RecordingName);
    }

    [Fact]
    public void Build_excludes_multi_speaker_slots()
    {
        // Overlapping audio is a mix of people. It can never train a single-person voiceprint, so offering it
        // as a candidate would be offering something the server will refuse.
        var id = Guid.NewGuid();
        var multi = new AttributionInput(id, Rec, "SPEAKER_01", false, IsMultiSpeaker: true, 30000,
            StillLinked: false);

        Assert.Empty(PersonAttributions.Build([multi], [], Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));
    }

    [Fact]
    public void Build_carries_the_speech_duration_through()
    {
        var id = Guid.NewGuid();

        var row = Assert.Single(
            PersonAttributions.Build([Speaker(id, speechMs: 4321)], [], Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));

        Assert.Equal(4321, row.SpeechMs);
    }

    [Fact]
    public void Build_orders_by_recording_name_then_label()
    {
        var recB = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [Rec] = "Zulu", [recB] = "Alpha" };
        var a = new AttributionInput(Guid.NewGuid(), recB, "SPEAKER_00", false, false, 1000, true);
        var z = new AttributionInput(Guid.NewGuid(), Rec, "SPEAKER_00", false, false, 1000, true);

        var rows = PersonAttributions.Build([z, a], [], names, new HashSet<Guid> { Rec, recB }, new HashSet<Guid> { Rec, recB });

        Assert.Equal(["Alpha", "Zulu"], rows.Select(r => r.RecordingName));
    }

    [Fact]
    public void Build_matches_a_sample_to_its_own_speaker_only()
    {
        // Two speakers of the same person in different recordings. Keying the lookup on anything but the
        // speaker id would mark both as training off one sample.
        var trained = Guid.NewGuid();
        var untrained = Guid.NewGuid();
        var recB = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [Rec] = "One", [recB] = "Two" };
        List<VoiceSample> samples = [new() { Id = Guid.NewGuid(), SpeakerId = trained, RecordingId = Rec }];
        var other = new AttributionInput(untrained, recB, "SPEAKER_00", false, false, 1000, true);

        var rows = PersonAttributions.Build(
            [Speaker(trained), other], samples, names, new HashSet<Guid> { Rec, recB }, new HashSet<Guid> { Rec, recB });

        Assert.True(rows.Single(r => r.SpeakerId == trained).IsTraining);
        Assert.False(rows.Single(r => r.SpeakerId == untrained).IsTraining);
    }

    [Fact]
    public void A_sample_whose_speaker_moved_is_listed_but_not_training()
    {
        // Six of these were found live. Hiding them would repeat the original defect in a new place -
        // invisible is exactly how they survived - so the row stays and says so.
        var speakerId = Guid.NewGuid();
        var orphan = new AttributionInput(speakerId, Rec, "SPEAKER_00", false, false, 30000,
            StillLinked: false);
        List<VoiceSample> samples = [new() { Id = Guid.NewGuid(), SpeakerId = speakerId, RecordingId = Rec }];

        var row = Assert.Single(PersonAttributions.Build(
            [orphan], samples, Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));

        Assert.False(row.StillLinked);
        Assert.False(row.IsTraining);
    }

    [Fact]
    public void A_moved_speaker_with_nothing_left_to_show_is_not_listed()
    {
        // No sample means nothing of this person's voiceprint came from it, so there is nothing to see and
        // nothing to undo. Listing it would fill the tab with every speaker anyone ever unassigned.
        var orphan = new AttributionInput(Guid.NewGuid(), Rec, "SPEAKER_00", false, false, 30000,
            StillLinked: false);

        Assert.Empty(PersonAttributions.Build(
            [orphan], [], Names, new HashSet<Guid> { Rec }, new HashSet<Guid> { Rec }));
    }

    [Fact]
    public void A_speaker_can_be_reassigned_only_in_a_recording_you_own()
    {
        // Manage voiceprints grants listening to a segment for assessment. It does not grant editing someone
        // else's transcript, and AssignSpeaker enforces ownership regardless - so offering the control on a
        // recording you merely have access to would produce a button that always fails.
        var recB = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [Rec] = "Mine", [recB] = "Theirs" };
        var mine = new AttributionInput(Guid.NewGuid(), Rec, "SPEAKER_00", false, false, 1000, true);
        var theirs = new AttributionInput(Guid.NewGuid(), recB, "SPEAKER_00", false, false, 1000, true);

        var rows = PersonAttributions.Build(
            [mine, theirs], [], names,
            accessibleRecordings: new HashSet<Guid> { Rec, recB },
            ownedRecordings: new HashSet<Guid> { Rec });

        Assert.True(rows.Single(r => r.RecordingId == Rec).CanReassign);
        Assert.False(rows.Single(r => r.RecordingId == recB).CanReassign);
        Assert.True(rows.Single(r => r.RecordingId == recB).CanAccessRecording);
    }
}
