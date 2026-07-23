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

    /// <summary>One-line descriptions for each tag (section) in the published document. The OpenAPI generator tags
    /// every operation with its controller name minus <c>Controller</c>, so these keys are exactly those names, and
    /// Scalar renders the description under each section heading. Every included controller must have an entry
    /// (enforced by tests). Keep the copy free of em/en dashes per the user-facing-text convention.</summary>
    public static readonly IReadOnlyDictionary<string, string> TagDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Actions"] = "Every action item across your recordings as one flat list (the Actions tab), plus bulk complete or un-complete.",
        ["ApiTokens"] = "Create, list, and revoke your personal REST API tokens. The token value is shown once at creation; only its hash is stored.",
        ["Attachments"] = "Supporting documents (files or URLs) attached to a recording - list, add, rename, remove, and open their content.",
        ["Auth"] = "Sign in and obtain a session token for calling the rest of the API.",
        ["Calendar"] = "Read-only calendar events for the signed-in user, merging connected Google calendars and subscribed .ics feeds (the Calendar tab).",
        ["CalendarEventNotes"] = "Pre-meeting note lines attached to a calendar event, before any recording exists; adopted onto the recording once one is linked.",
        ["CalendarFeeds"] = "Your external iCalendar (.ics) feed subscriptions - list, add, update, and remove. Feed URLs are validated (https only) before they are stored.",
        ["Chat"] = "Multi-turn chat over your transcripts: stream answers over selected recordings and save, load, or delete conversations.",
        ["FormulaResults"] = "The Markdown documents a formula produced for a recording - list, read, edit, delete, email, and download.",
        ["Formulas"] = "Manage Formulas (a saved prompt plus context) and run one over a recording to generate a document.",
        ["Groups"] = "Administer user groups and their platform permissions. The built-in Platform Administrators group is protected from lockout.",
        ["Languages"] = "The list of supported interface languages (public reference data).",
        ["McpTokens"] = "Create, list, and revoke your MCP personal access tokens, used to connect Claude to your own transcripts. Shown once at creation.",
        ["MeetingNotes"] = "Your own timestamped note lines for a recording - list, add, edit, and remove; capture times are immutable.",
        ["MeetingTypes"] = "Meeting types (minutes templates): read the shared and your personal types; writes are permission-gated.",
        ["RecordingActions"] = "Action items for a single recording - extract them from the transcript, then list, add, edit, and remove.",
        ["RecordingTranslation"] = "Translate a recording's transcript, summary, and actions - or a single segment - into another language.",
        ["Recordings"] = "Your recordings: upload, list, view, rename, re-transcribe, summarise, move between folders, edit segments and speakers, export the transcript, and delete.",
        ["Rooms"] = "The shared rooms you belong to, and (with the right permission) creating, editing, and deleting rooms and their membership. Your Personal room is immutable here.",
        ["Screenshots"] = "Screen captures taken during a recording from the desktop app - list them and fetch the full image or thumbnail.",
        ["Search"] = "Search across your transcripts, returning structured hits with a snippet, the moment it occurs, and where the recording lives.",
        ["SectionAttachments"] = "Supporting documents attached directly to a folder (files, URLs, or Markdown) - list, add, rename, edit, and remove.",
        ["SectionFormulaResults"] = "Run a formula across a whole folder and manage the resulting documents. Access is gated by folder membership.",
        ["SectionPage"] = "A folder's detail page: aggregated stats, an LLM folder summary and minutes, and the actions, notes, and attachments across it and its sub-folders.",
        ["Sections"] = "Your folder tree (sections) - create, rename, reorder, and delete folders.",
        ["SpeakerProfiles"] = "Enrolled voiceprints - create a profile from a recording's speaker, then list, rename, merge, and erase them (GDPR).",
        ["Storage"] = "Your storage usage against your quota.",
        ["Tags"] = "Your aggregated tag cloud - every tag across your recordings with a count, summed weight, and the recordings behind each.",
        ["UserProfile"] = "Your own profile - display name and language preferences. Updating returns a fresh token so the new name applies immediately.",
        ["UserSettings"] = "Your AI settings - the OpenAI-compatible endpoint, model, and API key used for summaries and chat. The key is write-only.",
        ["Webhooks"] = "Your outbound webhook subscriptions (Automations) - create, edit, and delete them, send a test ping, and read the delivery log. Gated on the platform Automations toggle.",
        ["WorkflowSignals"] = "The admin-defined Workflow Signals a formula author can attach to a formula. Anyone can list the active signals; managing the vocabulary needs a platform administrator.",
    };

    /// <summary>Attaches <see cref="TagDescriptions"/> to the document's tag groups so each section in the reference
    /// carries a short explanation. Every mapped tag belongs to a controller that has operations in the document,
    /// so this never creates an empty section.</summary>
    public sealed class TagDescriptionsTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
        {
            document.Tags ??= new HashSet<OpenApiTag>();
            foreach (var (name, description) in TagDescriptions)
            {
                var tag = document.Tags.FirstOrDefault(t => t.Name == name);
                if (tag is null)
                {
                    tag = new OpenApiTag { Name = name };
                    document.Tags.Add(tag);
                }
                tag.Description = description;
            }
            return Task.CompletedTask;
        }
    }

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
