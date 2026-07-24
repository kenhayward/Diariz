using System.Reflection;
using Diariz.Api.Controllers;
using Diariz.Api.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Diariz.Api.Tests;

public class OpenApiCurationTests
{
    [Theory]
    [InlineData("api/recordings", true)]
    [InlineData("api/user/api-tokens", true)]
    [InlineData("api/actions", true)]
    [InlineData("api/platform/settings", false)] // admin surface excluded (also the only TimeOnly carrier)
    [InlineData("api/admin/users", false)]
    [InlineData("api/maintenance/backup", false)]
    [InlineData("api/oauth/connections", false)]  // OAuth plumbing excluded
    [InlineData("api/oauth", false)]
    [InlineData("internal/transcriptions/result", false)]
    [InlineData("connect/register", false)]
    [InlineData(".well-known/oauth-protected-resource", false)]
    [InlineData(null, false)]
    public void ShouldInclude_KeepsUserApiOnly(string? relativePath, bool expected) =>
        Assert.Equal(expected, OpenApiCuration.ShouldInclude(relativePath));

    /// <summary>The tag descriptions in the reference are keyed by controller name (minus "Controller"), which is the
    /// tag the OpenAPI generator assigns. This enumerates every controller that lands in the published document and
    /// asserts each has a non-empty description - so adding a new user-facing controller without documenting its
    /// section fails here rather than shipping a blank heading.</summary>
    public static IEnumerable<object[]> IncludedControllerTags()
    {
        foreach (var type in typeof(RecordingsController).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type)) continue;
            var route = type.GetCustomAttribute<RouteAttribute>()?.Template;
            if (!OpenApiCuration.ShouldInclude(route)) continue; // only the published surface
            yield return [type.Name[..^"Controller".Length]];
        }
    }

    [Theory]
    [MemberData(nameof(IncludedControllerTags))]
    public void TagDescriptions_CoversEveryIncludedController(string tag)
    {
        Assert.True(
            OpenApiCuration.TagDescriptions.TryGetValue(tag, out var description) && !string.IsNullOrWhiteSpace(description),
            $"No OpenApiCuration.TagDescriptions entry for the '{tag}' section.");
    }

    [Fact]
    public async Task TagDescriptionsTransformer_SetsDescriptionOnMatchingTag()
    {
        var document = new OpenApiDocument { Tags = new HashSet<OpenApiTag> { new() { Name = "Actions" } } };

        await new OpenApiCuration.TagDescriptionsTransformer().TransformAsync(document, null!, CancellationToken.None);

        var actions = document.Tags!.Single(t => t.Name == "Actions");
        Assert.False(string.IsNullOrWhiteSpace(actions.Description));
    }

    [Fact]
    public async Task SecuritySchemeTransformer_SetsInfoTitleAndDescription()
    {
        var document = new OpenApiDocument { Info = new OpenApiInfo() };

        await new OpenApiCuration.SecuritySchemeTransformer().TransformAsync(document, null!, CancellationToken.None);

        Assert.Equal("Diariz API", document.Info.Title);
        // The reference's landing panel (Scalar renders Info.Description) tells users what the API is and how
        // to authenticate, so it must be populated and mention the personal API token.
        Assert.False(string.IsNullOrWhiteSpace(document.Info.Description));
        Assert.Contains("dz_api_", document.Info.Description!);
    }
}
