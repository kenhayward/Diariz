using Diariz.Api.Configuration;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services.Llm;

/// <summary>The ordered parameter layers a model resolves through, most specific first.
///
/// <para><b>Why this is a type rather than four lines inside the resolver.</b> Two callers need the
/// identical walk. <see cref="LlmSettingsResolver"/> uses it to decide what to send; <see
/// cref="ChatModelCatalog"/> uses it to tell the chat picker what a model can do. Written separately they
/// would agree by coincidence and diverge on the first change - and the failure that produces is the worst
/// kind: a picker that offers a vision model the pipeline then refuses as text-only, or the reverse, with
/// nothing in either code path looking wrong.</para>
///
/// <para>A layer the model does not have is a <c>null</c> ENTRY, not an omission. The resolver skips nulls,
/// so the slot costs nothing - but keeping it means the layers below never shift depth, and a caller
/// indexing the stack (as the tests do) is reading the position it thinks it is.</para></summary>
public static class LlmParameterStack
{
    public static List<string?> For(
        LlmModel? model, LlmCallGroup? group, LlmDefaultsOptions defaults, PlatformSettings? platform)
    {
        var layers = new List<string?>
        {
            group is null || model is null ? null : ParametersFor(model, group.Value),
            model is null ? null : ParametersFor(model, LlmCallGroup.ModelBase),
        };
        layers.AddRange(LlmPlatformLayers.Below(defaults, group, platform));
        return layers;
    }

    private static string? ParametersFor(LlmModel model, LlmCallGroup group) =>
        model.Parameters.FirstOrDefault(p => p.Group == group)?.ParametersJson;
}
