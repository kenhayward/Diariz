using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The people directory API. Replaces <c>api/speaker-profiles</c>, whose cases are carried over
/// here - this is a 0.x product with no external API consumers, and running both surfaces would double the
/// n8n node's operation list and guarantee the two drift.</summary>
public class PeopleControllerTests
{
    private static PeopleController Build(
        DiarizDbContext db, Guid userId, PlatformPermission perms = PlatformPermission.ManagePeople)
    {
        Users.Ensure(db, userId);
        Perms.Grant(db, userId, perms);
        return new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue())
        {
            ControllerContext = Http.Context(userId),
        };
    }

    /// <summary>Build with a queue the test can inspect - the spans endpoint's whole job is to enqueue.</summary>
    private static PeopleController BuildWithQueue(
        DiarizDbContext db, Guid userId, FakeJobQueue queue,
        PlatformPermission perms = PlatformPermission.ManagePeople)
    {
        Users.Ensure(db, userId);
        if (perms != PlatformPermission.None) Perms.Grant(db, userId, perms);
        return new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), queue)
        {
            ControllerContext = Http.Context(userId),
        };
    }

    private static async Task<Person> SeedPerson(
        DiarizDbContext db, string name, string? email = null, bool isInternal = false,
        int sampleCount = 0, Guid? linkedUserId = null, string? company = null)
    {
        var person = new Person
        {
            Id = Guid.NewGuid(), Name = name, Email = email, IsInternal = isInternal,
            SampleCount = sampleCount, LinkedUserId = linkedUserId, CompanyName = company,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person;
    }

    private static IReadOnlyList<PersonDto> Ok(ActionResult<IReadOnlyList<PersonDto>> result) =>
        Assert.IsAssignableFrom<IReadOnlyList<PersonDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // ---- List ----

    [Fact]
    public async Task List_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada");

        var result = await Build(db, Guid.NewGuid(), PlatformPermission.None).List();

        Assert.IsType<ForbidResult>(result.Result);
    }

    /// <summary>Note the caller's own person is in here too: List self-heals by provisioning it, so a user
    /// created by a path that forgot always appears in their own directory.</summary>
    [Fact]
    public async Task List_ReturnsPeopleEnrolledByAnyone()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada");
        await SeedPerson(db, "Grace");

        var people = Ok(await Build(db, Guid.NewGuid()).List());

        Assert.Contains(people, p => p.Name == "Ada");
        Assert.Contains(people, p => p.Name == "Grace");
    }

    /// <summary>Matching email as well as name is what makes the directory usable once it holds contact
    /// details - the same choice AddMemberTypeahead already made.</summary>
    [Fact]
    public async Task List_FiltersByQuery_MatchingNameOrEmail()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace", "ada@example.com");
        await SeedPerson(db, "Grace Hopper", "grace@navy.mil");

        var byName = Ok(await Build(db, Guid.NewGuid()).List(q: "lovelace"));
        var byEmail = Ok(await Build(db, Guid.NewGuid()).List(q: "NAVY"));

        Assert.Equal("Ada Lovelace", Assert.Single(byName).Name);
        Assert.Equal("Grace Hopper", Assert.Single(byEmail).Name);
    }

    /// <summary>Company matters for the directory: "show me everyone from Acme" is a real question, and the
    /// person asking may not know their names. Search (the speaker picker) deliberately does NOT match
    /// company - there it would surface colleagues who were never in the meeting.</summary>
    [Fact]
    public async Task List_FiltersByQuery_MatchingCompany()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace", company: "Analytical Engines");
        await SeedPerson(db, "Grace Hopper", company: "US Navy");

        var hits = Ok(await Build(db, Guid.NewGuid()).List(q: "analytical"));

        Assert.Equal("Ada Lovelace", Assert.Single(hits).Name);
    }

    [Fact]
    public async Task Search_DoesNotMatchCompany()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace", company: "Analytical Engines");

        Assert.Empty(Ok(await Build(db, Guid.NewGuid(), PlatformPermission.None).Search("analytical")));
    }

    [Fact]
    public async Task List_FiltersByInternal()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Colleague", isInternal: true);
        await SeedPerson(db, "Client", isInternal: false);

        var internals = Ok(await Build(db, Guid.NewGuid()).List(isInternal: true));
        var externals = Ok(await Build(db, Guid.NewGuid()).List(isInternal: false));

        Assert.Contains(internals, p => p.Name == "Colleague");
        Assert.DoesNotContain(internals, p => p.Name == "Client");
        Assert.Equal("Client", Assert.Single(externals).Name);
    }

    [Fact]
    public async Task List_FiltersByHasVoiceprint()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Enrolled", sampleCount: 2);
        await SeedPerson(db, "Never enrolled");

        var withPrint = Ok(await Build(db, Guid.NewGuid()).List(hasVoiceprint: true));
        var without = Ok(await Build(db, Guid.NewGuid()).List(hasVoiceprint: false));

        Assert.Equal("Enrolled", Assert.Single(withPrint).Name);
        Assert.Contains(without, p => p.Name == "Never enrolled");
        Assert.DoesNotContain(without, p => p.Name == "Enrolled");
    }

    // ---- Search (ungated) ----

    /// <summary>Labelling a speaker must not need a permission, or nobody without ManagePeople could name
    /// anyone in their own recording. Search is therefore open, and returns only what a picker needs.</summary>
    [Fact]
    public async Task Search_WithoutManagePeople_Succeeds()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace", "ada@example.com");

        var result = await Build(db, Guid.NewGuid(), PlatformPermission.None).Search("ada");

        Assert.Equal("Ada Lovelace", Assert.Single(Ok(result)).Name);
    }

    [Fact]
    public async Task Search_WithAShortQuery_ReturnsNothing()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace");

        Assert.Empty(Ok(await Build(db, Guid.NewGuid(), PlatformPermission.None).Search("a")));
    }

    // ---- Create ----

    [Fact]
    public async Task Create_WithoutARecording_CreatesAPersonWithNoVoiceprint()
    {
        using var db = TestDb.Create();

        var result = await Build(db, Guid.NewGuid()).Create(new CreatePersonRequest(
            "Ada Lovelace", "Engineer", "Analytical Engines", "ada@example.com", "+44", true, null, null, null));

        var dto = Assert.IsType<CreatedAtActionResult>(result.Result).Value as PersonDto;
        Assert.NotNull(dto);
        Assert.Equal("Ada Lovelace", dto!.Name);
        Assert.Equal("Engineer", dto.Title);
        Assert.Equal("Analytical Engines", dto.CompanyName);
        Assert.True(dto.IsInternal);
        Assert.False(dto.HasVoiceprint);
        Assert.Equal(0, dto.SampleCount);
    }

    [Fact]
    public async Task Create_WithABlankName_ReturnsBadRequest()
    {
        using var db = TestDb.Create();

        var result = await Build(db, Guid.NewGuid()).Create(new CreatePersonRequest(
            "  ", null, null, null, null, null, null, null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- Update ----

    [Fact]
    public async Task Update_SetsTheContactFields()
    {
        using var db = TestDb.Create();
        var person = await SeedPerson(db, "Ada");

        var result = await Build(db, Guid.NewGuid()).Update(person.Id, new UpdatePersonRequest(
            null, "Engineer", "Analytical Engines", null, "+44", true, null));

        Assert.IsType<NoContentResult>(result);
        var reloaded = await db.People.SingleAsync(p => p.Id == person.Id);
        Assert.Equal("Engineer", reloaded.Title);
        Assert.Equal("Analytical Engines", reloaded.CompanyName);
        Assert.Equal("+44", reloaded.Phone);
        Assert.True(reloaded.IsInternal);
        Assert.Equal("Ada", reloaded.Name); // a null field means "not supplied", not "clear it"
    }

    /// <summary>A linked person's name and email follow their account, so editing them here would be
    /// silently undone by the next sync.</summary>
    [Fact]
    public async Task Update_NameOrEmailOnALinkedPerson_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var accountId = Guid.NewGuid();
        Users.Ensure(db, accountId);
        var person = await SeedPerson(db, "Ada", linkedUserId: accountId);
        var controller = Build(db, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(person.Id, new UpdatePersonRequest("New", null, null, null, null, null, null)));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(person.Id, new UpdatePersonRequest(null, null, null, "new@x.test", null, null, null)));
    }

    [Fact]
    public async Task Update_SomeoneElsesPerson_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        var person = await SeedPerson(db, "Ada");

        var result = await Build(db, Guid.NewGuid(), PlatformPermission.None)
            .Update(person.Id, new UpdatePersonRequest(null, "Engineer", null, null, null, null, null));

        Assert.IsType<ForbidResult>(result);
    }

    /// <summary>Your own entry is yours to correct without an administrator.</summary>
    [Fact]
    public async Task Update_YourOwnPerson_WithoutManagePeople_Succeeds()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var person = await SeedPerson(db, "Ada", linkedUserId: me);

        var result = await Build(db, me, PlatformPermission.None)
            .Update(person.Id, new UpdatePersonRequest(null, "Engineer", null, null, null, null, null));

        Assert.IsType<NoContentResult>(result);
    }

    // ---- The capability flag the UI renders from ----

    /// <summary>The UI must render the opt-out and erase controls from this flag rather than recomputing
    /// `ManagePeople || IsSelf` client-side, or the two drift the first time either is edited.</summary>
    [Fact]
    public async Task PersonDto_CanManageBiometrics_ReflectsTheGate()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        await SeedPerson(db, "Ada Lovelace", linkedUserId: me);
        await SeedPerson(db, "Grace Hopper");

        var mine = Assert.Single(Ok(await Build(db, me, PlatformPermission.None).Search("Lovelace")));
        Assert.True(mine.CanManageBiometrics);
        Assert.True(mine.IsSelf);

        var theirs = Assert.Single(Ok(await Build(db, me, PlatformPermission.None).Search("Hopper")));
        Assert.False(theirs.CanManageBiometrics);
        Assert.False(theirs.IsSelf);

        var asManager = Assert.Single(Ok(await Build(db, me).Search("Hopper")));
        Assert.True(asManager.CanManageBiometrics);
    }

    // ---- Voiceprint ----

    [Fact]
    public async Task EnrolVoiceprint_OnAnOptedOutPerson_ReturnsConflict()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
        });
        var person = await SeedPerson(db, "Opted out");
        person.VoiceprintOptOut = true;
        await db.SaveChangesAsync();

        var result = await Build(db, userId)
            .EnrolVoiceprint(person.Id, new EnrolVoiceprintRequest(rec.Id, "SPEAKER_00"));

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task EnrolVoiceprint_WhenTheSpeakerHasNoEmbedding_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
        });
        var person = await SeedPerson(db, "Ada");

        var result = await Build(db, userId)
            .EnrolVoiceprint(person.Id, new EnrolVoiceprintRequest(rec.Id, "SPEAKER_00"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteVoiceprint_KeepsThePerson_RevertsAutoLabels_KeepsManualNames()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var person = await SeedPerson(db, "Ada", sampleCount: 2);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        var auto = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada",
            PersonId = person.Id, IdentifiedAuto = true,
        };
        var manual = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Ada",
            PersonId = person.Id, IdentifiedAuto = false,
        };
        db.Speakers.AddRange(auto, manual);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Build(db, Guid.NewGuid()).DeleteVoiceprint(person.Id));

        Assert.NotNull(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
        Assert.Equal("SPEAKER_00", (await db.Speakers.SingleAsync(s => s.Id == auto.Id)).DisplayName);
        Assert.Equal("Ada", (await db.Speakers.SingleAsync(s => s.Id == manual.Id)).DisplayName);
    }

    // ---- Duplicates ----

    [Fact]
    public async Task Duplicates_GroupsByEmailAndName()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada Lovelace", "ada@example.com");
        await SeedPerson(db, "A. Lovelace", "ADA@example.com");
        await SeedPerson(db, "Grace Hopper");
        await SeedPerson(db, "grace  hopper ");

        var result = await Build(db, Guid.NewGuid()).Duplicates();
        var groups = Assert.IsAssignableFrom<IReadOnlyList<PersonDuplicateGroupDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Reason == "email");
        Assert.Contains(groups, g => g.Reason == "name");
    }

    [Fact]
    public async Task Duplicates_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();

        Assert.IsType<ForbidResult>((await Build(db, Guid.NewGuid(), PlatformPermission.None).Duplicates()).Result);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        var person = await SeedPerson(db, "Ada");

        Assert.IsType<ForbidResult>(await Build(db, Guid.NewGuid(), PlatformPermission.None).Delete(person.Id));
        Assert.NotNull(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
    }

    [Fact]
    public async Task Delete_WithManagePeople_RemovesThePerson()
    {
        using var db = TestDb.Create();
        var person = await SeedPerson(db, "Ada");

        Assert.IsType<NoContentResult>(await Build(db, Guid.NewGuid()).Delete(person.Id));
        Assert.Null(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
    }

    [Fact]
    public async Task DeleteAllVoiceprints_RequiresManagePlatform()
    {
        using var db = TestDb.Create();
        await SeedPerson(db, "Ada", sampleCount: 1);

        Assert.IsType<ForbidResult>(
            await Build(db, Guid.NewGuid(), PlatformPermission.ManagePeople).DeleteAllVoiceprints());
        Assert.IsType<NoContentResult>(
            await Build(db, Guid.NewGuid(), PlatformPermission.ManagePlatform).DeleteAllVoiceprints());
    }

    // ---- Merge ----
    //
    // Merge deletes the source row outright, so anything the source alone was carrying is destroyed unless it
    // is explicitly moved first. The account link is the dangerous one: losing it detaches a real user from
    // the directory, and because the self-exception on the biometric gate resolves through LinkedUserId, that
    // user silently loses the ability to opt themselves out of voice-printing - a GDPR right, failing closed
    // and without a message.

    [Fact]
    public async Task Merge_MovesSamplesAndSpeakersToTheTarget_AndDeletesTheSource()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var target = await SeedPerson(db, "Ada Lovelace");
        var source = await SeedPerson(db, "Ada L");
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        var speakerId = Guid.NewGuid();
        db.Speakers.Add(new Speaker
        {
            Id = speakerId, RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Ada L", PersonId = source.Id,
        });
        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = source.Id, RecordingId = rec.Id, SpeakerId = speakerId,
        });
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(
            await Build(db, userId).Merge(target.Id, new MergePeopleRequest(source.Id)));

        Assert.Null(await db.People.SingleOrDefaultAsync(p => p.Id == source.Id));
        Assert.Equal(target.Id, (await db.VoiceSamples.SingleAsync()).PersonId);
        var speaker = await db.Speakers.SingleAsync();
        Assert.Equal(target.Id, speaker.PersonId);
        Assert.Equal("Ada Lovelace", speaker.DisplayName);
    }

    [Fact]
    public async Task Merge_CarriesTheAccountLinkFromTheSource()
    {
        using var db = TestDb.Create();
        var actor = Guid.NewGuid();
        var account = Guid.NewGuid();
        Users.Ensure(db, account);
        var target = await SeedPerson(db, "Ada Lovelace");
        var source = await SeedPerson(db, "Ada L", linkedUserId: account);

        Assert.IsType<NoContentResult>(
            await Build(db, actor).Merge(target.Id, new MergePeopleRequest(source.Id)));

        Assert.Equal(account, (await db.People.SingleAsync(p => p.Id == target.Id)).LinkedUserId);
    }

    [Fact]
    public async Task Merge_KeepsTheTargetsOwnAccountLink()
    {
        using var db = TestDb.Create();
        var actor = Guid.NewGuid();
        var account = Guid.NewGuid();
        Users.Ensure(db, account);
        var target = await SeedPerson(db, "Ada Lovelace", linkedUserId: account);
        var source = await SeedPerson(db, "Ada L");

        Assert.IsType<NoContentResult>(
            await Build(db, actor).Merge(target.Id, new MergePeopleRequest(source.Id)));

        Assert.Equal(account, (await db.People.SingleAsync(p => p.Id == target.Id)).LinkedUserId);
    }

    /// Two accounts are two humans, whatever their names look like. There is no correct answer to which link
    /// survives, so refuse rather than pick one - and say which two, because the duplicates banner will keep
    /// offering the pair otherwise.
    [Fact]
    public async Task Merge_WhenBothArePeopleWithAccounts_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var actor = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Users.Ensure(db, first);
        Users.Ensure(db, second);
        var target = await SeedPerson(db, "Ada Lovelace", linkedUserId: first);
        var source = await SeedPerson(db, "Ada L", linkedUserId: second);

        Assert.IsType<BadRequestObjectResult>(
            await Build(db, actor).Merge(target.Id, new MergePeopleRequest(source.Id)));

        Assert.Equal(2, await db.People.CountAsync());
        Assert.Equal(second, (await db.People.SingleAsync(p => p.Id == source.Id)).LinkedUserId);
    }

    /// The target is the record being kept, so its own details win. The source's are salvage: they fill the
    /// gaps rather than overwrite, because a merge should never lose a contact detail that existed a moment
    /// ago.
    [Fact]
    public async Task Merge_FillsOnlyTheContactFieldsTheTargetLacks()
    {
        using var db = TestDb.Create();
        var actor = Guid.NewGuid();
        var target = await SeedPerson(db, "Ada Lovelace", company: "Analytical Engines");
        var source = await SeedPerson(db, "Ada L", email: "ada@example.com", company: "Somewhere Else");
        source.Title = "Mathematician";
        source.Phone = "0100";
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(
            await Build(db, actor).Merge(target.Id, new MergePeopleRequest(source.Id)));

        var kept = await db.People.SingleAsync(p => p.Id == target.Id);
        Assert.Equal("Analytical Engines", kept.CompanyName);
        Assert.Equal("ada@example.com", kept.Email);
        Assert.Equal("Mathematician", kept.Title);
        Assert.Equal("0100", kept.Phone);
    }

    // ---- Choosing which audio trains a voice sample ----

    /// <summary>A person with one voice sample from one recording. Embedding is left null: the vector
    /// column is Ignore'd under the in-memory provider, and none of these cases read it.</summary>
    private static async Task<(Person person, VoiceSample sample, Recording recording)> SeedSample(
        DiarizDbContext db, Guid ownerId, bool optedOut = false, int? usedMs = 30000,
        DateTimeOffset? audioDeletedAt = null, Guid? linkedUserId = null)
    {
        var person = new Person
        {
            Id = Guid.NewGuid(), Name = "Alice", SampleCount = 1,
            VoiceprintOptOut = optedOut, LinkedUserId = linkedUserId,
        };
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = "Standup", BlobKey = "audio/r1.webm",
            AudioDeletedAt = audioDeletedAt,
        };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice",
            PersonId = person.Id,
        };
        var sample = new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
            UsedMs = usedMs,
        };
        db.AddRange(person, rec, speaker, sample);
        await db.SaveChangesAsync();
        return (person, sample, rec);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_StoresThemAndQueuesAJobCarryingTheBlobKey()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId);
        var controller = BuildWithQueue(db, userId, queue);

        var result = await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)]));

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal([new VoiceprintSpan(1000, 3000)],
            VoiceprintSpans.Parse(db.VoiceSamples.Single().SpansJson));
        var job = Assert.Single(queue.VoiceprintJobs);
        Assert.Equal("audio/r1.webm", job.BlobKey);
        Assert.Equal(sample.Id, job.VoiceSampleId);
        Assert.Equal([new VoiceprintSpan(1000, 3000)], job.Spans);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_ClearsUsedMsSoTheRowReadsAsPending()
    {
        // Pending has to be server-derived, or a reload during a recompute shows the old "using 0:30" as
        // though the new selection had already been applied.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (person, sample, _) = await SeedSample(db, userId, usedMs: 30000);
        var controller = BuildWithQueue(db, userId, new FakeJobQueue());

        await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)]));

        Assert.Null(db.VoiceSamples.Single().UsedMs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_MergesAdjacentSelections()
    {
        // The client sends one span per ticked segment. Without collapsing them a 40-minute selection
        // becomes hundreds of one-line spans in the job payload.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId);
        var controller = BuildWithQueue(db, userId, queue);

        await controller.SetVoiceSampleSpans(person.Id, sample.Id, new SetVoiceSampleSpansRequest(
            [new VoiceprintSpan(1000, 2000), new VoiceprintSpan(2000, 3000)]));

        Assert.Equal([new VoiceprintSpan(1000, 3000)], Assert.Single(queue.VoiceprintJobs).Spans);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_WithNoSpans_RevertsToTheWholeSpeaker()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId);
        var controller = BuildWithQueue(db, userId, queue);

        await controller.SetVoiceSampleSpans(person.Id, sample.Id, new SetVoiceSampleSpansRequest([]));

        Assert.Null(db.VoiceSamples.Single().SpansJson);
        // Still recomputes: going back to the whole speaker is itself a change to what trains it.
        Assert.Single(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_ForAnOptedOutPerson_IsConflictAndQueuesNothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId, optedOut: true);
        var controller = BuildWithQueue(db, userId, queue);

        Assert.IsType<ConflictObjectResult>(await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
        Assert.Empty(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_WhenTheAudioIsGone_IsConflictAndQueuesNothing()
    {
        // Audio can be deleted while the sample survives. Queueing a job the worker can only fail is worse
        // than saying so up front.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId, audioDeletedAt: DateTimeOffset.UtcNow);
        var controller = BuildWithQueue(db, userId, queue);

        Assert.IsType<ConflictObjectResult>(await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
        Assert.Empty(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_WithoutBiometricPermission_IsForbidden()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId);
        var controller = BuildWithQueue(db, userId, queue, PlatformPermission.None);

        Assert.IsType<ForbidResult>(await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
        Assert.Empty(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_ForYourOwnPerson_IsAllowedWithoutManagePeople()
    {
        // The self exception: withdrawing or reshaping consent for your own biometric is the data
        // subject's right, and routing it through an administrator would be a weak posture.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId, linkedUserId: userId);
        var controller = BuildWithQueue(db, userId, queue, PlatformPermission.None);

        Assert.IsType<AcceptedResult>(await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
        Assert.Single(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task SetVoiceSampleSpans_ForASampleOfAnotherPerson_IsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (_, sample, _) = await SeedSample(db, userId);
        var other = await SeedPerson(db, "Bob");
        var controller = BuildWithQueue(db, userId, queue);

        Assert.IsType<NotFoundResult>(await controller.SetVoiceSampleSpans(other.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
        Assert.Empty(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task Get_ReportsSelectedAndUsedAndPendingPerSample()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var (person, sample, _) = await SeedSample(db, userId);
        var controller = BuildWithQueue(db, userId, queue);
        await controller.SetVoiceSampleSpans(person.Id, sample.Id,
            new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)]));

        var detail = (await controller.Get(person.Id)).Value!;

        var dto = Assert.Single(detail.Samples);
        Assert.Equal(2000, dto.SelectedMs);
        Assert.Null(dto.UsedMs);
        Assert.True(dto.Pending);
    }

    [Fact]
    public async Task Get_ReportsASampleAsStaleWhenItsSpeakersAudioWasReattributed()
    {
        // Derived by joining to the speaker rather than stored twice: two columns saying the same thing
        // would eventually disagree.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (person, _, rec) = await SeedSample(db, userId);
        var speaker = db.Speakers.Single(s => s.RecordingId == rec.Id);
        speaker.EmbeddingStale = true;
        await db.SaveChangesAsync();
        var controller = BuildWithQueue(db, userId, new FakeJobQueue());

        var detail = (await controller.Get(person.Id)).Value!;

        Assert.True(Assert.Single(detail.Samples).Stale);
    }
}
