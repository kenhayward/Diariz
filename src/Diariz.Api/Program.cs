using System.Text;
using Amazon.S3;
using Amazon.Runtime;
using Diariz.Api.Configuration;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
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
        o.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<DiarizDbContext>();

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
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// ---- Storage (MinIO / S3) ----
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var cfg = new AmazonS3Config
    {
        ServiceURL = storage.Endpoint,
        ForcePathStyle = storage.ForcePathStyle,
        AuthenticationRegion = "us-east-1"
    };
    return new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), cfg);
});
builder.Services.AddSingleton<IAudioStorage, AudioStorage>();

// ---- Redis job queue ----
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(queue.RedisConnection));
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();

// ---- App services ----
builder.Services.AddScoped<ITokenService, TokenService>();

builder.Services.AddControllers();
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
    await SeedDefaultUserAsync(sp, app.Configuration);
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<TranscriptionHub>("/hubs/transcription");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static async Task SeedDefaultUserAsync(IServiceProvider sp, IConfiguration config)
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
    var email = config["Seed:Email"];
    var password = config["Seed:Password"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        logger.LogWarning("Seed skipped: Seed:Email / Seed:Password not configured.");
        return;
    }

    var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
    if (await users.FindByEmailAsync(email) is not null)
    {
        logger.LogInformation("Seed user {Email} already exists; nothing to do.", email);
        return;
    }

    var result = await users.CreateAsync(new ApplicationUser { UserName = email, Email = email }, password);
    if (result.Succeeded)
        logger.LogInformation("Seed user {Email} created.", email);
    else
        logger.LogError("Seed user {Email} creation FAILED: {Errors}", email,
            string.Join("; ", result.Errors.Select(e => e.Description)));
}
