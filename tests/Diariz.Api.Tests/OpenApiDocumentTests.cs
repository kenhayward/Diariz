using System.Net;
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
}
