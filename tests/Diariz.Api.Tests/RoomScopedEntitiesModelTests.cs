using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Phase 2d: voiceprints, chats and meeting types carry a RoomId. Plain columns for now (the FK and
/// the UserId retirement wait for Phase 4, as in 2c); MeetingType's is nullable (null = platform type).</summary>
public class RoomScopedEntitiesModelTests
{
    [Fact]
    public async Task SpeakerProfile_And_ChatSession_And_MeetingType_CarryARoomId()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "A" });
        db.ChatSessions.Add(new ChatSession { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Title = "C", MessagesJson = "[]" });
        db.MeetingTypes.Add(new MeetingType { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Title = "T" });
        db.MeetingTypes.Add(new MeetingType { Id = Guid.NewGuid(), UserId = null, RoomId = null, Title = "Platform" });
        await db.SaveChangesAsync();

        Assert.Equal(roomId, db.SpeakerProfiles.Single().RoomId);
        Assert.Equal(roomId, db.ChatSessions.Single().RoomId);
        Assert.Equal(roomId, db.MeetingTypes.Single(m => m.UserId == userId).RoomId);
        Assert.Null(db.MeetingTypes.Single(m => m.UserId == null).RoomId);
    }
}
