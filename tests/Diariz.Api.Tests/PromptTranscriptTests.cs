using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The "[Speaker]: [Text]" flattening every summary, minutes, actions and tags prompt reads.
/// Since the worker splits segments where the word-level speaker changes (#803), one person's turn can
/// arrive as several rows; each repeated "Name: " prefix spends the fixed character budget on nothing.</summary>
public class PromptTranscriptTests
{
    private static SegmentDto Seg(string label, string display, string text) =>
        new(Guid.NewGuid(), label, display, 0, 1000, text);

    [Fact]
    public void Build_JoinsConsecutiveRowsOfOneSpeakerIntoOneLine()
    {
        var text = PromptTranscript.Build([
            Seg("SPEAKER_00", "Alice", "We ship Friday."),
            Seg("SPEAKER_00", "Alice", "Unless the tests fail."),
            Seg("SPEAKER_01", "Bob", "They will not."),
        ]);

        Assert.Equal("Alice: We ship Friday. Unless the tests fail.\nBob: They will not.\n", text);
    }

    [Fact]
    public void Build_KeepsEachTurnWhenSpeakersAlternate()
    {
        var text = PromptTranscript.Build([
            Seg("SPEAKER_00", "Alice", "Ready?"),
            Seg("SPEAKER_01", "Bob", "Yes."),
            Seg("SPEAKER_00", "Alice", "Go."),
        ]);

        Assert.Equal("Alice: Ready?\nBob: Yes.\nAlice: Go.\n", text);
    }

    [Fact]
    public void Build_JoinsByTheNameTheModelSeesNotTheDiarizationLabel()
    {
        // Two labels named as the same person read as one speaker to the model, so they are one turn.
        var text = PromptTranscript.Build([
            Seg("SPEAKER_00", "Alice", "First."),
            Seg("SPEAKER_03", "Alice", "Second."),
        ]);

        Assert.Equal("Alice: First. Second.\n", text);
    }

    [Fact]
    public void Build_FitsMoreOfTheTranscriptIntoTheSameBudget()
    {
        var segments = Enumerable.Range(0, 10)
            .Select(i => Seg("SPEAKER_00", "Alice", $"w{i}."))
            .ToList();

        // One prefix instead of ten: everything fits, where the old flattening spent the budget on
        // "Alice: " and lost the tail.
        var text = PromptTranscript.Build(segments, charBudget: 50);

        Assert.Equal("Alice: w0. w1. w2. w3. w4. w5. w6. w7. w8. w9.\n", text);
    }

    [Fact]
    public void Build_StillCutsAtTheBudget()
    {
        var text = PromptTranscript.Build([
            Seg("SPEAKER_00", "Alice", new string('a', 30)),
            Seg("SPEAKER_01", "Bob", new string('b', 30)),
        ], charBudget: 20);

        Assert.Equal(20, text.Length);
        Assert.StartsWith("Alice: ", text);
    }

    [Fact]
    public void Build_OfNothingIsEmpty() =>
        Assert.Equal("", PromptTranscript.Build([]));
}
