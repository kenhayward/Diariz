using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The folder chat-context block: its roll-up summary, minutes and aggregated actions, with clear
/// placeholders when a roll-up hasn't been generated yet.</summary>
public class ChatFolderContextTests
{
    [Fact]
    public void Includes_summary_minutes_and_actions()
    {
        var text = ChatFolderContext.BuildText("The folder theme.", "# Minutes\nDecisions.", "Action items:\n- Ship it");

        Assert.Contains("The folder theme.", text);
        Assert.Contains("Decisions.", text);
        Assert.Contains("Ship it", text);
        Assert.Contains("Folder summary:", text);
        Assert.Contains("Folder minutes:", text);
    }

    [Fact]
    public void Uses_placeholders_when_rollups_are_missing()
    {
        var text = ChatFolderContext.BuildText(null, "  ", actionsText: "");

        Assert.Contains("no folder summary generated yet", text);
        Assert.Contains("no folder minutes generated yet", text);
    }

    [Fact]
    public void Omits_the_actions_block_when_there_are_none()
    {
        var text = ChatFolderContext.BuildText("s", "m", "");
        Assert.DoesNotContain("Action items", text);
    }
}
