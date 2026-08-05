using Diariz.Api.Services;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class SectionSubtreeIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task SubtreeIds_ReachThreeLevelsDown_AndStopAtTheRoomBoundary()
    {
        Guid roomId, customersId, falconId, acmeId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var room = new Room { Id = Guid.NewGuid(), Name = $"P {Guid.NewGuid():N}", Kind = RoomKind.Personal, OwnerUserId = user.Id };
            var other = new Room { Id = Guid.NewGuid(), Name = $"O {Guid.NewGuid():N}", Kind = RoomKind.Shared };
            var customers = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Customers" };
            var acme = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Acme", ParentId = customers.Id };
            var falcon = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Falcon", ParentId = acme.Id };
            // Another room's folder claiming the same parent - the room filter must exclude it.
            var decoy = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = other.Id, Name = "Decoy", ParentId = customers.Id };
            db.AddRange(user, room, other, customers, acme, falcon, decoy);
            await db.SaveChangesAsync();
            (roomId, customersId, falconId, acmeId) = (room.Id, customers.Id, falcon.Id, acme.Id);
        }

        await using var verify = fx.CreateDbContext();
        var ids = await SectionTree.SubtreeIdsAsync(verify, roomId, customersId, default);

        Assert.Equal(3, ids.Count);
        Assert.Equal(customersId, ids[0]);
        Assert.Contains(acmeId, ids);
        Assert.Contains(falconId, ids);   // the grandchild - the bug this phase prevents
    }

    [Fact]
    public async Task DeletingATopFolder_CascadesThreeLevels_AndUngroupsTheDeepestRecording()
    {
        Guid userId, customersId, falconId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var room = new Room { Id = Guid.NewGuid(), Name = $"P {Guid.NewGuid():N}", Kind = RoomKind.Personal, OwnerUserId = user.Id };
            var customers = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Customers" };
            var acme = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Acme", ParentId = customers.Id };
            var falcon = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Falcon", ParentId = acme.Id };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            db.AddRange(user, room, customers, acme, falcon, rec);
            await db.SaveChangesAsync();
            await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, user.Id, falcon.Id);
            (userId, customersId, falconId, recId) = (user.Id, customers.Id, falcon.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Remove(await db.Sections.FindAsync(customersId) ?? throw new InvalidOperationException());
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        // Postgres cascades a self-referencing FK recursively, so the grandchild goes too.
        Assert.Null(await verify.Sections.FindAsync(falconId));
        var scope = new RoomScope(verify);
        var roomId = await scope.PersonalRoomIdAsync(userId);
        Assert.Null(await scope.SectionIdAsync(roomId, recId)); // ungrouped, not deleted
        Assert.NotNull(await verify.Recordings.FindAsync(recId));
    }
}
