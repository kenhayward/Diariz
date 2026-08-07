using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The id scheme for pushed Outlook events. Deterministic rather than a random surrogate, and short
/// rather than carrying the raw Outlook uid - both properties are load-bearing, see the individual tests.</summary>
public class OutlookEventIdTests
{
    private static readonly Guid Source = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSource = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The same occurrence keeps its id across syncs, so an event that vanishes from the window and
    /// comes back is the same row - and any <c>RecordingCalendarLink</c> or <c>MeetingNote</c> pointing at it
    /// still resolves. A random surrogate would silently orphan both.</summary>
    [Fact]
    public void For_IsDeterministic()
    {
        Assert.Equal(OutlookEventId.For(Source, "040000008200E00074C5B7101A82E008"),
                     OutlookEventId.For(Source, "040000008200E00074C5B7101A82E008"));
    }

    [Fact]
    public void For_DistinguishesUidsWithinASource()
    {
        Assert.NotEqual(OutlookEventId.For(Source, "uid-a"), OutlookEventId.For(Source, "uid-b"));
    }

    /// <summary>Two machines syncing the same mailbox are separate sources, so the same uid must not collide
    /// into one row - otherwise each device's sweep would delete the other's events.</summary>
    [Fact]
    public void For_DistinguishesSourcesForTheSameUid()
    {
        Assert.NotEqual(OutlookEventId.For(Source, "uid-a"), OutlookEventId.For(OtherSource, "uid-a"));
    }

    /// <summary>A well-formed v4-shaped Guid, so it round-trips through Postgres' uuid type and anything that
    /// inspects the variant bits.</summary>
    [Fact]
    public void For_ProducesAConformantGuid()
    {
        var bytes = OutlookEventId.For(Source, "uid-a").ToByteArray();
        Assert.Equal(0x40, bytes[7] & 0xF0);        // version 4 (Guid byte order: version nibble is byte 7)
        Assert.Equal(0x80, bytes[8] & 0xC0);        // RFC-4122 variant
    }

    /// <summary>THE constraint that keeps this feature migration-free.
    /// <para><c>MeetingNote.CalendarId</c>/<c>EventId</c> are <c>varchar(256)</c>, and
    /// <c>CalendarEventNotesController</c> clamps to 256 <b>on write</b> while list/update/delete filter on the
    /// raw route value - so an id over 256 chars produces a pre-meeting note that is saved and then permanently
    /// invisible. Outlook's <c>GlobalAppointmentID</c> routinely exceeds 256 chars, so the raw uid can never
    /// appear in the public id.</para></summary>
    [Theory]
    [InlineData("040000008200E00074C5B7101A82E00800000000B0A1B2C3D4E5F6")]
    [InlineData("a-very-long-globalappointmentid-that-embeds-an-external-organiser-ical-uid-and-keeps-going-and-going-and-going-well-past-any-reasonable-length-limit-because-exchange-encodes-the-whole-originating-uid-into-the-trailing-bytes-of-the-global-object-id-blob-0123456789abcdef")]
    public void PublicIds_StayShortEnoughForMeetingNotes(string uid)
    {
        var eventId = OutlookEventId.EventKey(OutlookEventId.For(Source, uid));
        var calendarId = OutlookEventId.CalendarKey(Source);

        Assert.Equal(44, eventId.Length);
        Assert.Equal(44, calendarId.Length);
        Assert.StartsWith("outlook:", eventId);
        Assert.StartsWith("outlook:", calendarId);
        // Comfortably inside MeetingNote's 256 and RecordingCalendarLink's 1024.
        Assert.True(eventId.Length < 256);
    }

    /// <summary>The round-trip the store needs to answer <c>GET /api/calendar/events/{id}</c>.</summary>
    [Fact]
    public void TryParseEventKey_RoundTripsAndRejectsForeignIds()
    {
        var id = OutlookEventId.For(Source, "uid-a");
        Assert.True(OutlookEventId.TryParseEventKey(OutlookEventId.EventKey(id), out var parsed));
        Assert.Equal(id, parsed);

        Assert.False(OutlookEventId.TryParseEventKey("ics:abc:def", out _));       // another source's scheme
        Assert.False(OutlookEventId.TryParseEventKey("outlook:not-a-guid", out _));
        Assert.False(OutlookEventId.TryParseEventKey(id.ToString(), out _));       // unprefixed
        Assert.False(OutlookEventId.TryParseEventKey("", out _));
    }
}
