using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

/// <summary>The two generation strategies. Per-section makes one LLM call per model-prompt block and assembles the
/// results in order; single-call makes exactly one call over a skeleton document with <c>[[WRITE: ...]]</c> markers.</summary>
public class MeetingTypeMinutesStrategyTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
        [new(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello")];

    private static readonly SummarizationRequestConfig Config = new("https://llm.test/v1", "sk", "m", 60);

    private static MinutesComposition Compose(MeetingTypeContent content, Func<string, string?>? fields = null) =>
        new(content, "Overview text", fields ?? (_ => ""), Segments, Config, 16000, "PREAMBLE");

    private static MeetingTypeContent TwoPrompts() => new(
    [
        new TemplateSection(1, "Purpose", [new TemplateBlock(TemplateBlock.Prompt, Text: "State the purpose.")]),
        new TemplateSection(1, "Decisions", [new TemplateBlock(TemplateBlock.Prompt, Text: "List decisions.")]),
    ]);

    [Fact]
    public async Task PerSection_calls_the_model_once_per_prompt_block_and_assembles_in_order()
    {
        var client = new FakeMeetingMinutesClient
        {
            // Answer each block based on its instruction, so we can prove ordering.
            Responder = msgs => msgs[0].Content.Contains("State the purpose") ? "The purpose." : "A decision.",
        };
        var strategy = new PerSectionMinutesStrategy(client);

        var md = await strategy.GenerateAsync(Compose(TwoPrompts()));

        Assert.Equal(2, client.Calls);
        Assert.Equal("# Purpose\n\nThe purpose.\n\n# Decisions\n\nA decision.", md);
        // Each call carried the guardrail preamble, the overview, and the transcript as a data turn.
        Assert.Contains("PREAMBLE", client.AllMessages[0][0].Content);
        Assert.Contains("Overview text", client.AllMessages[0][0].Content);
        Assert.Contains("## Transcript:", client.AllMessages[0][1].Content);
    }

    [Fact]
    public async Task PerSection_substitutes_fields_deterministically_without_a_call()
    {
        var content = new MeetingTypeContent(
        [
            new TemplateSection(1, "Details",
                [new TemplateBlock(TemplateBlock.Boilerplate, Text: "Date: "),
                 new TemplateBlock(TemplateBlock.FieldKind, Field: "date")]),
        ]);
        var client = new FakeMeetingMinutesClient();
        var md = await new PerSectionMinutesStrategy(client).GenerateAsync(
            Compose(content, f => f == "date" ? "2026-07-06" : ""));

        Assert.Equal(0, client.Calls);                 // no prompt blocks → no LLM calls
        Assert.Equal("# Details\n\nDate: 2026-07-06", md);
    }

    [Fact]
    public async Task SingleCall_makes_exactly_one_call_over_a_marker_skeleton()
    {
        var client = new FakeMeetingMinutesClient { Result = "# Final\n\nWhole document." };
        var strategy = new SingleCallMinutesStrategy(client);

        var md = await strategy.GenerateAsync(Compose(TwoPrompts()));

        Assert.Equal(1, client.Calls);
        Assert.Equal("# Final\n\nWhole document.", md);       // the model returns the assembled document

        // The single prompt embeds the skeleton with a WRITE marker per prompt block, plus the real headings.
        var system = client.LastMessages![0].Content;
        Assert.Contains("[[WRITE: State the purpose.]]", system);
        Assert.Contains("[[WRITE: List decisions.]]", system);
        Assert.Contains("# Purpose", system);
    }

    [Fact]
    public async Task Strategies_report_their_mode()
    {
        Assert.Equal(Diariz.Domain.Entities.MinutesGenerationMode.PerSection,
            new PerSectionMinutesStrategy(new FakeMeetingMinutesClient()).Mode);
        Assert.Equal(Diariz.Domain.Entities.MinutesGenerationMode.SingleCall,
            new SingleCallMinutesStrategy(new FakeMeetingMinutesClient()).Mode);
    }
}
