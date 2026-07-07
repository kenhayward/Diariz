using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class NotesComposerTests
{
    private static readonly Guid RecId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MeetingNoteDto Note(int i, string text, long? ms = null) =>
        new(Guid.NewGuid(), text, ms, i, DateTimeOffset.UtcNow);

    [Fact]
    public void Render_BoldUserText_ItalicStamp_ExpansionAndDeepLinks()
    {
        var md = NotesComposer.Render(
            [Note(0, "Comp expectations", 61_000)],
            [new EnhancedNote(0, "Bob said comp must be market-rate.", [60_000, 125_000], false)],
            RecId);

        Assert.Contains("- **Comp expectations** *(1:01)* - Bob said comp must be market-rate.", md);
        Assert.Contains($"[1:00](/recordings/{RecId}?t=60000)", md);
        Assert.Contains($"[2:05](/recordings/{RecId}?t=125000)", md);
    }

    [Fact]
    public void Render_KeepsNotDiscussedLines_Marked()
    {
        var md = NotesComposer.Render(
            [Note(0, "ask about visas")],
            [new EnhancedNote(0, null, [], true)],
            RecId);

        Assert.Contains("- **ask about visas** - *not discussed in the recording*", md);
    }

    [Fact]
    public void Render_EscapesMarkdownInUserText_Verbatim()
    {
        var md = NotesComposer.Render(
            [Note(0, "check *pricing* [Q3]")],
            [new EnhancedNote(0, "Covered.", [], false)],
            RecId);

        Assert.Contains(@"**check \*pricing\* \[Q3\]**", md);
    }

    [Fact]
    public void Render_MissingEnhancement_FallsBackToNotDiscussed()
    {
        var md = NotesComposer.Render([Note(0, "a"), Note(1, "b")], [new EnhancedNote(0, "yes", [], false)], RecId);
        Assert.Contains("- **b** - *not discussed in the recording*", md);
    }

    [Fact]
    public void RenderRaw_ListsLinesWithStamps_NoExpansion()
    {
        var md = NotesComposer.RenderRaw([Note(0, "first", 5_000), Note(1, "second")]);
        Assert.Equal("- **first** *(0:05)*\n- **second**", md);
    }

    [Fact]
    public void EmptyNotes_RenderNoNotesSentence()
    {
        Assert.Equal(NotesComposer.NoNotes, NotesComposer.Render([], [], RecId));
        Assert.Equal(NotesComposer.NoNotes, NotesComposer.RenderRaw([]));
    }
}
