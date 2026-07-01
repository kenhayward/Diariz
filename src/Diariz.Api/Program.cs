using System.Text;
using Amazon.S3;
using Amazon.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Diariz.Api.Configuration;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- Options ----
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<JobQueueOptions>(builder.Configuration.GetSection(JobQueueOptions.Section));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
builder.Services.Configure<SummarizationOptions>(builder.Configuration.GetSection(SummarizationOptions.Section));
builder.Services.Configure<MeetingMinutesOptions>(builder.Configuration.GetSection(MeetingMinutesOptions.Section));
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.Section));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<AppPublicOptions>(builder.Configuration.GetSection(AppPublicOptions.Section));
builder.Services.Configure<IdentificationOptions>(builder.Configuration.GetSection(IdentificationOptions.Section));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.Section));
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection(AttachmentOptions.Section));

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
    });
builder.Services.AddAuthorization(o =>
    o.AddPolicy("Admin", p => p.RequireRole(Roles.Administrator, Roles.PlatformAdministrator)));

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
builder.Services.AddScoped<IChatToolRegistry, ChatToolRegistry>();
builder.Services.AddScoped<IChatToolSettingsResolver, ChatToolSettingsResolver>();
builder.Services.AddScoped<IChatToolOrchestrator, ChatToolOrchestrator>();

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

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TranscriptionHub>("/hubs/transcription");
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = appVersion }));

app.Run();

