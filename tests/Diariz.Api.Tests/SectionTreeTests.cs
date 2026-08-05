using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class SectionTreeTests
{
    // A fixed tree used across the cases:
    //   customers -> acme -> falcon
    //   podcasts (unrelated top-level)
    private static readonly Guid Customers = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Falcon = Guid.NewGuid();
    private static readonly Guid Podcasts = Guid.NewGuid();

    private static SectionLink[] Tree() =>
    [
        new(Customers, null),
        new(Acme, Customers),
        new(Falcon, Acme),
        new(Podcasts, null),
    ];

    [Fact]
    public void Subtree_OfALeaf_IsJustItself()
    {
        Assert.Equal([Falcon], SectionTree.Subtree(Tree(), Falcon));
    }

    [Fact]
    public void Subtree_IncludesGrandchildren_NotJustDirectChildren()
    {
        var ids = SectionTree.Subtree(Tree(), Customers);

        Assert.Equal(Customers, ids[0]);           // the root leads
        Assert.Contains(Acme, ids);                // direct child
        Assert.Contains(Falcon, ids);              // grandchild - the whole point
        Assert.DoesNotContain(Podcasts, ids);      // unrelated branch excluded
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void Subtree_OfAnUnknownRoot_IsStillJustThatRoot()
    {
        // Callers add the root themselves today; keeping that contract means a deleted
        // folder yields "nothing but itself" rather than an empty set that reads as "everything".
        var unknown = Guid.NewGuid();
        Assert.Equal([unknown], SectionTree.Subtree(Tree(), unknown));
    }

    [Fact]
    public void Subtree_WithACycle_Terminates()
    {
        // Nothing in the schema prevents a ParentId cycle. Without a visited set this spins forever.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SectionLink[] cyclic = [new(a, b), new(b, a)];

        var ids = SectionTree.Subtree(cyclic, a);

        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public async Task SubtreeIdsAsync_ReadsOneRoomsFolders_AndReachesGrandchildren()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        var otherRoomId = Guid.NewGuid();
        var customers = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Customers" };
        var acme = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Acme", ParentId = customers.Id };
        var falcon = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Falcon", ParentId = acme.Id };
        // Same shape in a different room - must not leak in.
        var decoy = new Section { Id = Guid.NewGuid(), RoomId = otherRoomId, Name = "Decoy", ParentId = customers.Id };
        db.Sections.AddRange(customers, acme, falcon, decoy);
        await db.SaveChangesAsync();

        var ids = await SectionTree.SubtreeIdsAsync(db, roomId, customers.Id, default);

        Assert.Equal(3, ids.Count);
        Assert.Contains(falcon.Id, ids);
        Assert.DoesNotContain(decoy.Id, ids);
    }
}
