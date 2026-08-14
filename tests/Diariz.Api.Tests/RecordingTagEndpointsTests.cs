using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>The hub's manual tagging: adopt (typed or promoted), remove, dismiss. The write gate is
/// owner-or-<see cref="RoomPermission.EditOthersRecordings"/>, so the room cases below are load-bearing:
/// a member without that flag can read the recording but must not touch its tags.</summary>
public class RecordingTagEndpointsTests
{
    private static Recording AddRecording(DiarizDbContext db, Guid ownerId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = ownerId, BlobKey = "k" };
        db.Recordings.Add(rec);
        return rec;
    }

    private static RecordingTag Tag(
        DiarizDbContext db, Guid recId, string tag, double weight, RecordingTagStatus status, int ordinal = 0)
    {
        var row = new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = recId, Tag = tag, Weight = weight, Ordinal = ordinal,
            Status = status,
            AdoptedAt = status == RecordingTagStatus.Adopted ? DateTimeOffset.UtcNow : null,
        };
        db.RecordingTags.Add(row);
        return row;
    }

    [Fact]
    public async Task AddTag_TypedByHand_CreatesAnAdoptedTagWithWeightOne()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("metadata"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal("metadata", tag.Tag);
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal(1.0, tag.Weight);
        Assert.NotNull(tag.AdoptedAt);
    }

    [Fact]
    public async Task AddTag_CollapsesWhitespaceToHyphens()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("budget planning 2026"));

        Assert.Equal("budget-planning-2026", Assert.Single(db.RecordingTags.ToList()).Tag);
    }

    [Fact]
    public async Task AddTag_PromotesAMatchingSuggestion_InsteadOfInsertingASecondRow()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Data Collection", 0.8, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("Data Collection"));

        var tag = Assert.Single(db.RecordingTags.ToList());   // flipped, not duplicated
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal(1.0, tag.Weight);
        Assert.NotNull(tag.AdoptedAt);
    }

    [Fact]
    public async Task AddTag_MatchesCaseInsensitively_WhenPromoting()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Metadata", 0.8, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("metadata"));

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal("metadata", tag.Tag);   // rewritten to the normalised form of what was typed - an
                                              // adopted tag's text is always normalised, never the LLM's raw
                                              // suggestion casing, so the cloud can merge it correctly
    }

    [Fact]
    public async Task AddTag_AlreadyAdopted_IsAnIdempotentNoOp()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        var existing = Tag(db, rec.Id, "metadata", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();
        var adoptedAt = existing.AdoptedAt;

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("METADATA"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(adoptedAt, tag.AdoptedAt);   // not re-stamped
    }

    [Fact]
    public async Task AddTag_RevivesADismissedTag_WhenTheUserTypesItAnyway()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "boilerplate", 0.3, RecordingTagStatus.Dismissed);
        await db.SaveChangesAsync();

        await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("boilerplate"));

        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
    }

    [Fact]
    public async Task AddTag_WhenTheNormalizedFormIsAlreadyAdoptedUnderADifferentRow_ConvergesInsteadOfDuplicating()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        // The user already adopted this tag by hand (stored normalised, per AddTag). A later extraction
        // separately suggested the same concept in the LLM's raw, spaced spelling - a different row, because
        // the unique index only blocks exact lower-case duplicates of the raw text.
        Tag(db, rec.Id, "Data-Collection", 1.0, RecordingTagStatus.Adopted);
        Tag(db, rec.Id, "Data Collection", 0.8, RecordingTagStatus.Suggested, ordinal: 1);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest("Data Collection"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());   // the redundant suggestion is dropped, not flipped
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
        Assert.Equal("Data-Collection", tag.Tag);   // the already-adopted row is untouched
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public async Task AddTag_RejectsUnusableText(string raw)
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).AddTag(rec.Id, new SetRecordingTagRequest(raw));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.RecordingTags.ToList());
    }

    [Fact]
    public async Task RemoveTag_DeletesTheRow_SoItDoesNotReturnAsAHint()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "metadata", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).RemoveTag(rec.Id, "METADATA");

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.RecordingTags.ToList());
    }

    [Fact]
    public async Task RemoveTag_LeavesADismissalTombstoneAlone()
    {
        // A dismissal is the row that stops a word being suggested here again. Removing an adopted tag must
        // not take a tombstone with it, or the next extraction re-offers the word the user rejected - which
        // is the whole point of dismissing it.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "metadata", 1.0, RecordingTagStatus.Adopted);
        Tag(db, rec.Id, "boilerplate", 0.3, RecordingTagStatus.Dismissed, ordinal: 1);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Recordings.Build(db, me).RemoveTag(rec.Id, "boilerplate"));

        var rows = db.RecordingTags.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(RecordingTagStatus.Dismissed, rows.Single(t => t.Tag == "boilerplate").Status);
    }

    [Fact]
    public async Task RemoveTag_OfAWordThatIsOnlyASuggestion_LeavesTheSuggestion()
    {
        // Remove means "un-adopt". A suggestion was never adopted, so there is nothing to remove - deleting
        // the row would silently dismiss it without leaving the tombstone a dismissal leaves.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "suggested-word", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Recordings.Build(db, me).RemoveTag(rec.Id, "SUGGESTED word"));

        var row = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Suggested, row.Status);
    }

    [Fact]
    public async Task RemoveTag_ThatIsNotThere_IsStillNoContent()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Recordings.Build(db, me).RemoveTag(rec.Id, "absent"));
    }

    [Fact]
    public async Task DismissTag_MarksTheSuggestionDismissed_AndKeepsTheRowAsATombstone()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "Boilerplate", 0.3, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).DismissTag(rec.Id, new SetRecordingTagRequest("boilerplate"));

        Assert.IsType<NoContentResult>(result);
        var tag = Assert.Single(db.RecordingTags.ToList());
        Assert.Equal(RecordingTagStatus.Dismissed, tag.Status);
    }

    [Fact]
    public async Task DismissTag_WithNoSuchSuggestion_Is404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "mine", 1.0, RecordingTagStatus.Adopted);
        await db.SaveChangesAsync();

        var result = await Recordings.Build(db, me).DismissTag(rec.Id, new SetRecordingTagRequest("mine"));

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(RecordingTagStatus.Adopted, Assert.Single(db.RecordingTags.ToList()).Status);
    }

    /// <summary>Shares <paramref name="rec"/> into a new shared room and puts <paramref name="member"/> in it
    /// with exactly <paramref name="permissions"/>. The recording keeps its main placement in the owner's
    /// personal room, as a real shared recording does.</summary>
    private static async Task ShareWith(
        DiarizDbContext db, Recording rec, Guid owner, Guid member, RoomPermission permissions)
    {
        var scope = new Diariz.Api.Services.RoomScope(db);
        await scope.PlaceInMainRoomAsync(rec.Id, owner, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, member, permissions);
        await scope.ShareIntoRoomAsync(rec.Id, roomId, owner, sectionId: null);
    }

    [Fact]
    public async Task ARoomMemberWithEditOthersRecordings_CanAddRemoveAndDismiss()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        Users.Ensure(db, owner);
        Users.Ensure(db, member);
        var rec = AddRecording(db, owner);
        Tag(db, rec.Id, "suggested-word", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();
        await ShareWith(db, rec, owner, member,
            RoomPermission.CreateRecording | RoomPermission.EditOthersRecordings);

        var ctl = Recordings.Build(db, member);
        Assert.IsType<NoContentResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("theirs")));
        Assert.IsType<NoContentResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("suggested-word")));
        Assert.IsType<NoContentResult>(await ctl.RemoveTag(rec.Id, "theirs"));
    }

    [Fact]
    public async Task ARoomMemberWithoutEditOthersRecordings_IsForbiddenFromAllThree()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        Users.Ensure(db, owner);
        Users.Ensure(db, member);
        var rec = AddRecording(db, owner);
        Tag(db, rec.Id, "theirs", 1.0, RecordingTagStatus.Adopted);
        Tag(db, rec.Id, "suggested-word", 0.5, RecordingTagStatus.Suggested, ordinal: 1);
        await db.SaveChangesAsync();
        // Every permission EXCEPT EditOthersRecordings: this is about that one flag, not about membership.
        await ShareWith(db, rec, owner, member,
            RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
            RoomPermission.ShareOut | RoomPermission.ManageContents);

        var ctl = Recordings.Build(db, member);
        // 403, not 404: they can already see the recording, so hiding it would be a lie.
        Assert.IsType<ForbidResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("mine")));
        Assert.IsType<ForbidResult>(await ctl.RemoveTag(rec.Id, "theirs"));
        Assert.IsType<ForbidResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("suggested-word")));

        var rows = db.RecordingTags.ToList();
        Assert.Equal(2, rows.Count);   // nothing added, nothing removed
        Assert.Equal(RecordingTagStatus.Adopted, rows.Single(t => t.Tag == "theirs").Status);
        Assert.Equal(RecordingTagStatus.Suggested, rows.Single(t => t.Tag == "suggested-word").Status);
    }

    [Fact]
    public async Task TheOwner_CanAlwaysTag_EvenWithTheRecordingInNoRoomAtAll()
    {
        // Ownership is its own grant, not something inferred from a room walk - the permission check only
        // ever gates a NON-owner. A recording with no placement yet (mid-upload, or a fixture like the ones
        // above) must still be taggable by whoever recorded it.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        var rec = AddRecording(db, me);
        Tag(db, rec.Id, "suggested-word", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();
        Assert.Empty(db.RoomRecordings.ToList());

        var ctl = Recordings.Build(db, me);
        Assert.IsType<NoContentResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("mine")));
        Assert.IsType<NoContentResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("suggested-word")));
        Assert.IsType<NoContentResult>(await ctl.RemoveTag(rec.Id, "mine"));
    }

    [Fact]
    public async Task SomeoneWhoCannotSeeTheRecording_Gets404FromAllThree()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        Users.Ensure(db, owner);
        Users.Ensure(db, stranger);
        var rec = AddRecording(db, owner);
        Tag(db, rec.Id, "theirs", 0.5, RecordingTagStatus.Suggested);
        await db.SaveChangesAsync();

        var ctl = Recordings.Build(db, stranger);
        Assert.IsType<NotFoundResult>(await ctl.AddTag(rec.Id, new SetRecordingTagRequest("mine")));
        Assert.IsType<NotFoundResult>(await ctl.RemoveTag(rec.Id, "theirs"));
        Assert.IsType<NotFoundResult>(await ctl.DismissTag(rec.Id, new SetRecordingTagRequest("theirs")));
        Assert.Equal(RecordingTagStatus.Suggested, Assert.Single(db.RecordingTags.ToList()).Status);
    }

    [Fact]
    public async Task ARecordingThatDoesNotExist_Gets404FromAllThree()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Users.Ensure(db, me);
        await db.SaveChangesAsync();
        var absent = Guid.NewGuid();

        var ctl = Recordings.Build(db, me);
        Assert.IsType<NotFoundResult>(await ctl.AddTag(absent, new SetRecordingTagRequest("mine")));
        Assert.IsType<NotFoundResult>(await ctl.RemoveTag(absent, "mine"));
        Assert.IsType<NotFoundResult>(await ctl.DismissTag(absent, new SetRecordingTagRequest("mine")));
    }
}
