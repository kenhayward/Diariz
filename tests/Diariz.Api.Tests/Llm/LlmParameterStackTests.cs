using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Llm;

/// <summary>The ordered layer list a model's parameters resolve through.
///
/// <para>It is its own type because TWO callers need the identical walk: <see cref="LlmSettingsResolver"/>,
/// deciding what to send, and <c>ChatModelCatalog</c>, telling the picker what a model can do. Written
/// separately in each they would agree by coincidence and diverge on the first change, producing a picker
/// that offers a capability the pipeline then refuses.</para></summary>
public class LlmParameterStackTests
{
    private static LlmModel ModelWith(params (LlmCallGroup Group, string Json)[] rows)
    {
        var m = new LlmModel { Id = Guid.NewGuid(), Name = "m", ApiBase = "http://llm/v1" };
        foreach (var (group, json) in rows)
            m.Parameters.Add(new LlmModelParameters { Group = group, ParametersJson = json });
        return m;
    }

    [Fact]
    public void For_PutsTheGroupOverrideFirstThenModelBase()
    {
        var model = ModelWith(
            (LlmCallGroup.Chat, """{"temperature":0.1}"""),
            (LlmCallGroup.ModelBase, """{"temperature":0.9}"""));

        var stack = LlmParameterStack.For(model, LlmCallGroup.Chat, new LlmDefaultsOptions(), null);

        Assert.Equal("""{"temperature":0.1}""", stack[0]);
        Assert.Equal("""{"temperature":0.9}""", stack[1]);
    }

    /// <summary>A missing override must occupy its slot as null rather than vanish: the layers below it
    /// have to stay at the same depth, or the walk silently changes meaning.</summary>
    [Fact]
    public void For_NoOverrideForTheGroup_LeavesANullInThatSlot()
    {
        var model = ModelWith((LlmCallGroup.ModelBase, """{"temperature":0.9}"""));

        var stack = LlmParameterStack.For(model, LlmCallGroup.Chat, new LlmDefaultsOptions(), null);

        Assert.Null(stack[0]);
        Assert.Equal("""{"temperature":0.9}""", stack[1]);
    }

    [Fact]
    public void For_NoModelAtAll_IsJustThePlatformLayers()
    {
        var defaults = new LlmDefaultsOptions();

        var stack = LlmParameterStack.For(null, LlmCallGroup.Chat, defaults, null);

        Assert.Null(stack[0]);
        Assert.Null(stack[1]);
        Assert.Equal(LlmPlatformLayers.Below(defaults, LlmCallGroup.Chat, null), stack[2..]);
    }

    /// <summary>The platform layers must follow the model's, in their own order. Reordering them would move
    /// the administrator's platform timeout above per-model tuning, which is the exact inversion
    /// LlmPlatformLayers documents as wrong.</summary>
    [Fact]
    public void For_AppendsThePlatformLayersBelowTheModelsInTheirOwnOrder()
    {
        var defaults = new LlmDefaultsOptions();
        var platform = new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 600 };
        var model = ModelWith((LlmCallGroup.ModelBase, """{"temperature":0.9}"""));

        var stack = LlmParameterStack.For(model, LlmCallGroup.Chat, defaults, platform);

        Assert.Equal(LlmPlatformLayers.Below(defaults, LlmCallGroup.Chat, platform), stack[2..]);
    }

    [Fact]
    public void For_NullGroup_StillIncludesTheModelBaseLayer()
    {
        var model = ModelWith(
            (LlmCallGroup.Chat, """{"temperature":0.1}"""),
            (LlmCallGroup.ModelBase, """{"temperature":0.9}"""));

        var stack = LlmParameterStack.For(model, null, new LlmDefaultsOptions(), null);

        Assert.Null(stack[0]); // no group means no group override, NOT the Chat one
        Assert.Equal("""{"temperature":0.9}""", stack[1]);
    }
}
