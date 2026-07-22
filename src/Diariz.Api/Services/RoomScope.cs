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

/// <summary>A room a recording is placed in (its main room, plus any it is shared into).</summary>
public record RecordingRoomPlacement(
    Guid RoomId, string Name, RoomKind Kind, string? Icon, string? Color, bool IsMainRoom);

/// <summary>The result of the single "can read" walk over a recording's placements: whether
/// <paramref name="CanRead"/>, plus the placements that made it true (or, for an owner, that are simply visible
/// to them) - so a caller that needs both (<see cref="Diariz.Api.Controllers.RecordingsController.Get"/>) can
/// get them from one walk instead of two.</summary>
public record RecordingReadAccess(bool CanRead, IReadOnlyList<RecordingRoomPlacement> VisibleRooms);

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

    /// <summary>The single "can read" rule for a recording: true when <paramref name="userId"/> owns it, or is
    /// a member of any room it is placed in (false, including for a recording that doesn't exist).
    /// Per-recording sub-resources that only need the yes/no answer (screenshots, meeting notes, ...) call this;
    /// it is a thin wrapper over <see cref="ReadAccessForRecordingAsync"/>, the shared implementation, so the
    /// rule cannot drift between the two.</summary>
    Task<bool> CanReadRecordingAsync(Guid userId, Guid recordingId, CancellationToken ct = default);

    /// <summary>The same "can read" rule as <see cref="CanReadRecordingAsync"/>, plus the placements the walk
    /// found visible to <paramref name="userId"/> - for <see cref="Diariz.Api.Controllers.RecordingsController.Get"/>,
    /// which needs both the boolean and the room list for its response and would otherwise have to walk the
    /// recording's placements twice (once to build the room list, once inside CanReadRecordingAsync).</summary>
    Task<RecordingReadAccess> ReadAccessForRecordingAsync(Guid userId, Guid recordingId, CancellationToken ct = default);

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

    /// <summary>The rooms a recording is placed in - its main room first, then any it is shared into by name.</summary>
    Task<IReadOnlyList<RecordingRoomPlacement>> RoomsForRecordingAsync(Guid recordingId, CancellationToken ct = default);

    /// <summary>Unshare a recording from a room (delete the non-main placement). False when there is no such
    /// placement, or when it is the main room (destroying that is a delete, issued from the recording's home).</summary>
    Task<bool> RemoveFromRoomAsync(Guid recordingId, Guid roomId, CancellationToken ct = default);

    /// <summary>The ids of every room the caller belongs to (their personal room plus any shared room). The read
    /// set for cross-room search: a chunk/recording is visible if it is placed in one of these rooms.</summary>
    Task<IReadOnlyList<Guid>> RoomIdsForUserAsync(Guid userId, CancellationToken ct = default);

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

    public async Task<bool> CanReadRecordingAsync(Guid userId, Guid recordingId, CancellationToken ct = default) =>
        (await ReadAccessForRecordingAsync(userId, recordingId, ct)).CanRead;

    public async Task<RecordingReadAccess> ReadAccessForRecordingAsync(
        Guid userId, Guid recordingId, CancellationToken ct = default)
    {
        var ownerId = await db.Recordings
            .Where(r => r.Id == recordingId)
            .Select(r => (Guid?)r.UserId)
            .FirstOrDefaultAsync(ct);
        if (ownerId is null) return new RecordingReadAccess(false, []); // no such recording

        // Walked once regardless of ownership: the response also needs the rooms visible to THIS caller (an
        // owner is not necessarily a member of every room their recording is shared into - the room list only
        // shows what they can actually see), so the loop isn't skippable even when ownerId == userId.
        var placements = await RoomsForRecordingAsync(recordingId, ct);
        var visible = new List<RecordingRoomPlacement>();
        foreach (var p in placements)
            if (await IsMemberAsync(userId, p.RoomId, ct)) visible.Add(p);

        return new RecordingReadAccess(ownerId == userId || visible.Count > 0, visible);
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
        // Idempotent: a recording has exactly one main placement, so a repeat call updates its folder rather
        // than minting a second main row (which the filtered-unique index would reject anyway).
        var existing = await db.RoomRecordings.FirstOrDefaultAsync(p => p.RecordingId == recordingId && p.IsMainRoom, ct);
        if (existing is not null)
            existing.SectionId = sectionId;
        else
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

    public async Task<IReadOnlyList<RecordingRoomPlacement>> RoomsForRecordingAsync(
        Guid recordingId, CancellationToken ct = default)
    {
        var placements = await (
            from p in db.RoomRecordings
            where p.RecordingId == recordingId
            join r in db.Rooms on p.RoomId equals r.Id
            select new RecordingRoomPlacement(r.Id, r.Name, r.Kind, r.Icon, r.Color, p.IsMainRoom)
        ).ToListAsync(ct);

        return placements
            .OrderByDescending(p => p.IsMainRoom)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<bool> RemoveFromRoomAsync(Guid recordingId, Guid roomId, CancellationToken ct = default)
    {
        var placement = await db.RoomRecordings
            .FirstOrDefaultAsync(p => p.RoomId == roomId && p.RecordingId == recordingId, ct);
        if (placement is null || placement.IsMainRoom) return false; // the main placement can only be destroyed by a delete

        db.RoomRecordings.Remove(placement);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> RoomIdsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        // A read path - do NOT mint a personal room here (unlike RoomsForUserAsync). A user with no room yet
        // simply has no recordings to see, so search finds nothing rather than throwing on a missing user.
        var personal = await FindPersonalRoomIdAsync(userId, ct);

        var groupIds = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var ids = await db.RoomMembers
            .Where(m => (m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == userId)
                        || (m.PrincipalType == RoomPrincipalType.Group && groupIds.Contains(m.PrincipalId)))
            .Select(m => m.RoomId)
            .ToListAsync(ct);

        if (personal is { } p) ids.Add(p);
        return ids.Distinct().ToList();
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
