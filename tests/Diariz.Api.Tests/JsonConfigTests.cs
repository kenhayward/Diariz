using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class JsonConfigTests
{
    // The web client types RecordingStatus as a string union ("Transcribed", "Failed", ...).
    // The API must serialize the enum by name, not its underlying number, or the UI shows
    // "3" instead of "Transcribed" and never matches status === "Failed".
    [Fact]
    public void RecordingStatus_SerializesAsStringName_NotNumber()
    {
        var options = new JsonSerializerOptions();
        JsonConfig.Apply(options);

        var dto = new RecordingSummaryDto(
            Guid.NewGuid(), "title", null, RecordingSource.Microphone, 0, RecordingStatus.Transcribed, DateTimeOffset.UnixEpoch,
            null, null, false, true);
        var json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"Transcribed\"", json);
    }
}
