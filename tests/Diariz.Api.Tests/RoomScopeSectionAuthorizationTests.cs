using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The shared section-authorization gates on <see cref="IRoomScope"/> - <c>ViewableSectionAsync</c>
/// (member of the section's own room) and <c>ManageableSectionAsync</c>/<c>AuthorizeManageContentsAsync</c>
/// (additionally <see cref="RoomPermission.ManageContents"/>). These replace three near-identical private
/// copies that used to live in SectionPageController, SectionFormulaResultsController and
/// SectionAttachmentsController, plus the room-id-based shape in SectionsController.AuthorizeManage.
///
/// The NotFound-before-Forbidden ordering is a security property: a non-member must get the exact same result
/// (NotFound) whether the section/room exists or not, so a stranger cannot distinguish "no such folder" from
/// "not permitted" by getting a Forbidden instead. That property is asserted explicitly below.</summary>
public class RoomScopeSectionAuthorizationTests
{
    private static async Task<(Guid UserId, Guid RoomId, Section Section)> SeedPersonalSection(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "F" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return (userId, roomId, section);
    }

    private static async Task<Room> SeedSharedRoom(DiarizDbContext db)
    {
        var room = new Room { Id = Guid.NewGuid(), Name = "Eng", Kind = RoomKind.Shared };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private static async Task<Section> SeedSectionIn(DiarizDbContext db, Guid roomId)
    {
        var section = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "F" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return section;
    }

    private static async Task SeedMember(DiarizDbContext db, Guid roomId, Guid userId, RoomPermission perms)
    {
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = roomId, PrincipalType = RoomPrincipalType.User, PrincipalId = userId, Permissions = perms,
        });
        await db.SaveChangesAsync();
    }

    // ---- ViewableSectionAsync ----

    [Fact]
    public async Task Viewable_ReturnsNull_ForAMissingSection()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);

        Assert.Null(await new RoomScope(db).ViewableSectionAsync(caller, Guid.NewGuid()));
    }

    [Fact]
    public async Task Viewable_ReturnsNull_ForANonMember()
    {
        using var db = TestDb.Create();
        var (_, _, section) = await SeedPersonalSection(db);
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);

        Assert.Null(await new RoomScope(db).ViewableSectionAsync(stranger, section.Id));
    }

    [Fact]
    public async Task Viewable_ReturnsTheSection_ForThePersonalRoomOwner()
    {
        using var db = TestDb.Create();
        var (owner, _, section) = await SeedPersonalSection(db);

        var result = await new RoomScope(db).ViewableSectionAsync(owner, section.Id);

        Assert.NotNull(result);
        Assert.Equal(section.Id, result!.Id);
    }

    [Fact]
    public async Task Viewable_ReturnsTheSection_ForASharedRoomMember_RegardlessOfPermissions()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        Users.Ensure(db, member);
        var room = await SeedSharedRoom(db);
        await SeedMember(db, room.Id, member, RoomPermission.None);
        var section = await SeedSectionIn(db, room.Id);

        var result = await new RoomScope(db).ViewableSectionAsync(member, section.Id);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Viewable_WithArtifacts_IncludesSummaryAndMinutes()
    {
        using var db = TestDb.Create();
        var (owner, _, section) = await SeedPersonalSection(db);
        db.SectionSummaries.Add(new SectionSummary { Id = Guid.NewGuid(), SectionId = section.Id, Text = "S" });
        db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = section.Id, Text = "M" });
        await db.SaveChangesAsync();

        var result = await new RoomScope(db).ViewableSectionAsync(owner, section.Id, withArtifacts: true);

        Assert.NotNull(result!.Summary);
        Assert.NotNull(result.Minutes);
    }

    // ---- ManageableSectionAsync: the full matrix ----

    [Fact]
    public async Task Manageable_NonMember_GetsNotFound_NotForbidden()
    {
        using var db = TestDb.Create();
        var (_, _, section) = await SeedPersonalSection(db);
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(stranger, section.Id);

        Assert.Null(result);
        Assert.Equal(RoomAccessError.NotFound, error);
    }

    [Fact]
    public async Task Manageable_MissingSection_GetsNotFound()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(caller, Guid.NewGuid());

        Assert.Null(result);
        Assert.Equal(RoomAccessError.NotFound, error);
    }

    [Fact]
    public async Task Manageable_MemberWithoutManageContents_GetsForbidden_NotNotFound()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        Users.Ensure(db, member);
        var room = await SeedSharedRoom(db);
        await SeedMember(db, room.Id, member, RoomPermission.CreateRecording); // some permission, not ManageContents
        var section = await SeedSectionIn(db, room.Id);

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(member, section.Id);

        Assert.Null(result);
        Assert.Equal(RoomAccessError.Forbidden, error);
    }

    [Fact]
    public async Task Manageable_MemberWithManageContents_Succeeds()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        Users.Ensure(db, member);
        var room = await SeedSharedRoom(db);
        await SeedMember(db, room.Id, member, RoomPermission.ManageContents);
        var section = await SeedSectionIn(db, room.Id);

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(member, section.Id);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(section.Id, result!.Id);
    }

    [Fact]
    public async Task Manageable_PersonalRoomOwner_Succeeds()
    {
        using var db = TestDb.Create();
        var (owner, _, section) = await SeedPersonalSection(db);

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(owner, section.Id);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Manageable_WithArtifacts_IncludesSummaryAndMinutes()
    {
        using var db = TestDb.Create();
        var (owner, _, section) = await SeedPersonalSection(db);
        db.SectionSummaries.Add(new SectionSummary { Id = Guid.NewGuid(), SectionId = section.Id, Text = "S" });
        db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = section.Id, Text = "M" });
        await db.SaveChangesAsync();

        var (result, error) = await new RoomScope(db).ManageableSectionAsync(owner, section.Id, withArtifacts: true);

        Assert.Null(error);
        Assert.NotNull(result!.Summary);
        Assert.NotNull(result.Minutes);
    }

    // ---- AuthorizeManageContentsAsync: the room-id-based twin used by SectionsController ----

    [Fact]
    public async Task AuthorizeManageContents_NonMember_GetsNotFound()
    {
        using var db = TestDb.Create();
        var room = await SeedSharedRoom(db);
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);

        Assert.Equal(RoomAccessError.NotFound, await new RoomScope(db).AuthorizeManageContentsAsync(stranger, room.Id));
    }

    [Fact]
    public async Task AuthorizeManageContents_MemberWithoutPermission_GetsForbidden()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        Users.Ensure(db, member);
        var room = await SeedSharedRoom(db);
        await SeedMember(db, room.Id, member, RoomPermission.CreateRecording);

        Assert.Equal(RoomAccessError.Forbidden, await new RoomScope(db).AuthorizeManageContentsAsync(member, room.Id));
    }

    [Fact]
    public async Task AuthorizeManageContents_MemberWithPermission_Succeeds()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        Users.Ensure(db, member);
        var room = await SeedSharedRoom(db);
        await SeedMember(db, room.Id, member, RoomPermission.ManageContents);

        Assert.Null(await new RoomScope(db).AuthorizeManageContentsAsync(member, room.Id));
    }

    [Fact]
    public async Task AuthorizeManageContents_PersonalRoomOwner_Succeeds()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        Users.Ensure(db, owner);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(owner);

        Assert.Null(await new RoomScope(db).AuthorizeManageContentsAsync(owner, roomId));
    }

    [Fact]
    public async Task AuthorizeManageContents_MissingRoom_GetsNotFound()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);

        Assert.Equal(RoomAccessError.NotFound, await new RoomScope(db).AuthorizeManageContentsAsync(caller, Guid.NewGuid()));
    }
}
