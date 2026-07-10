using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Thrown when a caller lacks a room permission. Translated to a 403 by the controllers that consume
/// RoomScope in later phases.</summary>
public class RoomForbiddenException(RoomPermission required)
    : Exception($"This action requires the {required} permission in this room.")
{
    public RoomPermission Required { get; } = required;
}

/// <summary>A room the caller belongs to, with the caller's effective permission grid.</summary>
public record RoomListEntry(
    Guid Id, string Name, RoomKind Kind, string? Icon, string? Color, bool IsPersonal, RoomPermission Permissions);

/// <summary>Resolves rooms and the caller's authority inside them.
///
/// Effective permissions are the union of the caller's own <see cref="RoomMember"/> row and the rows of every
/// group they belong to, plus one override: the owner of a Personal room implicitly holds everything.
///
/// Membership is the read gate. A non-member is not a member and holds nothing; callers return 404 rather than
/// 403, so a stranger cannot learn that a room exists.</summary>
public interface IRoomScope
{
    /// <summary>The caller's Personal room, created on first ask. Every user has exactly one.</summary>
    Task<Guid> PersonalRoomIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every room the caller belongs to (their Personal room, minted on demand, plus any shared room
    /// they are a member of), personal first, each with the caller's effective permission grid.</summary>
    Task<IReadOnlyList<RoomListEntry>> RoomsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Create a new Shared room. Personal rooms are minted only by <see cref="PersonalRoomIdAsync"/>.</summary>
    Task<Guid> CreateSharedRoomAsync(string name, string? description, string? icon, string? color, CancellationToken ct = default);

    /// <summary>Rename/restyle a Shared room. False if it does not exist or is a Personal room (immutable).</summary>
    Task<bool> UpdateRoomAsync(Guid roomId, string name, string? description, string? icon, string? color, CancellationToken ct = default);

    /// <summary>Delete a Shared room, unsharing (never destroying) its recordings. False for a Personal room.</summary>
    Task<bool> DeleteRoomAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>Upsert a member's permission grid on a Shared room. False for a Personal room (memberless).</summary>
    Task<bool> SetMemberAsync(Guid roomId, RoomPrincipalType type, Guid principalId, RoomPermission permissions, CancellationToken ct = default);

    /// <summary>Remove a member from a Shared room. False for a Personal room, or when no such row exists.</summary>
    Task<bool> RemoveMemberAsync(Guid roomId, RoomPrincipalType type, Guid principalId, CancellationToken ct = default);

    Task<RoomPermission> PermissionsAsync(Guid userId, Guid roomId, CancellationToken ct = default);

    Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct = default);

    /// <summary>Throws <see cref="RoomForbiddenException"/> unless the caller holds <paramref name="required"/>.</summary>
    Task RequireAsync(Guid userId, Guid roomId, RoomPermission required, CancellationToken ct = default);

    /// <summary>Every recording placed in this room: its main-room recordings plus everything shared into it.
    /// The base queryable for every room-scoped recording query - the equivalent of today's
    /// <c>.Where(r => r.UserId == UserId)</c>, one level up.</summary>
    IQueryable<Recording> RecordingsIn(Guid roomId);

    /// <summary>Create the main placement for a new recording, in its recorder's personal room.</summary>
    Task PlaceInMainRoomAsync(Guid recordingId, Guid recordedByUserId, Guid? sectionId, CancellationToken ct = default);

    /// <summary>Share a recording into a Shared room (a second, non-main placement). Idempotent - a recording
    /// already placed in the room is left as-is. False if the room is not a Shared room.</summary>
    Task<bool> ShareIntoRoomAsync(Guid recordingId, Guid roomId, Guid sharedByUserId, Guid? sectionId, CancellationToken ct = default);

    /// <summary>The folder this recording sits in, within this room. Null when ungrouped, or when it is not
    /// placed in that room at all.</summary>
    Task<Guid?> SectionIdAsync(Guid roomId, Guid recordingId, CancellationToken ct = default);

    /// <summary>Move the recording to a folder within this room (null = ungroup). False when it is not placed
    /// in that room.</summary>
    Task<bool> SetSectionAsync(Guid roomId, Guid recordingId, Guid? sectionId, CancellationToken ct = default);
}

public class RoomScope(DiarizDbContext db) : IRoomScope
{
    private const RoomPermission AllPermissions =
        RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
        RoomPermission.ShareOut | RoomPermission.ManageContents | RoomPermission.EditOthersRecordings;

    public async Task<Guid> PersonalRoomIdAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await FindPersonalRoomIdAsync(userId, ct);
        if (existing is { } id) return id;

        // Created here rather than at the four user-creation sites (AdminUsersController, AuthController,
        // GoogleSignInHandler, Seeder), so a fifth site cannot forget. The filtered unique index on
        // OwnerUserId makes the race safe: a concurrent request that wins leaves us to re-read.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException($"No such user: {userId}");

        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = Display(user),
            Kind = RoomKind.Personal,
            OwnerUserId = userId,
        };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id,
            PrincipalType = RoomPrincipalType.User,
            PrincipalId = userId,
            Permissions = AllPermissions,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return room.Id;
        }
        catch (DbUpdateException)
        {
            // Another request created it between our read and our write. Theirs is as good as ours.
            db.ChangeTracker.Clear();
            return await FindPersonalRoomIdAsync(userId, ct)
                   ?? throw new InvalidOperationException($"Personal room vanished for user {userId}");
        }
    }

    public async Task<IReadOnlyList<RoomListEntry>> RoomsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var personalId = await PersonalRoomIdAsync(userId, ct); // mint on demand so a new user still has one room

        var groupIds = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var memberRoomIds = await db.RoomMembers
            .Where(m => (m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == userId)
                        || (m.PrincipalType == RoomPrincipalType.Group && groupIds.Contains(m.PrincipalId)))
            .Select(m => m.RoomId)
            .ToListAsync(ct);

        var ids = memberRoomIds.Append(personalId).Distinct().ToList();
        var rooms = await db.Rooms.Where(r => ids.Contains(r.Id)).ToListAsync(ct);

        var result = new List<RoomListEntry>(rooms.Count);
        foreach (var r in rooms)
            result.Add(new RoomListEntry(
                r.Id, r.Name, r.Kind, r.Icon, r.Color,
                IsPersonal: r.Kind == RoomKind.Personal && r.OwnerUserId == userId,
                Permissions: await PermissionsAsync(userId, r.Id, ct)));

        return result
            .OrderByDescending(r => r.IsPersonal)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Guid> CreateSharedRoomAsync(
        string name, string? description, string? icon, string? color, CancellationToken ct = default)
    {
        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Icon = icon,
            Color = color,
            Kind = RoomKind.Shared,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync(ct);
        return room.Id;
    }

    public async Task<bool> UpdateRoomAsync(
        Guid roomId, string name, string? description, string? icon, string? color, CancellationToken ct = default)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null || room.Kind == RoomKind.Personal) return false; // personal rooms are immutable

        room.Name = name;
        room.Description = description;
        room.Icon = icon;
        room.Color = color;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteRoomAsync(Guid roomId, CancellationToken ct = default)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null || room.Kind == RoomKind.Personal) return false; // personal rooms cannot be deleted

        // Remove members + (shared) placements explicitly, so the outcome is the same under the in-memory
        // provider (no cascade) as under Postgres. A shared room holds only shared placements, so this unshares
        // its recordings; the recordings themselves are never touched.
        db.RoomMembers.RemoveRange(db.RoomMembers.Where(m => m.RoomId == roomId));
        db.RoomRecordings.RemoveRange(db.RoomRecordings.Where(p => p.RoomId == roomId));
        db.Rooms.Remove(room);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetMemberAsync(
        Guid roomId, RoomPrincipalType type, Guid principalId, RoomPermission permissions, CancellationToken ct = default)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null || room.Kind == RoomKind.Personal) return false; // personal rooms are memberless

        var existing = await db.RoomMembers.FirstOrDefaultAsync(
            m => m.RoomId == roomId && m.PrincipalType == type && m.PrincipalId == principalId, ct);
        if (existing is null)
            db.RoomMembers.Add(new RoomMember
            {
                RoomId = roomId, PrincipalType = type, PrincipalId = principalId, Permissions = permissions,
            });
        else
            existing.Permissions = permissions;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveMemberAsync(
        Guid roomId, RoomPrincipalType type, Guid principalId, CancellationToken ct = default)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null || room.Kind == RoomKind.Personal) return false;

        var existing = await db.RoomMembers.FirstOrDefaultAsync(
            m => m.RoomId == roomId && m.PrincipalType == type && m.PrincipalId == principalId, ct);
        if (existing is null) return false;

        db.RoomMembers.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RoomPermission> PermissionsAsync(Guid userId, Guid roomId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => new { r.Kind, r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (room is null) return RoomPermission.None;

        // The owner of a personal room holds everything, with no member row needed.
        if (room.Kind == RoomKind.Personal) return room.OwnerUserId == userId ? AllPermissions : RoomPermission.None;

        var rows = await MemberRowsAsync(userId, roomId, ct);

        var result = RoomPermission.None;
        foreach (var r in rows) result |= r;
        return result;
    }

    /// <summary>Membership is row existence, NOT "holds some permission". A member granted RoomPermission.None
    /// can still see the room; inferring membership from the flags would 404 them out of a room they belong to.</summary>
    public async Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => new { r.Kind, r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (room is null) return false;
        if (room.Kind == RoomKind.Personal) return room.OwnerUserId == userId;

        return (await MemberRowsAsync(userId, roomId, ct)).Count > 0;
    }

    public async Task RequireAsync(Guid userId, Guid roomId, RoomPermission required, CancellationToken ct = default)
    {
        if (!(await PermissionsAsync(userId, roomId, ct)).HasFlag(required))
            throw new RoomForbiddenException(required);
    }

    // An explicit join, not `.Select(p => p.Recording!)`: the in-memory test provider does not fix up the
    // navigation for an untracked query and would yield nulls. Same trap as UserPermissions in Phase 1.
    public IQueryable<Recording> RecordingsIn(Guid roomId) =>
        from p in db.RoomRecordings
        where p.RoomId == roomId
        join r in db.Recordings on p.RecordingId equals r.Id
        select r;

    public async Task PlaceInMainRoomAsync(
        Guid recordingId, Guid recordedByUserId, Guid? sectionId, CancellationToken ct = default)
    {
        var roomId = await PersonalRoomIdAsync(recordedByUserId, ct);
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId,
            RecordingId = recordingId,
            IsMainRoom = true,
            SectionId = sectionId,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ShareIntoRoomAsync(
        Guid recordingId, Guid roomId, Guid sharedByUserId, Guid? sectionId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => new { r.Kind })
            .FirstOrDefaultAsync(ct);
        if (room is null || room.Kind != RoomKind.Shared) return false; // only Shared rooms take shared placements

        // Idempotent: don't add a second row if it is already placed here.
        if (await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId && p.RecordingId == recordingId, ct)) return true;

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId,
            RecordingId = recordingId,
            IsMainRoom = false,
            SectionId = sectionId,
            SharedByUserId = sharedByUserId,
            SharedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<Guid?> SectionIdAsync(Guid roomId, Guid recordingId, CancellationToken ct = default) =>
        db.RoomRecordings
            .Where(p => p.RoomId == roomId && p.RecordingId == recordingId)
            .Select(p => p.SectionId)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetSectionAsync(
        Guid roomId, Guid recordingId, Guid? sectionId, CancellationToken ct = default)
    {
        var placement = await db.RoomRecordings
            .FirstOrDefaultAsync(p => p.RoomId == roomId && p.RecordingId == recordingId, ct);
        if (placement is null) return false;

        // A recording can only be filed under a folder in the SAME room as the placement. Trivially true while
        // sections are personal, but load-bearing once shared rooms have folders.
        if (sectionId is { } sid && !await db.Sections.AnyAsync(s => s.Id == sid && s.RoomId == roomId, ct))
            return false;

        placement.SectionId = sectionId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The caller's member rows in a room: their own, plus one per group they belong to.</summary>
    private async Task<List<RoomPermission>> MemberRowsAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var groupIds = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        return await db.RoomMembers
            .Where(m => m.RoomId == roomId
                        && ((m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == userId)
                            || (m.PrincipalType == RoomPrincipalType.Group && groupIds.Contains(m.PrincipalId))))
            .Select(m => m.Permissions)
            .ToListAsync(ct);
    }

    private Task<Guid?> FindPersonalRoomIdAsync(Guid userId, CancellationToken ct) =>
        db.Rooms
            .Where(r => r.OwnerUserId == userId && r.Kind == RoomKind.Personal)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>A personal room's display name. Never empty: the Name column is required.</summary>
    private static string Display(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName!.Trim();
        if (!string.IsNullOrWhiteSpace(user.Email)) return user.Email!;
        return "Personal";
    }
}
