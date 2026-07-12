using System.Text;
using Amazon.S3;
using Amazon.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- Options ----
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection(GoogleAuthOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<JobQueueOptions>(builder.Configuration.GetSection(JobQueueOptions.Section));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
builder.Services.Configure<SummarizationOptions>(builder.Configuration.GetSection(SummarizationOptions.Section));
builder.Services.Configure<MeetingMinutesOptions>(builder.Configuration.GetSection(MeetingMinutesOptions.Section));
builder.Services.Configure<ActionsOptions>(builder.Configuration.GetSection(ActionsOptions.Section));
builder.Services.Configure<TagsOptions>(builder.Configuration.GetSection(TagsOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<SectionSummaryOptions>(builder.Configuration.GetSection(SectionSummaryOptions.Section));
builder.Services.Configure<SectionMinutesOptions>(builder.Configuration.GetSection(SectionMinutesOptions.Section));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.Section));
builder.Services.Configure<DictationOptions>(builder.Configuration.GetSection(DictationOptions.Section));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<AppPublicOptions>(builder.Configuration.GetSection(AppPublicOptions.Section));
builder.Services.Configure<IdentificationOptions>(builder.Configuration.GetSection(IdentificationOptions.Section));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.Section));
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection(AttachmentOptions.Section));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.Section));
builder.Services.Configure<McpOAuthOptions>(builder.Configuration.GetSection(McpOAuthOptions.Section));

// Honour X-Forwarded-* from the reverse proxy (nginx/TLS terminator) so Request.Scheme/IsHttps reflect the
// browser's HTTPS — needed for the OAuth state cookie's Secure flag and any request-derived URLs. The proxy
// is on the container network, so clear the default trusted-proxy allowlist to trust it.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
var storage = builder.Configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
var queue = builder.Configuration.GetSection(JobQueueOptions.Section).Get<JobQueueOptions>() ?? new JobQueueOptions();

// ---- Database ----
builder.Services.AddDbContext<DiarizDbContext>(opt =>
    opt.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres"),
        o => o.UseVector()));

// ---- Identity ----
builder.Services.AddIdentityCore<ApplicationUser>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Password.RequireNonAlphanumeric = true;
        o.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<DiarizDbContext>()
    .AddDefaultTokenProviders(); // one-time account-setup token

// ---- Auth (JWT + personal API key) ----
// The default authenticate scheme is a forwarding policy scheme: a `Bearer dz_api_…` personal API token
// routes to the ApiKey handler, everything else (a JWT bearer, or no Authorization header for the
// SignalR/audio/backup query-string flows) routes to JWT. This makes a personal API key satisfy every
// [Authorize] variant - including the permission policies, which resolve the caller's group membership from
// the NameIdentifier claim that both schemes emit. Named schemes (e.g. the Mcp policy on /mcp) select their
// own scheme and are unaffected.
const string SmartAuthScheme = "smart";
builder.Services.AddAuthentication(SmartAuthScheme)
    .AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, o =>
    {
        o.ForwardDefaultSelector = ctx =>
        {
            string auth = ctx.Request.Headers.Authorization!;
            return !string.IsNullOrEmpty(auth)
                   && auth.StartsWith("Bearer " + Diariz.Api.Services.ApiTokenService.TokenPrefix, StringComparison.Ordinal)
                ? Diariz.Api.Auth.ApiKeyAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
        };
        // Allow SignalR to authenticate via access_token query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Allow query-string auth for connections that can't set an Authorization header:
                // the SignalR WS handshake, the <audio> element streaming a recording, and opening an
                // attachment in a new browser tab.
                var token = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                var isRecordingAsset = path.StartsWithSegments("/api/recordings")
                    && (path.Value!.EndsWith("/audio", StringComparison.OrdinalIgnoreCase)
                        || path.Value!.EndsWith("/content", StringComparison.OrdinalIgnoreCase));
                // Folder-direct attachment files open in a new tab too (same reason as recording /content).
                var isSectionAsset = path.StartsWithSegments("/api/sections")
                    && path.Value!.EndsWith("/content", StringComparison.OrdinalIgnoreCase);
                // The platform backup is downloaded via an anchor href (can't set an Authorization header).
                var isBackup = path.StartsWithSegments("/api/maintenance")
                    && path.Value!.EndsWith("/backup", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(token)
                    && (path.StartsWithSegments("/hubs") || isRecordingAsset || isSectionAsset || isBackup))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    })
    // MCP personal-access-token scheme, used only by the /mcp endpoint (a bearer token pasted into Claude's
    // MCP config). Independent of the JWT session.
    .AddScheme<Diariz.Api.Auth.McpAuthSchemeOptions, Diariz.Api.Auth.McpBearerAuthenticationHandler>(
        Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName, _ => { })
    // Personal REST-API token scheme (dz_api_…), routed here by the forwarding default selector above.
    .AddScheme<Diariz.Api.Auth.ApiKeyAuthSchemeOptions, Diariz.Api.Auth.ApiKeyAuthenticationHandler>(
        Diariz.Api.Auth.ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o =>
{
    // Platform authority, resolved from the caller's group membership. Each policy requires ANY of the flags
    // it names.
    o.AddPolicy("ManageRooms", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageRooms)));
    o.AddPolicy("ManageUsers", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageUsers)));
    o.AddPolicy("ManagePlatform", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManagePlatform)));
    o.AddPolicy("ManageFormulas", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageFormulas)));
    // Reading platform settings: the Manage Users modal shows the default quota, so an Administrator
    // (ManageUsers, no ManagePlatform) must still be able to GET them. Writes remain ManagePlatform.
    o.AddPolicy("ReadAdminSettings", p => p.AddRequirements(
        new PermissionRequirement(PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform)));

    // The /mcp endpoint authenticates only with the MCP token scheme (not the browser's JWT).
    o.AddPolicy(Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName, p =>
    {
        p.AddAuthenticationSchemes(Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName);
        p.RequireAuthenticatedUser();
    });
});

// ---- Storage (MinIO / S3) ----
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var cfg = new AmazonS3Config
    {
        ServiceURL = storage.Endpoint,
        ForcePathStyle = storage.ForcePathStyle,
        AuthenticationRegion = "us-east-1",
        UseHttp = storage.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    };
    return new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), cfg);
});
builder.Services.AddSingleton<IAudioStorage, AudioStorage>();

// ---- Platform backup/restore (shells out to pg_dump/pg_restore — installed in the API image) ----
builder.Services.AddSingleton<IDatabaseBackup>(_ =>
    new PgToolsDatabaseBackup(builder.Configuration.GetConnectionString("Postgres")!));
builder.Services.AddScoped<ISchemaVersion, EfSchemaVersion>();
// Platform authority, resolved from the caller's group membership on every request (never from a JWT claim).
builder.Services.AddScoped<IUserPermissions, UserPermissions>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
// Rooms. Nothing consumes this yet - phases 2b-2d wire it into the controllers.
builder.Services.AddScoped<IRoomScope, RoomScope>();

// ---- Redis job queue ----
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(queue.RedisConnection));
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddSingleton<IDesktopAuthCodeStore, RedisDesktopAuthCodeStore>();

// ---- Data Protection (encrypts user-supplied API keys at rest) ----
// In Docker, point DataProtection:KeysPath at a mounted volume so the keyring survives container
// recreates — otherwise stored API keys become undecryptable. Locally the default per-user keyring is fine.
var dpKeys = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection();
if (!string.IsNullOrWhiteSpace(dpKeys))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeys));
builder.Services.AddSingleton<IApiKeyProtector, ApiKeyProtector>();

// ---- OAuth 2.1 authorization server for the MCP web connector (OpenIddict) ----
// Makes the API a spec-compliant OAuth 2.1 AS so claude.ai (and Desktop/Code) can add Diariz as a remote
// connector via a browser consent handshake, alongside the existing static dz_mcp_ token. This registers only
// the authorization server + its endpoints/keys; the interactive authorize/consent + token controllers and the
// /mcp resource-server validation are wired in later PRs.
var mcpOAuth = builder.Configuration.GetSection(McpOAuthOptions.Section).Get<McpOAuthOptions>() ?? new McpOAuthOptions();
if (mcpOAuth.Enabled)
{
    var appPublic = builder.Configuration.GetSection(AppPublicOptions.Section).Get<AppPublicOptions>() ?? new AppPublicOptions();
    var issuer = !string.IsNullOrWhiteSpace(mcpOAuth.Issuer) ? mcpOAuth.Issuer : appPublic.PublicUrl;
    // Persist the token signing/encryption certs next to the Data Protection keyring (same mounted volume), so
    // issued tokens survive a container recreate. With no keys volume (local dev) fall back to ephemeral certs.
    var oidcKeysDir = !string.IsNullOrWhiteSpace(mcpOAuth.KeysPath) ? mcpOAuth.KeysPath : dpKeys;

    // The canonical MCP resource identifier (token audience + protected-resource metadata), shared so token
    // issuance, validation, and the metadata document all agree.
    var mcpResource = OAuthResource.Resolve(issuer, overrideValue: null);
    builder.Services.AddSingleton(new McpResourceIdentifier(mcpResource));

    builder.Services.AddDiarizMcpOAuth(mcpOAuth, issuer, oidcKeysDir, builder.Environment.IsDevelopment(), mcpResource);
    // Bridges the SPA JWT consent screen to the browser-redirect /connect/authorize step (encrypted cookie).
    builder.Services.AddSingleton<IOAuthConsentTicketProtector, OAuthConsentTicketProtector>();
}

// ---- Email (account-setup link; no-op fallback when SMTP unconfigured) ----
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// ---- Speaker identification (match recording speakers to enrolled voiceprints) ----
builder.Services.AddScoped<ISpeakerIdentifier, SpeakerIdentifier>();

// ---- Summarisation (OpenAI-compatible endpoint + background consumer) ----
// Every LLM client disables HttpClient's own 100s timeout so the per-request timeout (the admin-set
// PlatformSettings.LlmTimeoutSeconds, applied via a linked CTS in each client) is the single authority -
// otherwise a configured timeout above 100s was silently capped and slow local models timed out.
static void NoHttpTimeout(HttpClient c) => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
builder.Services.AddHttpClient<ISummarizationClient, SummarizationClient>(NoHttpTimeout);
builder.Services.AddHttpClient<IDictationClient, DictationClient>(NoHttpTimeout);
builder.Services.AddHttpClient<IActionsClient, ActionsClient>(NoHttpTimeout);
builder.Services.AddHttpClient<ITranslationClient, TranslationClient>(NoHttpTimeout);
builder.Services.AddScoped<ISummarizationSettingsResolver, SummarizationSettingsResolver>();
builder.Services.AddHostedService<SummarizationWorker>();
builder.Services.AddScoped<IFormulaRunner, FormulaRunner>();

// ---- Meeting minutes (shares the per-user summarisation config; its own stream + consumer) ----
builder.Services.AddHttpClient<IMeetingMinutesClient, MeetingMinutesClient>(NoHttpTimeout);
// Template-driven generation: two strategies (per-section vs single-call), chosen per run by the platform mode.
builder.Services.AddScoped<IMeetingTypeMinutesStrategy, PerSectionMinutesStrategy>();
builder.Services.AddScoped<IMeetingTypeMinutesStrategy, SingleCallMinutesStrategy>();
builder.Services.AddScoped<IMeetingTypeMinutesGenerator, MeetingTypeMinutesGenerator>();
builder.Services.AddHostedService<MeetingMinutesWorker>();
// Folder-level (section) roll-ups: their own streams/workers, reusing the per-user summarisation config +
// the arbitrary-prompt IMeetingMinutesClient to combine the included recordings' summaries/minutes.
builder.Services.AddHostedService<SectionSummaryWorker>();
builder.Services.AddHostedService<SectionMinutesWorker>();
// Action extraction also runs in the pipeline (its own stream/worker), reusing IActionsClient (registered above).
builder.Services.AddHostedService<ActionsWorker>();
// Tag-cloud extraction runs in the pipeline too (its own stream/worker), sharing the per-user summarisation
// config; TagBackfillService enqueues jobs once at startup for recordings that predate the feature.
builder.Services.AddHttpClient<ITagsClient, TagsClient>(NoHttpTimeout);
builder.Services.AddHostedService<TagsWorker>();
builder.Services.AddHostedService<TagBackfillService>();

// ---- Semantic-search (RAG, M3) embeddings: its own endpoint/model config, stream + consumer, and a
// one-time startup backfill that indexes the existing library once an embeddings endpoint is configured. ----
builder.Services.AddHttpClient<IEmbeddingClient, EmbeddingClient>(NoHttpTimeout);
builder.Services.AddScoped<IEmbeddingSettingsResolver, EmbeddingSettingsResolver>();
builder.Services.AddHostedService<EmbeddingWorker>();
builder.Services.AddHostedService<EmbeddingBackfillService>();

// ---- Editable LLM prompt templates (summarise / extract-actions / meeting-minutes) ----
// Prefer the content root's prompts/ (dev + published output), else the app base dir. Read per use so edits
// apply without an API restart; a missing file falls back to the built-in default in code.
var promptsDir = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "prompts"))
    ? Path.Combine(builder.Environment.ContentRootPath, "prompts")
    : Path.Combine(AppContext.BaseDirectory, "prompts");
builder.Services.AddSingleton<IPromptTemplateProvider>(_ => new FilePromptTemplateProvider(promptsDir));

// ---- Localized export/email labels (runtime JSON, not compiled .resx) ----
// Prefer the content root's locales/ (present in dev and copied to the published output), falling back to
// the app base directory.
var exportLocalesRoot = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "locales"))
    ? Path.Combine(builder.Environment.ContentRootPath, "locales")
    : Path.Combine(AppContext.BaseDirectory, "locales");
builder.Services.AddSingleton<IExportLocalizer>(_ => new JsonExportLocalizer(exportLocalesRoot));

// ---- Chat (streaming, reuses the per-user summarisation LLM config) ----
builder.Services.AddHttpClient<IChatStreamClient, ChatStreamClient>();
builder.Services.AddScoped<IChatContextResolver, ChatContextResolver>();
builder.Services.AddSingleton<IAttachmentExtractor, AttachmentExtractor>();
// URL-attachment fetcher: a named client with auto-redirect OFF so each hop is re-checked against the
// SSRF guard (see UrlFetcher).
builder.Services.AddHttpClient("url-attachments")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IUrlFetcher, UrlFetcher>();

// ---- Chat tool calling (built-in transcript tools) ----
builder.Services.AddScoped<ITranscriptSearch, TranscriptSearch>();
builder.Services.AddScoped<IChatTool, WhoSaidThatTool>();
builder.Services.AddScoped<IChatTool, WhatDidTheySayTool>();
builder.Services.AddScoped<IChatTool, ListRecordingsTool>();
builder.Services.AddScoped<IChatTool, SearchTranscriptsTool>();
builder.Services.AddScoped<IChatTool, WhenWasDiscussedTool>();
builder.Services.AddScoped<IChatTool, CountMentionsTool>();
builder.Services.AddScoped<IChatTool, ListActionItemsTool>();
builder.Services.AddScoped<IChatTool, GetRecordingSummaryTool>();
builder.Services.AddScoped<IChatTool, WhoAttendedTool>();
builder.Services.AddScoped<IChatTool, SpeakerTalkTimeTool>();
builder.Services.AddScoped<IChatTool, GetSegmentContextTool>();
// Single-recording read tools (also exposed over MCP): full transcript, minutes, and metadata.
builder.Services.AddScoped<IChatTool, GetTranscriptTool>();
builder.Services.AddScoped<IChatTool, GetMeetingMinutesTool>();
builder.Services.AddScoped<IChatTool, GetRecordingDetailsTool>();
// A write tool (emails the signed-in user their own address). On by default like the others; a user or
// operator can turn it off in Settings / Chat:DisabledTools.
builder.Services.AddScoped<IChatTool, SendEmailTool>();
// A write tool that saves prepared content to a transcript as a Markdown attachment (client resolves which).
builder.Services.AddScoped<IChatTool, AddAsAttachmentTool>();
builder.Services.AddScoped<IChatToolRegistry, ChatToolRegistry>();
builder.Services.AddScoped<IChatToolSettingsResolver, ChatToolSettingsResolver>();
builder.Services.AddScoped<IChatToolOrchestrator, ChatToolOrchestrator>();

// ---- Google sign-in + data access (server-side OAuth; inert unless GoogleAuth is configured) ----
builder.Services.AddHttpClient<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IGoogleSignInHandler, GoogleSignInHandler>();
builder.Services.AddSingleton<IGoogleTokenProtector, GoogleTokenProtector>();
builder.Services.AddScoped<IGoogleTokenProvider, GoogleTokenProvider>();
builder.Services.AddScoped<IGoogleCalendarSelectionStore, GoogleCalendarSelectionStore>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IGoogleCalendarClient, GoogleCalendarClient>();

// ---- External .ics calendar feeds (fetched behind an SSRF guard; auto-redirect OFF so each hop is
// re-checked against the resolved-IP allow-list, like the URL-attachment fetcher above). ----
builder.Services.AddHttpClient(IcsCalendarClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IIcsCalendarClient, IcsCalendarClient>();

// ---- MCP server (in-process Streamable-HTTP /mcp endpoint; per-user token auth) ----
// The token services back both the management controller (JWT) and the /mcp bearer scheme.
builder.Services.AddSingleton<IMcpTokenService, McpTokenService>();
builder.Services.AddScoped<IMcpTokenAuthenticator, McpTokenAuthenticator>();
builder.Services.AddSingleton<IApiTokenService, ApiTokenService>();
builder.Services.AddScoped<IApiTokenAuthenticator, ApiTokenAuthenticator>();
builder.Services.AddScoped<IMcpResourceService, McpResourceService>();
var mcpOptions = builder.Configuration.GetSection(McpOptions.Section).Get<McpOptions>() ?? new McpOptions();
if (mcpOptions.Enabled)
{
    builder.Services.AddHttpContextAccessor();
    // The handler reads the current request's user/services via IHttpContextAccessor (its backing store is a
    // static AsyncLocal, so a plain instance captured here sees the live request). MapMcp runs it in-pipeline.
    var mcpHandlers = new Diariz.Api.Mcp.DiarizMcpHandlers(new HttpContextAccessor());
    var mcpVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    // Shown to the user by MCP clients (the connector card / description banner) and to the model. Plain text,
    // no fancy dashes.
    const string mcpDescription =
        "Search and read your own Diariz meeting transcripts. Find who said what, when a topic came up, and how "
        + "long each person spoke; get a recording's summary, meeting minutes, action items, and attendees; and "
        + "email yourself a summary. Everything is scoped to your account.";
    const string mcpInstructions =
        "This server connects to the signed-in user's private Diariz meeting-transcription account. Use its "
        + "tools to answer questions about the user's OWN recordings: search transcripts, find who said a phrase "
        + "or when a topic was discussed, count mentions, list recordings, and read a recording's full "
        + "transcript, summary, meeting minutes, action items, attendees, or per-speaker talk time. Tool results "
        + "include markdown links back to the exact moment in a transcript - keep them so the user can click "
        + "through. The server can also email the user a message it composes, but only ever to their own "
        + "registered address. All access is scoped to the signed-in user; there is no access to other users' "
        + "data. Prefer these tools over guessing whenever the user asks about their meetings.";
    // The connector card icon points at the web app's logo (served at {PublicUrl}/logo.png). Only advertise it
    // when the public origin resolves to a reachable absolute URL; otherwise omit it (the client shows no icon).
    //
    // NOTE (claude.ai, verified Jul 2026): claude.ai's *custom* connector card does NOT render serverInfo.Icons
    // or serverInfo.Description at all - it shows a generic globe + the connector URL (see anthropics/claude-ai-mcp
    // #152, still open, and anthropics/claude-code #44675, closed-not-planned). First-party/directory connectors get
    // their icon+description from Anthropic's curated directory, not from this handshake. The one field that DOES
    // take effect is ServerInstructions below (it steers Claude's tool use even though it's never shown in the UI).
    //
    // We keep Icons/Description anyway: they're spec-correct (MCP 2025-11-25 / SEP-973) and light up automatically
    // if claude.ai ever honors them. The *only* thing that brands a custom connector today is a favicon trick:
    // claude.ai fetches https://www.google.com/s2/favicons?domain=<eTLD+1 of the connector URL>, so the icon is the
    // REGISTRABLE domain's apex favicon (shared across all its subdomains), not serverInfo.Icons and not a subdomain
    // favicon. On this deployment the connector lives under stocks-hayward.com, whose apex serves no favicon, so we
    // get the globe. When Diariz moves to its own domain (e.g. diariz.app), serve the Diariz logo as that domain's
    // APEX favicon and host /mcp under it - then the mark appears on the next (fresh-hostname) connector add.
    var mcpPublicUrl = (builder.Configuration.GetSection(AppPublicOptions.Section).Get<AppPublicOptions>()?.PublicUrl ?? "").TrimEnd('/');
    var mcpIcons = Uri.TryCreate($"{mcpPublicUrl}/logo.png", UriKind.Absolute, out var logoUri) && logoUri.Scheme is "http" or "https"
        ? new List<ModelContextProtocol.Protocol.Icon> { new() { Source = logoUri.ToString(), MimeType = "image/png", Sizes = ["616x616"] } }
        : null;
    builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "Diariz",
                Title = "Diariz - Meeting Transcripts",
                Version = mcpVersion,
                Description = mcpDescription,
                // TODO: swap to the marketing site once it exists; GitHub for now.
                WebsiteUrl = "https://github.com/kenhayward/Diariz",
                Icons = mcpIcons,
            };
            o.ServerInstructions = mcpInstructions;
            o.Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
            {
                Tools = new ModelContextProtocol.Protocol.ToolsCapability(),
                Resources = new ModelContextProtocol.Protocol.ResourcesCapability(),
                Prompts = new ModelContextProtocol.Protocol.PromptsCapability(),
            };
            o.Handlers.ListToolsHandler = mcpHandlers.ListToolsAsync;
            o.Handlers.CallToolHandler = mcpHandlers.CallToolAsync;
            o.Handlers.ListResourcesHandler = mcpHandlers.ListResourcesAsync;
            o.Handlers.ReadResourceHandler = mcpHandlers.ReadResourceAsync;
            o.Handlers.ListPromptsHandler = mcpHandlers.ListPromptsAsync;
            o.Handlers.GetPromptHandler = mcpHandlers.GetPromptAsync;
        })
        // Stateless: no server-initiated messages, so each POST is self-contained (no session id / SSE stream).
        .WithHttpTransport(t => t.Stateless = true);
}

// ---- App services ----
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPlatformSettingsService, PlatformSettingsService>();
builder.Services.AddScoped<IStorageUsage, StorageUsage>();
// One-time backfill of legacy recordings' SizeBytes (HEAD each blob), runs once after startup.
builder.Services.AddHostedService<StorageBackfillService>();
// Nightly audio-retention job: deletes audio blobs of old, transcribed, unprotected recordings when the
// Platform Administrator has opted in (off by default). Runs at the configured server-local time of day.
builder.Services.AddHostedService<AudioRetentionWorker>();

builder.Services.AddControllers()
    .AddJsonOptions(o => JsonConfig.Apply(o.JsonSerializerOptions));
builder.Services.AddSignalR();
// Publish a curated OpenAPI document: only user-facing REST endpoints (api/* minus api/oauth), with a bearer
// security scheme declared so the in-app reference's Authorize works. See OpenApiCuration.
builder.Services.AddOpenApi("v1", options =>
{
    options.ShouldInclude = desc => Diariz.Api.OpenApi.OpenApiCuration.ShouldInclude(desc.RelativePath);
    options.AddDocumentTransformer<Diariz.Api.OpenApi.OpenApiCuration.SecuritySchemeTransformer>();
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// ---- Startup: migrate, seed, ensure bucket ----
await using (var scope = app.Services.CreateAsyncScope())
{
    var sp = scope.ServiceProvider;
    var db = sp.GetRequiredService<DiarizDbContext>();
    await db.Database.MigrateAsync();
    await sp.GetRequiredService<IAudioStorage>().EnsureBucketAsync();
    await Seeder.SeedRolesAsync(sp);
    var seedUserId = await Seeder.SeedDefaultUserAsync(sp, app.Configuration);
    // Groups exist with their flags, and the seed user is always a Platform Administrator. Existing role
    // holders were moved into groups once, by the AddUserGroups migration - never on boot, or a demoted user
    // would be silently re-promoted from their stale AspNetUserRoles row.
    await Seeder.SeedPlatformAuthorityAsync(db, seedUserId);
    await Seeder.SeedFormulasAsync(db);
    await MeetingTypeSeeder.SeedAsync(db);
}

// Must run before auth/cookie handling so the pipeline sees the real client scheme.
app.UseForwardedHeaders();

// The curated OpenAPI document, served in every environment under /api (so the existing nginx proxy covers
// it) and requiring auth - it backs the in-app API reference at /developers/api.
app.MapOpenApi("/api/openapi/{documentName}.json").RequireAuthorization();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TranscriptionHub>("/hubs/transcription");
// MCP endpoint (Streamable HTTP), authenticated with the per-user MCP token scheme only.
if (mcpOptions.Enabled)
    app.MapMcp("/mcp").RequireAuthorization(Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName);
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = appVersion }));

app.Run();

