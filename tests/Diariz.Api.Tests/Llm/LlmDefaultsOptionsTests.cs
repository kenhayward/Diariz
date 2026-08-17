using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace Diariz.Api.Tests.Llm;

public class LlmDefaultsOptionsTests
{
    private static LlmDefaultsOptions FromConfig(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        var opts = new LlmDefaultsOptions();
        config.GetSection(LlmDefaultsOptions.Section).Bind(opts);
        return opts;
    }

    /// <summary>How the resolver will stack them: group layer over base layer.</summary>
    private static LlmParameters Resolve(LlmDefaultsOptions o, LlmCallGroup group) =>
        LlmParameterLayers.Resolve([o.LayerFor(group), o.BaseLayer]);

    [Fact]
    public void Ships_todays_behaviour_as_its_defaults()
    {
        // The whole refactor rests on this: an empty database and no overrides must reproduce today.
        var p = Resolve(new LlmDefaultsOptions(), LlmCallGroup.Summaries);

        Assert.Equal(0.3, p.Temperature);
        Assert.Null(p.MaxTokens);
        Assert.Null(p.MaxCompletionTokens);
        Assert.Null(p.TopP);
        Assert.Null(p.TopK);
        Assert.Null(p.RepeatPenalty);
        Assert.Null(p.FrequencyPenalty);
        Assert.Null(p.PresencePenalty);
        Assert.False(p.ReasoningEnabled);
        Assert.Equal(120, p.TimeoutSeconds);
    }

    [Fact]
    public void Keeps_translations_lower_temperature_as_a_group_default()
    {
        // Today's one deliberate exception, and the reason app defaults have to be group-capable.
        Assert.Equal(0.1, Resolve(new LlmDefaultsOptions(), LlmCallGroup.Translation).Temperature);
    }

    [Fact]
    public void Applies_the_base_temperature_to_every_other_group()
    {
        var o = new LlmDefaultsOptions();
        foreach (var group in new[]
                 {
                     LlmCallGroup.Tags, LlmCallGroup.Actions, LlmCallGroup.Summaries,
                     LlmCallGroup.MinutesAndFormulas, LlmCallGroup.Chat,
                 })
            Assert.Equal(0.3, Resolve(o, group).Temperature);
    }

    [Fact]
    public void Binds_a_base_value_from_configuration()
    {
        var o = FromConfig(("LlmDefaults:Temperature", "0.7"));
        Assert.Equal(0.7, LlmParameterLayers.Resolve([o.BaseLayer]).Temperature);
    }

    [Fact]
    public void Binds_a_group_override_from_configuration()
    {
        var o = FromConfig(("LlmDefaults:Temperature", "0.7"), ("LlmDefaults:Tags:Temperature", "0.0"));

        Assert.Equal(0.0, Resolve(o, LlmCallGroup.Tags).Temperature);
        Assert.Equal(0.7, Resolve(o, LlmCallGroup.Chat).Temperature);
    }

    [Fact]
    public void Binds_a_token_cap_that_ships_unset()
    {
        var o = FromConfig(("LlmDefaults:MaxTokens", "1500"));
        Assert.Equal(1500, LlmParameterLayers.Resolve([o.BaseLayer]).MaxTokens);
    }

    [Fact]
    public void Has_no_layer_for_a_group_nobody_configured()
    {
        Assert.Null(FromConfig(("LlmDefaults:Temperature", "0.7")).LayerFor(LlmCallGroup.Chat));
    }

    [Fact]
    public void Has_no_layer_for_the_kinds_that_take_no_sampling_parameters()
    {
        Assert.Null(new LlmDefaultsOptions().LayerFor(null));
        Assert.Null(new LlmDefaultsOptions().LayerFor(LlmCallGroup.ModelBase));
    }
}
