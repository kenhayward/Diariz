using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Llm;

public class LlmCallGroupsTests
{
    [Theory]
    [InlineData(LlmCallKind.Tags, LlmCallGroup.Tags)]
    [InlineData(LlmCallKind.ExtractActions, LlmCallGroup.Actions)]
    [InlineData(LlmCallKind.Summarize, LlmCallGroup.Summaries)]
    [InlineData(LlmCallKind.SectionSummary, LlmCallGroup.Summaries)]
    [InlineData(LlmCallKind.MeetingMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.SectionMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.MeetingTypeMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.FormulaRun, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.Translation, LlmCallGroup.Translation)]
    [InlineData(LlmCallKind.ChatMessage, LlmCallGroup.Chat)]
    [InlineData(LlmCallKind.ChatTitle, LlmCallGroup.Chat)]
    public void Maps_each_chat_kind_to_its_group(LlmCallKind kind, LlmCallGroup expected) =>
        Assert.Equal(expected, LlmCallGroups.GroupFor(kind));

    [Theory]
    [InlineData(LlmCallKind.Embedding)]
    [InlineData(LlmCallKind.SearchQuery)]
    [InlineData(LlmCallKind.Dictation)]
    [InlineData(LlmCallKind.AdminTest)]
    [InlineData(LlmCallKind.Unknown)]
    public void Has_no_group_for_kinds_that_send_no_sampling_parameters(LlmCallKind kind) =>
        Assert.Null(LlmCallGroups.GroupFor(kind));

    /// <summary>Enumerating the enum rather than listing cases: a kind added later without a decision would
    /// slip through both theories above, which only cover the members someone remembered to write down.</summary>
    [Fact]
    public void Every_kind_is_accounted_for()
    {
        foreach (var kind in Enum.GetValues<LlmCallKind>())
        {
            var ex = Record.Exception(() => LlmCallGroups.GroupFor(kind));
            Assert.True(ex is null, $"{kind} has no group decision: {ex?.Message}");
        }
    }

    /// <summary>ModelBase must be zero so it can be half of a non-nullable unique key: Postgres treats
    /// NULLs as distinct, so a nullable "this is the base" marker would permit two base rows per model.</summary>
    [Fact]
    public void ModelBase_is_zero() => Assert.Equal(0, (int)LlmCallGroup.ModelBase);

    /// <summary>Six real call groups plus the base scope. Pinned so that adding one is a deliberate act
    /// that also forces a look at the admin UI, which renders a panel per group.</summary>
    [Fact]
    public void Has_six_call_groups_plus_the_model_base_scope() =>
        Assert.Equal(7, Enum.GetValues<LlmCallGroup>().Length);
}
