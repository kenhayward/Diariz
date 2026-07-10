using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Phase 3: UserSettings carries the recording-placement preference (mode + a fixed folder for the
/// SpecificFolder mode).</summary>
public class RecordingPlacementPreferenceTests
{
    [Fact]
    public async Task Persists_TheModeAndSpecificFolder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            RecordingPlacementMode = RecordingPlacementMode.SpecificFolder,
            RecordingPlacementSectionId = sectionId,
        });
        await db.SaveChangesAsync();

        var loaded = db.UserSettings.Single(s => s.UserId == userId);
        Assert.Equal(RecordingPlacementMode.SpecificFolder, loaded.RecordingPlacementMode);
        Assert.Equal(sectionId, loaded.RecordingPlacementSectionId);
    }

    [Fact]
    public void DefaultsTo_SelectedFolder_WithNoFixedFolder()
    {
        var settings = new UserSettings { UserId = Guid.NewGuid() };
        Assert.Equal(RecordingPlacementMode.SelectedFolder, settings.RecordingPlacementMode);
        Assert.Null(settings.RecordingPlacementSectionId);
    }
}
