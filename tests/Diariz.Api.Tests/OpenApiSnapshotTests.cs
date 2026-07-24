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

/// <summary>Keeps the OpenAPI snapshot the n8n community node generates from in step with the real published
/// document. The node's long tail of operations is generated from this file, so an endpoint added, renamed or
/// removed here must be regenerated into the node rather than silently drifting.
/// The fix for a failure is to commit the regenerated snapshot and run <c>npm run generate</c> in
/// <c>integrations/n8n-nodes-diariz</c>, never to edit the snapshot by hand.</summary>
public class OpenApiSnapshotTests
{
    private static readonly string SnapshotPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "integrations", "n8n-nodes-diariz", "nodes", "Diariz", "generated", "openapi.snapshot.json");

    [Fact]
    public async Task Snapshot_MatchesTheCurrentDocument()
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

        // Re-serialise indented so the committed file diffs readably and stably.
        using var doc = JsonDocument.Parse(body);
        var normalized = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        var full = Path.GetFullPath(SnapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var current = File.Exists(full) ? File.ReadAllText(full).ReplaceLineEndings("\n") : null;
        if (current != normalized.ReplaceLineEndings("\n"))
        {
            File.WriteAllText(full, normalized);
            Assert.Fail(
                $"OpenAPI snapshot regenerated at {full}. Run 'npm run generate' in " +
                "integrations/n8n-nodes-diariz, then commit both files.");
        }
    }
}
