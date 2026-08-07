using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Services;

/// <summary>Identity for calendar events pushed from a desktop Outlook connector.
///
/// <para><b>Why the raw Outlook uid never appears in the public id.</b> Outlook's
/// <c>GlobalAppointmentID</c> - the stable per-occurrence key, and the same value EWS returns as
/// <c>calendar:UID</c> - routinely runs past 256 characters, because Exchange encodes the originating
/// iCalendar UID into the trailing bytes of the Global Object ID. That matters because
/// <c>MeetingNote.CalendarId</c>/<c>EventId</c> are <c>varchar(256)</c> and
/// <c>CalendarEventNotesController</c> clamps to 256 <b>on write</b> while list/update/delete filter on the
/// raw route value: a longer id saves a pre-meeting note under a truncated key and then can never read it
/// back. Hashing to a Guid keeps every public id at 44 characters, so it fits that column (and
/// <c>RecordingCalendarLink</c>'s 1024) with no migration and no truncation.</para>
///
/// <para><b>Why a hash rather than a random surrogate.</b> The id must be stable across syncs. An event that
/// drops out of the rolling window and later returns has to come back as the <i>same</i> row, or every
/// <see cref="Diariz.Domain.Entities.RecordingCalendarLink"/> and
/// <see cref="Diariz.Domain.Entities.MeetingNote"/> pointing at it silently orphans.</para>
///
/// <para>Scoped by source id, so two machines syncing the same mailbox stay independent - otherwise each
/// device's orphan sweep would delete the other's events.</para></summary>
public static class OutlookEventId
{
    /// <summary>Namespace prefix for both event and calendar ids, mirroring the <c>ics:</c> scheme.</summary>
    public const string Prefix = "outlook:";

    /// <summary>The stable row id for one occurrence: the first 16 bytes of
    /// <c>SHA-256(sourceId || uid)</c>, stamped with the RFC-4122 version-4 and variant bits so it is a
    /// well-formed Guid. 128 bits of hash - no practical collision risk across one user's calendar.</summary>
    public static Guid For(Guid sourceId, string uid)
    {
        var uidBytes = Encoding.UTF8.GetBytes(uid ?? string.Empty);
        var buffer = new byte[16 + uidBytes.Length];
        sourceId.ToByteArray().CopyTo(buffer, 0);
        uidBytes.CopyTo(buffer, 16);

        var hash = SHA256.HashData(buffer);
        var guidBytes = hash[..16];
        // Guid's byte order puts the version nibble in byte 7 and the variant in byte 8.
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40); // version 4
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC-4122 variant
        return new Guid(guidBytes);
    }

    /// <summary>The public event id (44 chars), e.g. <c>outlook:5f2c...-...</c>.</summary>
    public static string EventKey(Guid eventId) => $"{Prefix}{eventId:D}";

    /// <summary>The public calendar id (44 chars) - one per connected device, mirroring <c>ics:{sourceId}</c>.</summary>
    public static string CalendarKey(Guid sourceId) => $"{Prefix}{sourceId:D}";

    /// <summary>Parse a public event or calendar id back to its Guid. False for another source's scheme
    /// (<c>ics:</c>, a bare Google id) so a caller can route by prefix without throwing.</summary>
    public static bool TryParseEventKey(string? key, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return Guid.TryParseExact(key[Prefix.Length..], "D", out id);
    }
}
