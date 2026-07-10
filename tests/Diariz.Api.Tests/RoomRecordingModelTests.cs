using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomRecordingModelTests
{
    [Fact]
    public async Task MainPlacement_RoundTrips_WithAFolderAndNoSharer()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId,
            RecordingId = recordingId,
            IsMainRoom = true,
            SectionId = sectionId,
        });
        await db.SaveChangesAsync();

        var placement = db.RoomRecordings.Single();
        Assert.True(placement.IsMainRoom);
        Assert.Equal(sectionId, placement.SectionId);
        Assert.Null(placement.SharedByUserId);
        Assert.Null(placement.SharedAt);
    }

    [Fact]
    public async Task SharedPlacement_CarriesTheSharerAndNoMainFlag()
    {
        using var db = TestDb.Create();
        var sharer = Guid.NewGuid();
        var sharedAt = DateTimeOffset.UtcNow;

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            IsMainRoom = false,
            SectionId = null, // ungrouped in the room it was shared into
            SharedByUserId = sharer,
            SharedAt = sharedAt,
        });
        await db.SaveChangesAsync();

        var placement = db.RoomRecordings.Single();
        Assert.False(placement.IsMainRoom);
        Assert.Null(placement.SectionId);
        Assert.Equal(sharer, placement.SharedByUserId);
        Assert.Equal(sharedAt, placement.SharedAt);
    }
}
