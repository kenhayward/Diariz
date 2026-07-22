using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingScreenshotEntityTests
{
    [Fact]
    public async Task Screenshot_RoundTrips_WithItsCaptureFacts()
    {
        using var db = TestDb.Create();
        var id = Guid.NewGuid();
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = id,
            UserId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            CapturedAtMs = 42_000,
            BlobKey = "user/screenshots/a.png",
            ThumbBlobKey = "user/screenshots/a.thumb.jpg",
            Width = 1920,
            Height = 1080,
            SizeBytes = 1234,
            Ordinal = 0,
        });
        await db.SaveChangesAsync();

        var stored = await db.MeetingScreenshots.SingleAsync(s => s.Id == id);
        Assert.Equal(42_000, stored.CapturedAtMs);
        Assert.Equal("user/screenshots/a.thumb.jpg", stored.ThumbBlobKey);
        Assert.Equal(1920, stored.Width);
    }
}
