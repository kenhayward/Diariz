using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class GoogleCalendarSelectionTests
{
    private static CalendarListEntry Cal(string id, bool selected = false, bool primary = false) =>
        new(id, id, "#fff", "#000", selected, primary);

    private static readonly IReadOnlyList<CalendarListEntry> All =
    [
        Cal("primary", selected: false, primary: true),
        Cal("shown", selected: true),
        Cal("hidden", selected: false),
    ];

    [Fact]
    public void ApplySelection_NullSelection_KeepsSelectedOrPrimary()
    {
        var kept = GoogleCalendarClient.ApplySelection(All, null).Select(c => c.Id).ToHashSet();

        Assert.Contains("primary", kept); // primary always kept
        Assert.Contains("shown", kept);   // Google-visible kept
        Assert.DoesNotContain("hidden", kept);
    }

    [Fact]
    public void ApplySelection_ExplicitSet_KeepsExactlyThoseIds_IgnoringGoogleFlags()
    {
        var selection = new HashSet<string> { "hidden" }; // deselected in Google, but user chose it

        var kept = GoogleCalendarClient.ApplySelection(All, selection).Select(c => c.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "hidden" }, kept); // primary NOT auto-included when a selection exists
    }

    [Fact]
    public void ApplySelection_EmptySet_KeepsNone()
    {
        Assert.Empty(GoogleCalendarClient.ApplySelection(All, new HashSet<string>()));
    }
}
