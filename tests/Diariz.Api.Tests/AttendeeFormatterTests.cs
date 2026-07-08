using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Formatting a recording's attendee display names for the template's <c>attendees</c> field: identified
/// people are listed by name; the remaining auto-labelled speakers (SPEAKER_nn) collapse to a count.</summary>
public class AttendeeFormatterTests
{
    [Fact]
    public void Null_or_empty_yields_null()
    {
        Assert.Null(AttendeeFormatter.Summarize(null));
        Assert.Null(AttendeeFormatter.Summarize([]));
        Assert.Null(AttendeeFormatter.Summarize(["", "   "]));
    }

    [Fact]
    public void All_identified_are_listed_by_name()
    {
        Assert.Equal("Alice, Bob", AttendeeFormatter.Summarize(["Alice", "Bob"]));
    }

    [Fact]
    public void Only_unidentified_collapse_to_a_count()
    {
        Assert.Equal("3 unidentified attendees",
            AttendeeFormatter.Summarize(["SPEAKER_00", "SPEAKER_01", "SPEAKER_02"]));
    }

    [Fact]
    public void A_single_unidentified_uses_the_singular()
    {
        Assert.Equal("1 unidentified attendee", AttendeeFormatter.Summarize(["SPEAKER_00"]));
    }

    [Fact]
    public void Identified_names_then_the_unidentified_count()
    {
        Assert.Equal("Alice, Bob and 11 unidentified attendees",
            AttendeeFormatter.Summarize(
                ["Alice", "SPEAKER_00", "Bob", .. Enumerable.Range(1, 10).Select(i => $"SPEAKER_{i:D2}")]));
    }

    [Fact]
    public void The_UNKNOWN_placeholder_counts_as_unidentified()
    {
        // Segments with no attributed speaker carry the literal "UNKNOWN" label (Segment.SpeakerLabel default).
        Assert.Equal("1 unidentified attendee", AttendeeFormatter.Summarize(["UNKNOWN"]));
        Assert.Equal("Ken Hayward and 4 unidentified attendees",
            AttendeeFormatter.Summarize(["Ken Hayward", "UNKNOWN", "SPEAKER_00", "SPEAKER_02", "SPEAKER_03"]));
    }

    [Fact]
    public void Whitespace_names_are_ignored()
    {
        Assert.Equal("Alice and 1 unidentified attendee",
            AttendeeFormatter.Summarize(["  Alice  ", "", "SPEAKER_04"]));
    }
}
