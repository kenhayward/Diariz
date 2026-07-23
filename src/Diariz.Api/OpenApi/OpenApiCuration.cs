using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Diariz.Api.OpenApi;

/// <summary>Curation for the published OpenAPI document: only user-facing REST endpoints are included
/// (everything under <c>api/</c> except the OAuth plumbing under <c>api/oauth</c>); the worker callbacks
/// (<c>internal/*</c>), the OAuth server (<c>connect/*</c>, <c>.well-known/*</c>) and <c>/mcp</c> are dropped
/// because they are not <c>api/</c> routes. Also declares the bearer auth so the reference's "Authorize"
/// works with a personal API token or the session JWT.</summary>
public static class OpenApiCuration
{
    /// <summary>Admin/OAuth prefixes dropped from the published document: the reference is for the user-facing
    /// REST API. (The admin surface also carries the only <c>TimeOnly</c> field, which the OpenAPI schema
    /// exporter can't map - excluding it keeps the document generatable as well as on-topic.)</summary>
    private static readonly string[] ExcludedPrefixes = ["api/oauth", "api/platform", "api/admin", "api/maintenance"];

    /// <summary>Landing copy for the reference. Scalar renders <c>Info.Description</c> as the intro panel above the
    /// endpoint list, so this is where users learn what the API is and how to authenticate. Markdown; keep it free of
    /// em/en dashes per the user-facing-text convention.</summary>
    private const string Description =
        """
        The Diariz REST API lets you work with your own recordings, transcriptions, summaries, speaker
        profiles, and more - the same data you see in the app, scoped to your account.

        **Authentication.** Send a bearer token in the `Authorization` header. Generate a personal API
        token (`dz_api_...`) from **Settings -> Developers** (when your platform admin has enabled
        API access), or use your current session token. Use **Authorize** above to try requests here.

        Every endpoint is scoped to the signed-in user - you can only read and change your own data.
        """;

    public static bool ShouldInclude(string? relativePath) =>
        relativePath is not null
        && relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
        && !ExcludedPrefixes.Any(p => relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds an HTTP bearer security scheme (a personal <c>dz_api_</c> token or the session JWT) and a
    /// global security requirement, so the reference UI can send an Authorization header.</summary>
    public sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            var scheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                Description = "A personal API token (dz_api_…) or the session JWT.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = scheme;
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
            document.Info.Title = "Diariz API";
            document.Info.Description = Description;
            return Task.CompletedTask;
        }
    }
}
