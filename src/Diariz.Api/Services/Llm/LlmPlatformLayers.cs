using System.Text.Json.Nodes;
using Diariz.Api.Configuration;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services.Llm;

/// <summary>The parameter layers that sit BELOW a model's own, shared by <see cref="LlmSettingsResolver"/>
/// and the model editor's test call.
///
/// <para>Shared rather than written twice on purpose. The test rail exists to show an administrator what a
/// real call would do; a second copy of this walk means the panel can quietly disagree with the pipeline,
/// which is the one thing it must never do.</para></summary>
public static class LlmPlatformLayers
{
    /// <summary>Most specific first: the application's per-group defaults, then the administrator's
    /// platform timeout, then the application's base defaults.
    ///
    /// <para><b>Why the platform timeout is a layer at all.</b> It was read only on the environment-fallback
    /// path, so it went inert the moment a deployment configured its first model - while the Settings
    /// control went on promising a "platform-wide request timeout for every AI call". Every call silently
    /// reverted to the shipped 120s, which on a large local model looks like a dead endpoint rather than a
    /// setting that was ignored (0.235.1).</para>
    ///
    /// <para>It sits <b>below</b> the model's layers, so per-model tuning still wins - it is the floor for a
    /// model that says nothing, not an override. And it speaks only when the administrator has actually
    /// changed it: at its default it stays silent, so an operator's <c>LlmDefaults__TimeoutSeconds</c>
    /// keeps working rather than being outranked by a row that exists merely because settings were once
    /// saved.</para></summary>
    public static List<string?> Below(
        LlmDefaultsOptions defaults, LlmCallGroup? group, PlatformSettings? platform) =>
    [
        defaults.LayerFor(group),
        TimeoutLayer(platform),
        defaults.BaseLayer,
    ];

    /// <summary>The application base layer with the administrator's platform timeout merged in - the single
    /// bottom layer, flattened, for the editor to resolve inherited values and preview a request body
    /// against. The client cannot walk <see cref="Below"/> itself: it never sees PlatformSettings.</summary>
    public static string? BaseWithPlatformTimeout(LlmDefaultsOptions defaults, PlatformSettings? platform)
    {
        if (TimeoutLayer(platform) is null) return defaults.BaseLayer;

        var merged = defaults.BaseLayer is { } layer
            ? JsonNode.Parse(layer)!.AsObject()
            : new JsonObject();
        merged[LlmParameterLayers.TimeoutSeconds] = JsonValue.Create(platform!.LlmTimeoutSeconds);
        return merged.ToJsonString();
    }

    private static string? TimeoutLayer(PlatformSettings? platform) =>
        platform is null || platform.LlmTimeoutSeconds == PlatformSettings.DefaultLlmTimeoutSeconds
            ? null
            : new JsonObject
            {
                [LlmParameterLayers.TimeoutSeconds] = JsonValue.Create(platform.LlmTimeoutSeconds),
            }.ToJsonString();
}
