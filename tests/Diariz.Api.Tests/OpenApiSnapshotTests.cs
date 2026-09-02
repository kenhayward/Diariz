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
/// <c>integrations/n8n-nodes-diariz</c>, never to edit the snapshot by hand.
///
/// <para><b>This test repairs the thing it checks, which makes a real failure look like a flake.</b> On a
/// mismatch it rewrites the snapshot and then fails, so the very next run passes with nothing changed by
/// hand. That is the whole of issue #482: "exactly one test fails, re-running immediately passes" was
/// filed as an unidentified intermittent fault and hunted across 100+ instrumented runs, all of which ran
/// against an unchanged tree - where this test cannot fail, because the document and the snapshot are
/// identical by construction. It only ever fires when the API surface has moved since the snapshot was
/// committed, which is why it was only ever seen mid-session and never on CI. The failure message says so
/// at length; do not shorten it back into something that reads as noise.</para></summary>
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
        var normalized = Canonicalize(body);

        var full = Path.GetFullPath(SnapshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var current = File.Exists(full) ? File.ReadAllText(full).ReplaceLineEndings("\n") : null;
        var expected = normalized.ReplaceLineEndings("\n");
        if (current != expected)
        {
            File.WriteAllText(full, normalized);
            var nl = Environment.NewLine;
            Assert.Fail(
                $"The published API no longer matches the committed OpenAPI snapshot.{nl}{nl}" +
                $"THIS IS EXPECTED IF YOU HAVE JUST CHANGED THE API, AND IT IS NOT A FLAKY TEST.{nl}" +
                $"The snapshot has been REWRITTEN in your working tree, so re-running now passes. That pass " +
                $"means the file was regenerated, NOT that this failure was spurious - see issue #482, where " +
                $"exactly that fail-then-pass was hunted for weeks as an intermittent fault.{nl}{nl}" +
                $"To finish the job:{nl}" +
                $"  1. run 'npm run generate' in integrations/n8n-nodes-diariz{nl}" +
                $"  2. commit BOTH the regenerated snapshot and generated/index.ts{nl}{nl}" +
                $"Regenerated at {full}{nl}" +
                FirstDifference(current, expected));
        }
    }

    /// <summary>Serialises the document with every object's keys sorted and the root tag list ordered by name,
    /// so the snapshot depends only on the API's surface. Without this the guard is flaky rather than useful:
    /// the document's tag collection is a HashSet and operation discovery order is not contractually stable
    /// across platforms, so an unchanged API could still produce a byte-different file on another machine.
    /// Arrays are otherwise left alone - parameter order is meaningful.</summary>
    private static string Canonicalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            Write(writer, doc.RootElement, propertyName: null);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                // "tags" is a set, not a sequence, and its source collection has no ordering guarantee.
                var items = propertyName == "tags"
                    ? element.EnumerateArray().OrderBy(TagKey, StringComparer.Ordinal)
                    : element.EnumerateArray().AsEnumerable();
                foreach (var item in items) Write(writer, item, propertyName: null);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                // Descriptions come from C# raw string literals, so their newlines are whatever line endings
                // the source file was checked out with - CRLF on Windows, LF on Linux. Those land escaped
                // INSIDE the JSON string, where normalising the file's own line endings never reaches them.
                // Left alone, the snapshot differs by platform and the drift guard fires on every CI run.
                writer.WriteStringValue(element.GetString()?.Replace("\r\n", "\n").Replace("\r", "\n"));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string TagKey(JsonElement tag) =>
        tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("name", out var name)
            ? name.GetString() ?? ""
            : tag.ToString();

    /// <summary>The first differing line, so a CI failure says what moved instead of only that something did.</summary>
    private static string FirstDifference(string? current, string expected)
    {
        if (current is null) return "The snapshot did not exist yet.";
        var a = current.Split('\n');
        var b = expected.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : "<end of file>";
            var right = i < b.Length ? b[i] : "<end of file>";
            if (left == right) continue;
            return $"First difference at line {i + 1}:{Environment.NewLine}  committed: {left.Trim()}{Environment.NewLine}  current:   {right.Trim()}";
        }
        return "The files differ only in trailing content.";
    }
}
