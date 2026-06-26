using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diariz.Api.Configuration;

/// <summary>
/// Central JSON configuration for the API's HTTP responses, shared by Program.cs and tests.
/// </summary>
public static class JsonConfig
{
    public static void Apply(JsonSerializerOptions options)
    {
        // Serialize enums (e.g. RecordingStatus) by their string name so the web client's
        // string-union types match the wire format instead of receiving raw numbers.
        options.Converters.Add(new JsonStringEnumConverter());
    }
}
