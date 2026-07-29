using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The <c>attendees</c> array carried by every recording event: who was in the meeting, and enough
/// about them for a workflow to route on without calling back.</summary>
public class AttendeePayloadTests
{
    private static async Task<Recording> SeedAsync(DiarizDbContext db, Action<DiarizDbContext, Recording> seed)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        seed(db, rec);
        await db.SaveChangesAsync();
        return rec;
    }

    /// <summary>Serialising and re-reading is the only honest check of "the key is absent", since an
    /// anonymous type with a null member and one without it look the same to a debugger.
    ///
    /// <para><b>Through the envelope's own options.</b> These previously used plain
    /// <c>JsonSerializer.Serialize</c>, which kept nulls - so the tests asserted a shape production never
    /// emits, and only a live payload revealed it. <see cref="WebhookPayload"/> sets
    /// <c>WhenWritingNull</c>, so an unidentified speaker has **no** <c>personId</c> or <c>isInternal</c>
    /// key at all rather than a null one.</para></summary>
    private static JsonElement AsJson(IReadOnlyList<object> attendees)
    {
        // Exactly what the envelope does, so a change to those options fails here rather than in the wild.
        var body = WebhookPayload.Build("evt_1", WebhookEventTypes.RecordingSummarized, Created, new { attendees });
        return JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("attendees");
    }

    private static readonly DateTimeOffset Created = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    private static Person Person(string name, bool isInternal = true) => new()
    {
        Id = Guid.NewGuid(), Name = name, Title = "Engineer", CompanyName = "Analytical Engines",
        Email = "ada@example.com", Phone = "+44", IsInternal = isInternal,
    };

    [Fact]
    public async Task ForRecording_OrdersByLabel_SoTheArrayIsStableAcrossEvents()
    {
        using var db = TestDb.Create();
        var rec = await SeedAsync(db, (d, r) =>
        {
            foreach (var label in new[] { "SPEAKER_02", "SPEAKER_00", "SPEAKER_01" })
                d.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = r.Id, Label = label, DisplayName = label });
        });

        var json = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: false));

        Assert.Equal(
            new[] { "SPEAKER_00", "SPEAKER_01", "SPEAKER_02" },
            json.EnumerateArray().Select(a => a.GetProperty("label").GetString() ?? "").ToArray());
    }

    [Fact]
    public async Task ForRecording_AnUnidentifiedSpeaker_HasNoPersonAndNoInternalFlag()
    {
        using var db = TestDb.Create();
        var rec = await SeedAsync(db, (d, r) => d.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = r.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
        }));

        var entry = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: true)).EnumerateArray().Single();

        // Absent, not null: the envelope drops nulls, so there is simply no personId or isInternal key.
        // A consumer testing `isInternal === false` still behaves correctly on undefined.
        Assert.False(entry.TryGetProperty("personId", out _));
        Assert.False(entry.TryGetProperty("isInternal", out _));
    }

    [Fact]
    public async Task ForRecording_WithoutContacts_OmitsThoseKeysEntirely()
    {
        using var db = TestDb.Create();
        var person = Person("Ada Lovelace");
        var rec = await SeedAsync(db, (d, r) =>
        {
            d.People.Add(person);
            d.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = r.Id, Label = "SPEAKER_00",
                DisplayName = "Ada Lovelace", PersonId = person.Id, IdentifiedAuto = true,
            });
        });

        var entry = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: false)).EnumerateArray().Single();

        // Absent rather than null, so a subscriber cannot read "not permitted" as "not known".
        foreach (var key in new[] { "email", "phone", "title", "companyName" })
            Assert.False(entry.TryGetProperty(key, out _), $"{key} must be absent without the contacts opt-in");

        Assert.Equal("Ada Lovelace", entry.GetProperty("name").GetString());
        Assert.True(entry.GetProperty("isInternal").GetBoolean());
        Assert.True(entry.GetProperty("identifiedAuto").GetBoolean());
    }

    [Fact]
    public async Task ForRecording_WithContacts_CarriesThem()
    {
        using var db = TestDb.Create();
        var person = Person("Ada Lovelace");
        var rec = await SeedAsync(db, (d, r) =>
        {
            d.People.Add(person);
            d.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = r.Id, Label = "SPEAKER_00",
                DisplayName = "Ada Lovelace", PersonId = person.Id,
            });
        });

        var entry = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: true)).EnumerateArray().Single();

        Assert.Equal("ada@example.com", entry.GetProperty("email").GetString());
        Assert.Equal("+44", entry.GetProperty("phone").GetString());
        Assert.Equal("Engineer", entry.GetProperty("title").GetString());
        Assert.Equal("Analytical Engines", entry.GetProperty("companyName").GetString());
    }

    /// <summary>Overlapping speech is not one person, so attaching a job title to it would be a lie - even
    /// if something has been linked to the slot.</summary>
    [Fact]
    public async Task ForRecording_AMultiSpeakerSlot_CarriesNoPersonDetails()
    {
        using var db = TestDb.Create();
        var person = Person("Ada Lovelace");
        var rec = await SeedAsync(db, (d, r) =>
        {
            d.People.Add(person);
            d.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = r.Id, Label = "SPEAKER_00",
                DisplayName = Speaker.MultiSpeakerName, PersonId = person.Id, IsMultiSpeaker = true,
            });
        });

        var entry = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: true)).EnumerateArray().Single();

        Assert.True(entry.GetProperty("isMultiSpeaker").GetBoolean());
        Assert.False(entry.TryGetProperty("isInternal", out _));
        Assert.False(entry.TryGetProperty("email", out _));
    }

    /// <summary>Opting out concerns holding someone's voiceprint, not the fact that they attended a
    /// meeting. Dropping them from the attendee list would misrepresent what happened.</summary>
    [Fact]
    public async Task ForRecording_AnOptedOutPerson_StillAppearsByName()
    {
        using var db = TestDb.Create();
        var person = Person("Ada Lovelace");
        person.VoiceprintOptOut = true;
        var rec = await SeedAsync(db, (d, r) =>
        {
            d.People.Add(person);
            d.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = r.Id, Label = "SPEAKER_00",
                DisplayName = "Ada Lovelace", PersonId = person.Id,
            });
        });

        var entry = AsJson(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: false)).EnumerateArray().Single();

        Assert.Equal("Ada Lovelace", entry.GetProperty("name").GetString());
        Assert.Equal(person.Id, entry.GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task ForRecording_WithNoSpeakers_IsEmpty()
    {
        using var db = TestDb.Create();
        var rec = await SeedAsync(db, (_, _) => { });

        Assert.Empty(await AttendeePayload.ForRecordingAsync(db, rec.Id, includeContacts: true));
    }
}
