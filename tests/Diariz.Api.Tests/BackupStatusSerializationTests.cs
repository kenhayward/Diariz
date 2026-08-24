using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The wire format of the backup status. The web reads <c>lastOutcome</c> as a string union, matching
/// the existing <c>phase</c> field; controller tests assert on the returned record and never see the JSON,
/// which is where casing and enum-shape surprises hide.</summary>
public class BackupStatusSerializationTests
{
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonConfig.Apply(o);
        return o;
    }

    [Fact]
    public void Snapshot_SerializesLastOutcomeAsACamelCaseStringName()
    {
        var snapshot = new BackupProgressSnapshot(false, null, 0, null, BackupOutcome.Failed);

        var json = JsonSerializer.Serialize(snapshot, Options());

        Assert.Contains("\"lastOutcome\":\"Failed\"", json);
    }

    [Fact]
    public void Snapshot_SerializesNoOutcomeAsNull()
    {
        var snapshot = new BackupProgressSnapshot(true, BackupPhase.Objects, 3, null, null);

        var json = JsonSerializer.Serialize(snapshot, Options());

        Assert.Contains("\"lastOutcome\":null", json);
    }
}
