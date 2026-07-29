using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Keeps the people directory honest: every user account is also a <see cref="Person"/>, and a
/// person's voiceprint stays in step with the voice samples behind it.</summary>
public interface IPeopleDirectory
{
    /// <summary>The <see cref="Person"/> for this account, minting it on first ask. Idempotent.</summary>
    Task<Person> EnsureForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Pushes the account's name and email onto its linked person, and cascades the name onto every
    /// speaker already identified as them. Call after any write that changes a user's name or email.</summary>
    Task SyncFromUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Rebuilds the centroid and sample count from the person's remaining <see cref="VoiceSample"/>
    /// rows, clearing the voiceprint entirely when none are left. Call after anything that removes samples -
    /// including deleting a recording, which cascades them away without telling anyone.</summary>
    Task RecomputeVoiceprintAsync(Guid personId, CancellationToken ct = default);

    /// <summary>Destroys the person's voiceprint - the centroid and every voice sample behind it - while
    /// keeping the person. Reverts labels that automatic identification applied, but <b>keeps names typed by
    /// hand, and keeps their link to the person</b>: those are the user's own assertion about who was in the
    /// room, not something derived from the biometric.
    ///
    /// <para>Deliberately narrower than the full-delete path, which clears every link. Erasing a biometric is
    /// not the same as forgetting that someone attended.</para></summary>
    Task EraseVoiceprintAsync(Guid personId, CancellationToken ct = default);
}

/// <summary>Modelled on <see cref="RoomScope.PersonalRoomIdAsync"/>, down to the find-or-create race
/// handling: the filtered unique index on <c>LinkedUserId</c> makes a concurrent create safe to lose.</summary>
public class PeopleDirectory(DiarizDbContext db) : IPeopleDirectory
{
    public async Task<Person> EnsureForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await FindForUserAsync(userId, ct);
        if (existing is not null) return existing;

        // Created here rather than at the user-creation sites (AdminUsersController, AuthController,
        // GoogleSignInHandler, Seeder), so a fifth site cannot forget - the same argument as RoomScope.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException($"No such user: {userId}");

        var person = new Person
        {
            Id = Guid.NewGuid(),
            LinkedUserId = userId,
            Name = Display(user),
            Email = user.Email,
            IsInternal = true, // an account holder is by definition one of us
        };
        db.People.Add(person);

        try
        {
            await db.SaveChangesAsync(ct);
            return person;
        }
        catch (DbUpdateException)
        {
            // Another request created it between our read and our write. Theirs is as good as ours.
            db.ChangeTracker.Clear();
            return await FindForUserAsync(userId, ct)
                   ?? throw new InvalidOperationException($"Person vanished for user {userId}");
        }
    }

    public async Task SyncFromUserAsync(Guid userId, CancellationToken ct = default)
    {
        var person = await EnsureForUserAsync(userId, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        var name = Display(user);
        var changed = person.Name != name || person.Email != user.Email;
        if (!changed) return;

        person.Name = name;
        person.Email = user.Email;
        person.UpdatedAt = DateTimeOffset.UtcNow;

        // Speaker.DisplayName is denormalised - the name is copied onto each linked speaker rather than
        // joined at read time, because segments, exports, email, chat, MCP and minutes all read it with no
        // join. So a rename has to fan out, or every past transcript keeps showing the old name.
        foreach (var speaker in await db.Speakers.Where(s => s.PersonId == person.Id).ToListAsync(ct))
            speaker.DisplayName = name;

        await db.SaveChangesAsync(ct);
    }

    public async Task RecomputeVoiceprintAsync(Guid personId, CancellationToken ct = default)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null) return;

        var samples = await db.VoiceSamples.Where(v => v.PersonId == personId).ToListAsync(ct);

        // Embedding is Ignore'd under the in-memory provider, so a sample's vector can be null there even
        // though the column is NOT NULL on Postgres. Centroid returns null for an empty set either way.
        var snapshots = samples.Where(v => v.Embedding is not null).Select(v => v.Embedding.ToArray()).ToList();
        person.Embedding = Voiceprints.Centroid(snapshots);
        person.SampleCount = samples.Count;
        person.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task EraseVoiceprintAsync(Guid personId, CancellationToken ct = default)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null) return;

        person.Embedding = null;
        person.SampleCount = 0;
        person.UpdatedAt = DateTimeOffset.UtcNow;

        db.VoiceSamples.RemoveRange(await db.VoiceSamples.Where(v => v.PersonId == personId).ToListAsync(ct));

        foreach (var speaker in await db.Speakers.Where(s => s.PersonId == personId).ToListAsync(ct))
        {
            // Only auto-applied names came from the biometric, so only those revert. A name someone typed
            // stays, and keeps pointing at the person - it is their statement about who was speaking, and
            // erasing the voiceprint does not make it untrue.
            if (!speaker.IdentifiedAuto) continue;
            speaker.PersonId = null;
            speaker.DisplayName = speaker.Label;
            speaker.IdentifiedAuto = false;
        }

        await db.SaveChangesAsync(ct);
    }

    private Task<Person?> FindForUserAsync(Guid userId, CancellationToken ct) =>
        db.People.FirstOrDefaultAsync(p => p.LinkedUserId == userId, ct);

    /// <summary>Mirrors <c>RoomScope.Display</c>: an invited user has no FullName yet, and a person with a
    /// blank name is unusable in the assign typeahead.</summary>
    private static string Display(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName!.Trim();
        if (!string.IsNullOrWhiteSpace(user.Email)) return user.Email!;
        return "Unknown";
    }
}
