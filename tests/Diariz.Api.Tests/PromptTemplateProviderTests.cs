using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class PromptTemplateProviderTests
{
    [Fact]
    public void Get_ReadsTheNamedFile_WhenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summarise.md"), "MY CUSTOM {output_shape}");
        try
        {
            var provider = new FilePromptTemplateProvider(dir);
            Assert.Equal("MY CUSTOM {output_shape}", provider.Get("summarise", "fallback"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Get_FallsBackToDefault_WhenFileMissing()
    {
        var provider = new FilePromptTemplateProvider(
            Path.Combine(Path.GetTempPath(), $"no-prompts-{Guid.NewGuid():N}"));

        Assert.Equal("the default", provider.Get("meeting-minutes", "the default"));
    }

    [Fact]
    public void Get_FallsBackToDefault_WhenFileEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extract-actions.md"), "   \n  ");
        try
        {
            Assert.Equal("fb", new FilePromptTemplateProvider(dir).Get("extract-actions", "fb"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
