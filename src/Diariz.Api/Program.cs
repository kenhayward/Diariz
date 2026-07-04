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
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.Section));
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
    o.KnownNetworks.Clear();
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

// ---- Auth (JWT) ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
                // The platform backup is downloaded via an anchor href (can't set an Authorization header).
                var isBackup = path.StartsWithSegments("/api/maintenance")
                    && path.Value!.EndsWith("/backup", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(token) && (path.StartsWithSegments("/hubs") || isRecordingAsset || isBackup))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    })
    // MCP personal-access-token scheme, used only by the /mcp endpoint (a bearer token pasted into Claude's
    // MCP config). Independent of the JWT session.
    .AddScheme<Diariz.Api.Auth.McpAuthSchemeOptions, Diariz.Api.Auth.McpBearerAuthenticationHandler>(
        Diariz.Api.Auth.McpBearerAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.RequireRole(Roles.Administrator, Roles.PlatformAdministrator));
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

// ---- Redis job queue ----
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(queue.RedisConnection));
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();

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
builder.Services.AddHttpClient<ISummarizationClient, SummarizationClient>();
builder.Services.AddHttpClient<IActionsClient, ActionsClient>();
builder.Services.AddHttpClient<ITranslationClient, TranslationClient>();
builder.Services.AddScoped<ISummarizationSettingsResolver, SummarizationSettingsResolver>();
builder.Services.AddHostedService<SummarizationWorker>();

// ---- Meeting minutes (shares the per-user summarisation config; its own stream + consumer) ----
builder.Services.AddHttpClient<IMeetingMinutesClient, MeetingMinutesClient>();
builder.Services.AddHostedService<MeetingMinutesWorker>();
// Action extraction also runs in the pipeline (its own stream/worker), reusing IActionsClient (registered above).
builder.Services.AddHostedService<ActionsWorker>();

// ---- Semantic-search (RAG, M3) embeddings: its own endpoint/model config, stream + consumer, and a
// one-time startup backfill that indexes the existing library once an embeddings endpoint is configured. ----
builder.Services.AddHttpClient<IEmbeddingClient, EmbeddingClient>();
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
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IGoogleCalendarClient, GoogleCalendarClient>();

// ---- MCP server (in-process Streamable-HTTP /mcp endpoint; per-user token auth) ----
// The token services back both the management controller (JWT) and the /mcp bearer scheme.
builder.Services.AddSingleton<IMcpTokenService, McpTokenService>();
builder.Services.AddScoped<IMcpTokenAuthenticator, McpTokenAuthenticator>();
builder.Services.AddScoped<IMcpResourceService, McpResourceService>();
var mcpOptions = builder.Configuration.GetSection(McpOptions.Section).Get<McpOptions>() ?? new McpOptions();
if (mcpOptions.Enabled)
{
    builder.Services.AddHttpContextAccessor();
    // The handler reads the current request's user/services via IHttpContextAccessor (its backing store is a
    // static AsyncLocal, so a plain instance captured here sees the live request). MapMcp runs it in-pipeline.
    var mcpHandlers = new Diariz.Api.Mcp.DiarizMcpHandlers(new HttpContextAccessor());
    var mcpVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "Diariz", Version = mcpVersion };
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

builder.Services.AddControllers()
    .AddJsonOptions(o => JsonConfig.Apply(o.JsonSerializerOptions));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

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
    await Seeder.SeedDefaultUserAsync(sp, app.Configuration);
}

// Must run before auth/cookie handling so the pipeline sees the real client scheme.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

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

