using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class SectionRoomModelTests
{
    [Fact]
    public async Task Section_CarriesARoomId()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = roomId, Name = "F" });
        await db.SaveChangesAsync();
        Assert.Equal(roomId, db.Sections.Single().RoomId);
    }
}
