using System.Net;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Tests;

/// <summary>The published OpenAPI document backs the in-app API reference (/developers/api). If document
/// generation throws, that page renders blank. It is generatable only because the schema exporter is fragile:
/// a non-nullable value-type (Guid/TimeOnly/...) with a default value in an INCLUDED DTO crashes it and 500s
/// the whole document. This hosts the real controllers + curation in-process (no DB/infra) and asserts the
/// document generates - the guard that would have caught the RecordingDetailDto.RecordedByUserId regression.</summary>
public class OpenApiDocumentTests
{
    [Fact]
    public async Task PublishedDocument_GeneratesWithoutThrowing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(RecordingsController).Assembly) // the real Diariz.Api controllers
            .AddJsonOptions(o => JsonConfig.Apply(o.JsonSerializerOptions)); // same enum-as-string config as prod
        builder.Services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = desc => OpenApiCuration.ShouldInclude(desc.RelativePath);
            options.AddDocumentTransformer<OpenApiCuration.SecuritySchemeTransformer>();
        });

        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi("/api/openapi/{documentName}.json"); // generation only - no RequireAuthorization here
        await app.StartAsync();

        var res = await app.GetTestClient().GetAsync("/api/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // a schema-export crash surfaces as 500
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body);
        Assert.Contains("/api/recordings/{id}", body); // the endpoint whose response DTO regressed
    }

    /// <summary>Every section heading in the reference should carry a description. This generates the real document
    /// with the tag-description transformer and asserts that no operation is tagged with a section that lacks a
    /// description - i.e. every published controller's group is documented, end to end.</summary>
    [Fact]
    public async Task PublishedDocument_EveryTagGroupHasADescription()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(RecordingsController).Assembly)
            .AddJsonOptions(o => JsonConfig.Apply(o.JsonSerializerOptions));
        builder.Services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = desc => OpenApiCuration.ShouldInclude(desc.RelativePath);
            options.AddDocumentTransformer<OpenApiCuration.SecuritySchemeTransformer>();
            options.AddDocumentTransformer<OpenApiCuration.TagDescriptionsTransformer>();
        });

        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi("/api/openapi/{documentName}.json");
        await app.StartAsync();

        var body = await app.GetTestClient().GetStringAsync("/api/openapi/v1.json");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Map of tag name -> whether it has a non-empty description, from the top-level "tags" array.
        var described = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (root.TryGetProperty("tags", out var tags))
        {
            foreach (var t in tags.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString()!;
                described[name] = t.TryGetProperty("description", out var d)
                    && !string.IsNullOrWhiteSpace(d.GetString());
            }
        }

        // Every tag actually used by an operation must resolve to a described top-level tag.
        var usedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (op.Value.ValueKind != JsonValueKind.Object) continue; // skip "parameters" etc.
                if (!op.Value.TryGetProperty("tags", out var opTags)) continue;
                foreach (var tag in opTags.EnumerateArray()) usedTags.Add(tag.GetString()!);
            }
        }

        Assert.NotEmpty(usedTags);
        var undescribed = usedTags.Where(t => !described.TryGetValue(t, out var ok) || !ok).ToList();
        Assert.True(undescribed.Count == 0, $"Tag sections without a description: {string.Join(", ", undescribed)}");
    }
}
