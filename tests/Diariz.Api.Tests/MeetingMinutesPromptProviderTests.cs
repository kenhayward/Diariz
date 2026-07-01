using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class MeetingMinutesPromptProviderTests
{
    [Fact]
    public void GetTemplate_ReadsTheFile_WhenPresent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mm-prompt-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "MY CUSTOM TEMPLATE {meeting_title}");
        try
        {
            var provider = new FileMeetingMinutesPromptProvider(path);
            Assert.Equal("MY CUSTOM TEMPLATE {meeting_title}", provider.GetTemplate());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetTemplate_FallsBackToDefault_WhenFileMissing()
    {
        var provider = new FileMeetingMinutesPromptProvider(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.md"));

        Assert.Equal(MeetingMinutesPrompt.DefaultTemplate, provider.GetTemplate());
    }

    [Fact]
    public void GetTemplate_FallsBackToDefault_WhenFileEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mm-empty-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "   \n  ");
        try
        {
            Assert.Equal(MeetingMinutesPrompt.DefaultTemplate, new FileMeetingMinutesPromptProvider(path).GetTemplate());
        }
        finally { File.Delete(path); }
    }
}
