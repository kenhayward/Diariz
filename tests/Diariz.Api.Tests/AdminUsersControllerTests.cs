using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class AdminUsersControllerTests
{
    private static AdminUsersController Build(
        IdentityTestHost host, Guid adminId, FakeEmailSender? email = null, FakeAudioStorage? storage = null) =>
        new(host.Users, email ?? new FakeEmailSender(), host.Db, new PlatformSettingsService(host.Db),
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }),
            new UserPermissions(host.Db), storage ?? new FakeAudioStorage(), NullLogger<AdminUsersController>.Instance)
        {
            ControllerContext = Http.Context(adminId),
        };

    /// <summary>Seeds a user whose authority comes from group membership. The role is still assigned, because
    /// the seeder migrates roles to groups on boot and some tests assert on it, but nothing reads it for
    /// authorization any more - the matching permission flags are what count.</summary>
    private static async Task<ApplicationUser> Seed(
        IdentityTestHost host, string email, string role, UserStatus status = UserStatus.Active)
    {
        var user = new ApplicationUser { UserName = email, Email = email, Status = status, IsEnabled = true };
        await host.Users.CreateAsync(user);
        await host.Users.AddToRoleAsync(user, role);
        Perms.Grant(host.Db, user.Id, role switch
        {
            Roles.PlatformAdministrator => Perms.PlatformAdministrator,
            Roles.Administrator => Perms.Administrator,
            _ => PlatformPermission.None,
        });
        return user;
    }

    private static int? StatusOf(IActionResult r) => (r as ObjectResult)?.StatusCode;

    // ---- List ----

    [Fact]
    public async Task List_MapsAccountTypeFromRoles()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        await Seed(host, "plat@x.test", Roles.PlatformAdministrator);
        await Seed(host, "std@x.test", Roles.Standard);

        var list = await Build(host, admin.Id).List();

        Assert.Equal(Roles.PlatformAdministrator, list.Single(u => u.Email == "plat@x.test").AccountType);
        Assert.Equal(Roles.Standard, list.Single(u => u.Email == "std@x.test").AccountType);
    }

    // ---- Add user ----

    [Fact]
    public async Task Add_CreatesInvitedStandardUser_AndReturnsLink_WhenEmailUnconfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var email = new FakeEmailSender { Sent = false };

        var res = await Build(host, admin.Id, email).Add(new AddUserRequest("new@x.test"));

        var grant = Assert.IsType<GrantResultDto>(res.Value);
        Assert.False(grant.Emailed);
        Assert.Contains("/setup?email=", grant.SetupUrl);
        var created = await host.Users.FindByEmailAsync("new@x.test");
        Assert.NotNull(created);
        Assert.Equal(UserStatus.Invited, created!.Status);
        Assert.False(await host.Users.HasPasswordAsync(created)); // awaits setup
        Assert.Contains(Roles.Standard, await host.Users.GetRolesAsync(created));
    }

    [Fact]
    public async Task Add_SetsFullName_AndStarterQuota()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        await Build(host, admin.Id).Add(new AddUserRequest("new@x.test", "New Person"));

        var created = await host.Users.FindByEmailAsync("new@x.test");
        Assert.Equal("New Person", created!.FullName);
        Assert.Equal(PlatformSettings.DefaultStarterQuotaBytes, created.QuotaBytes);
    }

    // ---- Quota ----

    [Fact]
    public async Task SetQuota_RaisesQuota_WithinMax()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "std@x.test", Roles.Standard);

        var result = await Build(host, admin.Id).SetQuota(target.Id, new SetQuotaRequest(10L * 1024 * 1024 * 1024));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(10L * 1024 * 1024 * 1024, (await host.Users.FindByIdAsync(target.Id.ToString()))!.QuotaBytes);
    }

    [Fact]
    public async Task SetQuota_AboveMax_ReturnsBadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "std@x.test", Roles.Standard);

        var result = await Build(host, admin.Id)
            .SetQuota(target.Id, new SetQuotaRequest(PlatformSettings.DefaultMaxQuotaBytes + 1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetQuota_UnknownUser_ReturnsNotFound()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.IsType<NotFoundResult>(await Build(host, admin.Id).SetQuota(Guid.NewGuid(), new SetQuotaRequest(1024)));
    }

    [Fact]
    public async Task Add_EmailsWhenConfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var email = new FakeEmailSender { Sent = true };

        var grant = (await Build(host, admin.Id, email).Add(new AddUserRequest("new@x.test"))).Value!;

        Assert.True(grant.Emailed);
        Assert.Null(grant.SetupUrl);
        Assert.Single(email.Messages);
    }

    [Fact]
    public async Task Add_DuplicateEmail_BadRequest_AndNoSecondUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        await Seed(host, "dupe@x.test", Roles.Standard);

        Assert.IsType<BadRequestObjectResult>((await Build(host, admin.Id).Add(new AddUserRequest("dupe@x.test"))).Result);
        Assert.Equal(1, await host.Users.Users.CountAsync(u => u.Email == "dupe@x.test"));
    }

    [Fact]
    public async Task Add_BlankEmail_BadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.IsType<BadRequestObjectResult>((await Build(host, admin.Id).Add(new AddUserRequest("  "))).Result);
    }

    // ---- Grant ----

    [Fact]
    public async Task Grant_Requested_InvitesAndReturnsLink_WhenEmailUnconfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);
        var email = new FakeEmailSender { Sent = false };

        var res = await Build(host, admin.Id, email).Grant(pending.Id);

        var grant = Assert.IsType<GrantResultDto>(res.Value);
        Assert.False(grant.Emailed);
        Assert.Contains("/setup?email=", grant.SetupUrl);
        Assert.Equal(UserStatus.Invited, (await host.Users.FindByIdAsync(pending.Id.ToString()))!.Status);
    }

    [Fact]
    public async Task Grant_EmailsAndHidesLink_WhenConfigured()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);
        var email = new FakeEmailSender { Sent = true };

        var grant = (await Build(host, admin.Id, email).Grant(pending.Id)).Value!;

        Assert.True(grant.Emailed);
        Assert.Null(grant.SetupUrl);
        Assert.Single(email.Messages);
    }

    [Fact]
    public async Task Grant_NonRequested_BadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var active = await Seed(host, "act@x.test", Roles.Standard);

        Assert.IsType<BadRequestObjectResult>((await Build(host, admin.Id).Grant(active.Id)).Result);
    }

    [Fact]
    public async Task Deny_DeletesRequestedUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var pending = await Seed(host, "want@x.test", Roles.Standard, UserStatus.Requested);

        Assert.IsType<NoContentResult>(await Build(host, admin.Id).Deny(pending.Id));
        Assert.Null(await host.Users.FindByIdAsync(pending.Id.ToString()));
    }

    // ---- Role ----

    [Fact]
    public async Task SetRole_PromotesAndDemotes()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        await Seeder.SeedGroupsAsync(host.Db); // SetRole moves the user in and out of the Administrators group
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);
        var perms = new UserPermissions(host.Db);

        await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.Administrator));
        Assert.True((await perms.ForAsync(target.Id)).HasFlag(PlatformPermission.ManageUsers));
        // Promotion never confers ManagePlatform: that is the Platform Administrators group alone.
        Assert.False((await perms.ForAsync(target.Id)).HasFlag(PlatformPermission.ManagePlatform));

        await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.Standard));
        Assert.Equal(PlatformPermission.None, await perms.ForAsync(target.Id));
    }

    [Fact]
    public async Task SetRole_PlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetRole(plat.Id, new SetRoleRequest(Roles.Standard))));
        Assert.Contains(Roles.PlatformAdministrator, await host.Users.GetRolesAsync(plat)); // unchanged
    }

    [Fact]
    public async Task SetRole_Self_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetRole(admin.Id, new SetRoleRequest(Roles.Standard))));
    }

    [Fact]
    public async Task SetRole_InvalidRole_BadRequest()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        Assert.IsType<BadRequestObjectResult>(
            await Build(host, admin.Id).SetRole(target.Id, new SetRoleRequest(Roles.PlatformAdministrator)));
    }

    // ---- Enable / disable ----

    [Fact]
    public async Task SetEnabled_DisablesAndEnables()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        await Build(host, admin.Id).SetEnabled(target.Id, new SetEnabledRequest(false));
        Assert.False((await host.Users.FindByIdAsync(target.Id.ToString()))!.IsEnabled);

        await Build(host, admin.Id).SetEnabled(target.Id, new SetEnabledRequest(true));
        Assert.True((await host.Users.FindByIdAsync(target.Id.ToString()))!.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_DisablePlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetEnabled(plat.Id, new SetEnabledRequest(false))));
    }

    [Fact]
    public async Task SetEnabled_DisableSelf_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).SetEnabled(admin.Id, new SetEnabledRequest(false))));
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_RemovesUser()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);

        Assert.IsType<NoContentResult>(await Build(host, admin.Id).Delete(target.Id));
        Assert.Null(await host.Users.FindByIdAsync(target.Id.ToString()));
    }

    [Fact]
    public async Task Delete_PlatformAdmin_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var plat = await Seed(host, "plat@x.test", Roles.PlatformAdministrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).Delete(plat.Id)));
        Assert.NotNull(await host.Users.FindByIdAsync(plat.Id.ToString()));
    }

    [Fact]
    public async Task Delete_Self_Forbidden()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);

        Assert.Equal(403, StatusOf(await Build(host, admin.Id).Delete(admin.Id)));
    }

    /// <summary>Phase 4: deleting a user sweeps their RoomMember rows (which carry no FK, so the database can't
    /// cascade them) while leaving other members' rows in the same room untouched.</summary>
    [Fact]
    public async Task Delete_SweepsTheUsersRoomMemberships()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);
        var other = await Seed(host, "o@x.test", Roles.Standard);

        var roomId = Guid.NewGuid();
        host.Db.Rooms.Add(new Room { Id = roomId, Name = "Eng", Kind = RoomKind.Shared });
        host.Db.RoomMembers.Add(new RoomMember
        {
            RoomId = roomId, PrincipalType = RoomPrincipalType.User, PrincipalId = target.Id,
            Permissions = RoomPermission.CreateRecording,
        });
        host.Db.RoomMembers.Add(new RoomMember
        {
            RoomId = roomId, PrincipalType = RoomPrincipalType.User, PrincipalId = other.Id,
            Permissions = RoomPermission.CreateRecording,
        });
        await host.Db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Build(host, admin.Id).Delete(target.Id));

        Assert.False(await host.Db.RoomMembers.AnyAsync(m => m.PrincipalId == target.Id));
        Assert.True(await host.Db.RoomMembers.AnyAsync(m => m.PrincipalId == other.Id)); // untouched
    }

    /// <summary>Pins the collection/re-point logic cheaply with a FakeAudioStorage: every blob the deleted
    /// user owned (audio, recording attachment, both screenshot keys, own-folder section attachment) is
    /// passed to DeleteAsync, while the survivor's blob (uploaded into someone else's folder) is not - it is
    /// re-pointed instead. This does NOT stand in for the cascade/survival proof, which needs real Postgres +
    /// MinIO (see UserDeletionBlobCleanupIntegrationTests) - the in-memory provider enforces no FK cascade.</summary>
    [Fact]
    public async Task Delete_CollectsOwnedBlobKeys_ButNotTheRepointedSurvivors()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();
        var admin = await Seed(host, "admin@x.test", Roles.Administrator);
        var target = await Seed(host, "t@x.test", Roles.Standard);
        var folderOwner = await Seed(host, "owner@x.test", Roles.Standard);

        var storage = new FakeAudioStorage();
        var audioKey = $"{target.Id}/audio.webm";
        var attachmentKey = $"{target.Id}/attachments/doc.pdf";
        var screenshotKey = $"{target.Id}/screenshots/full.png";
        var thumbKey = $"{target.Id}/screenshots/thumb.jpg";
        var ownFolderKey = $"{target.Id}/section-attachments/own.md";
        var foreignFolderKey = $"{target.Id}/section-attachments/foreign.md";
        foreach (var key in new[] { audioKey, attachmentKey, screenshotKey, thumbKey, ownFolderKey, foreignFolderKey })
            storage.Objects[key] = [1, 2, 3];

        var recordingId = Guid.NewGuid();
        host.Db.Recordings.Add(new Recording
        {
            Id = recordingId, UserId = target.Id, Title = "R", BlobKey = audioKey, SizeBytes = 3,
        });
        host.Db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, Kind = AttachmentKind.File, Name = "doc.pdf",
            BlobKey = attachmentKey, SizeBytes = 3,
        });
        host.Db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(), UserId = target.Id, RecordingId = recordingId, CapturedAtMs = 0,
            BlobKey = screenshotKey, ThumbBlobKey = thumbKey,
        });
        var ownSectionId = Guid.NewGuid();
        host.Db.Sections.Add(new Section { Id = ownSectionId, UserId = target.Id, RoomId = Guid.NewGuid(), Name = "Own" });
        host.Db.SectionAttachments.Add(new SectionAttachment
        {
            Id = Guid.NewGuid(), SectionId = ownSectionId, Kind = AttachmentKind.File, Name = "own.md",
            BlobKey = ownFolderKey, SizeBytes = 3, UploadedByUserId = target.Id,
        });
        var foreignSectionId = Guid.NewGuid();
        var foreignAttachmentId = Guid.NewGuid();
        host.Db.Sections.Add(new Section { Id = foreignSectionId, UserId = folderOwner.Id, RoomId = Guid.NewGuid(), Name = "Foreign" });
        host.Db.SectionAttachments.Add(new SectionAttachment
        {
            Id = foreignAttachmentId, SectionId = foreignSectionId, Kind = AttachmentKind.File, Name = "foreign.md",
            BlobKey = foreignFolderKey, SizeBytes = 3, UploadedByUserId = target.Id,
        });
        await host.Db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Build(host, admin.Id, storage: storage).Delete(target.Id));

        // Every blob the user owned outright was deleted...
        Assert.DoesNotContain(audioKey, storage.Objects.Keys);
        Assert.DoesNotContain(attachmentKey, storage.Objects.Keys);
        Assert.DoesNotContain(screenshotKey, storage.Objects.Keys);
        Assert.DoesNotContain(thumbKey, storage.Objects.Keys);
        Assert.DoesNotContain(ownFolderKey, storage.Objects.Keys);
        // ...but the survivor's blob was NOT - it was re-pointed, not deleted.
        Assert.Contains(foreignFolderKey, storage.Objects.Keys);

        var survivor = await host.Db.SectionAttachments.SingleAsync(a => a.Id == foreignAttachmentId);
        Assert.Equal(folderOwner.Id, survivor.UploadedByUserId);
        Assert.Equal(foreignFolderKey, survivor.BlobKey); // never reconstructed
    }
}
