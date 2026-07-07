using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Domain;

public class DiarizDbContext(DbContextOptions<DiarizDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<Transcription> Transcriptions => Set<Transcription>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<TranscriptChunk> TranscriptChunks => Set<TranscriptChunk>();
    public DbSet<Speaker> Speakers => Set<Speaker>();
    public DbSet<Summary> Summaries => Set<Summary>();
    public DbSet<MeetingMinutes> MeetingMinutes => Set<MeetingMinutes>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<ProfileContribution> ProfileContributions => Set<ProfileContribution>();
    public DbSet<RecordingAction> RecordingActions => Set<RecordingAction>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<RecordingCalendarLink> RecordingCalendarLinks => Set<RecordingCalendarLink>();
    public DbSet<IcsCalendarSource> IcsCalendarSources => Set<IcsCalendarSource>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<McpAccessToken> McpAccessTokens => Set<McpAccessToken>();
    public DbSet<MeetingType> MeetingTypes => Set<MeetingType>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // OAuth 2.1 authorization-server tables (applications/authorizations/scopes/tokens) for the MCP web
        // connector. Provider-agnostic (plain relational columns, no vector) so it stays outside the Npgsql
        // guard below and loads under the in-memory test provider too.
        builder.UseOpenIddict();

        // The vector column and pgvector extension only exist on Postgres. Under other
        // providers (e.g. the EF in-memory provider used by unit tests) the embedding is
        // unmapped — it is unused before Milestone 3 anyway.
        var isNpgsql = Database.IsNpgsql();

        // pgvector extension; embedding dimension is for typical local embedding models
        // (e.g. nomic-embed-text = 768). Adjust the migration if a different model is chosen.
        if (isNpgsql)
            builder.HasPostgresExtension("vector");

        builder.Entity<Recording>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
            e.Ignore(r => r.HasAudio); // computed from AudioDeletedAt, not stored
            e.Ignore(r => r.IsAudioProtected); // computed from AudioProtectedAt, not stored
            e.Property(r => r.Title).HasMaxLength(512);
            e.Property(r => r.Name).HasMaxLength(512);
            e.HasMany(r => r.Transcriptions)
                .WithOne(t => t.Recording!)
                .HasForeignKey(t => t.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Speakers)
                .WithOne(s => s.Recording!)
                .HasForeignKey(s => s.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Actions)
                .WithOne(a => a.Recording!)
                .HasForeignKey(a => a.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Attachments)
                .WithOne(a => a.Recording!)
                .HasForeignKey(a => a.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a section ungroups its recordings rather than deleting them.
            e.HasOne(r => r.Section)
                .WithMany(s => s.Recordings)
                .HasForeignKey(r => r.SectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // 1:1 calendar link, shared primary key; cascade-deleted with the recording.
            e.HasOne(r => r.CalendarLink)
                .WithOne(l => l.Recording!)
                .HasForeignKey<RecordingCalendarLink>(l => l.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            // Chosen meeting type; deleting the type drops recordings back to the General default (SetNull).
            e.HasOne(r => r.MeetingType)
                .WithMany()
                .HasForeignKey(r => r.MeetingTypeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RecordingCalendarLink>(e =>
        {
            e.HasKey(l => l.RecordingId);
            e.Property(l => l.EventId).HasMaxLength(1024);
            e.Property(l => l.CalendarId).HasMaxLength(1024);
            e.Property(l => l.Summary).HasMaxLength(1024);
            e.Property(l => l.HtmlLink).HasMaxLength(2048);
            e.Property(l => l.Color).HasMaxLength(32);
        });

        builder.Entity<Section>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.Name });
            e.Property(s => s.Name).HasMaxLength(128);
            // Explicit cascade so deleting a user removes their sections (the FK was only implicit before).
            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Self-referential parent for one level of nesting. Deleting a parent cascades to its
            // sub-sections; each sub-section's recordings then drop to Ungrouped via the SetNull FK above.
            e.HasOne(s => s.Parent)
                .WithMany(s => s.Children)
                .HasForeignKey(s => s.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RecordingAction>(e =>
        {
            e.HasIndex(a => new { a.RecordingId, a.Ordinal });
        });

        builder.Entity<Attachment>(e =>
        {
            e.HasIndex(a => new { a.RecordingId, a.Ordinal });
            e.Property(a => a.Name).HasMaxLength(512);
        });

        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(256);
            // Default backfills existing rows on migration and any user created without an explicit quota.
            e.Property(u => u.QuotaBytes).HasDefaultValue(Entities.PlatformSettings.DefaultStarterQuotaBytes);
            // Google linkage: one Google identity maps to at most one user. Nullable, so multiple NULLs
            // (password-only accounts) coexist under a unique index in Postgres.
            e.Property(u => u.GoogleSubject).HasMaxLength(256);
            e.HasIndex(u => u.GoogleSubject).IsUnique();
            e.Property(u => u.PictureUrl).HasMaxLength(1024);
        });

        // Platform-wide settings: a single seeded row (Id = 1).
        builder.Entity<PlatformSettings>(e =>
        {
            e.Property(s => s.MinutesGenerationMode).HasDefaultValue(MinutesGenerationMode.SingleCall);
            e.Property(s => s.AutoDeleteAudioEnabled).HasDefaultValue(false);
            e.Property(s => s.AudioRetentionDays)
                .HasDefaultValue(Entities.PlatformSettings.DefaultAudioRetentionDays);
            e.Property(s => s.AudioDeletionTimeOfDay).HasDefaultValue(new TimeOnly(3, 0));
            e.HasData(new PlatformSettings
            {
                Id = Entities.PlatformSettings.SingletonId,
                StarterQuotaBytes = Entities.PlatformSettings.DefaultStarterQuotaBytes,
                MaxQuotaBytes = Entities.PlatformSettings.DefaultMaxQuotaBytes,
                MinutesGenerationMode = MinutesGenerationMode.SingleCall,
                AutoDeleteAudioEnabled = false,
                AudioRetentionDays = Entities.PlatformSettings.DefaultAudioRetentionDays,
                AudioDeletionTimeOfDay = new TimeOnly(3, 0),
            });
        });

        builder.Entity<Transcription>(e =>
        {
            e.HasIndex(t => new { t.RecordingId, t.Version }).IsUnique();
            e.HasMany(t => t.Segments)
                .WithOne(s => s.Transcription!)
                .HasForeignKey(s => s.TranscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Summary)
                .WithOne(s => s.Transcription!)
                .HasForeignKey<Summary>(s => s.TranscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.MeetingMinutes)
                .WithOne(m => m.Transcription!)
                .HasForeignKey<MeetingMinutes>(m => m.TranscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Segment>(e =>
        {
            e.HasIndex(s => new { s.TranscriptionId, s.Ordinal });
            if (isNpgsql)
                e.Property(s => s.Embedding).HasColumnType("vector(768)");
            else
                e.Ignore(s => s.Embedding);
        });

        // Windowed retrieval chunks (Milestone 3 RAG). Provider-agnostic except the vector column, which is
        // Postgres-only (ignored under the in-memory test provider, like Segment.Embedding). The (UserId,
        // RecordingId) index backs the owner-scoped pre-filter; chunks cascade-delete with their transcription.
        builder.Entity<TranscriptChunk>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.RecordingId });
            e.HasIndex(c => c.TranscriptionId);
            e.Property(c => c.SpeakerLabels).HasMaxLength(1024);
            e.HasOne(c => c.Transcription)
                .WithMany()
                .HasForeignKey(c => c.TranscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
            if (isNpgsql)
                e.Property(c => c.Embedding).HasColumnType("vector(768)");
            else
                e.Ignore(c => c.Embedding);
        });

        builder.Entity<Speaker>(e =>
        {
            e.HasIndex(s => new { s.RecordingId, s.Label }).IsUnique();
            e.Property(s => s.DisplayName).HasMaxLength(256);
            if (isNpgsql)
                e.Property(s => s.Embedding).HasColumnType("vector(192)"); // ECAPA-TDNN dimension
            else
                e.Ignore(s => s.Embedding);
            // Identifying a speaker links it to a profile; deleting the profile just unlinks (SetNull).
            e.HasOne(s => s.Profile)
                .WithMany()
                .HasForeignKey(s => s.ProfileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Enrolled voiceprints (per-user). The centroid is a pgvector column on Postgres; ignored under
        // the in-memory test provider (matching is integration-tested only).
        builder.Entity<SpeakerProfile>(e =>
        {
            e.HasIndex(p => p.UserId);
            e.Property(p => p.Name).HasMaxLength(256);
            if (isNpgsql)
                e.Property(p => p.Embedding).HasColumnType("vector(192)");
            else
                e.Ignore(p => p.Embedding);
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProfileContribution>(e =>
        {
            e.HasIndex(c => c.ProfileId);
            if (isNpgsql)
                e.Property(c => c.Embedding).HasColumnType("vector(192)");
            else
                e.Ignore(c => c.Embedding);
            e.HasOne(c => c.Profile)
                .WithMany(p => p.Contributions)
                .HasForeignKey(c => c.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            // The contributing speaker; deleting the recording's speaker drops the contribution.
            e.HasOne(c => c.Speaker)
                .WithMany()
                .HasForeignKey(c => c.SpeakerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Per-user settings: 1:1 with the user via a shared primary key (UserId). Provider-agnostic
        // (no vector column), so it must stay outside the Npgsql guard or the in-memory test model breaks.
        builder.Entity<UserSettings>(e =>
        {
            e.HasKey(s => s.UserId);
            e.Property(s => s.SummaryApiBase).HasMaxLength(512);
            e.Property(s => s.SummaryModel).HasMaxLength(256);
            e.Property(s => s.GoogleCalendarGranted).HasDefaultValue(false);
            if (isNpgsql)
                e.Property(s => s.ChatToolOverridesJson).HasColumnType("jsonb");
            e.HasOne(s => s.User)
                .WithOne(u => u.Settings)
                .HasForeignKey<UserSettings>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Saved chat conversations. The thread + context are JSON blobs (jsonb on Postgres; plain text
        // under the in-memory test provider). Provider-agnostic shape stays outside the Npgsql guard.
        builder.Entity<ChatSession>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.UpdatedAt });
            e.Property(c => c.Title).HasMaxLength(256);
            if (isNpgsql)
            {
                e.Property(c => c.MessagesJson).HasColumnType("jsonb");
                e.Property(c => c.ContextJson).HasColumnType("jsonb");
            }
            e.HasOne(c => c.User)
                .WithMany(u => u.ChatSessions)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Per-user external .ics calendar feeds. Events are fetched live and never stored; deleting a user
        // removes their feeds. Provider-agnostic shape.
        builder.Entity<IcsCalendarSource>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
            e.Property(s => s.Name).HasMaxLength(128);
            e.Property(s => s.Url).HasMaxLength(2048);
            e.Property(s => s.Color).HasMaxLength(32);
            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Meeting types (minutes templates). UserId null = a shared Platform type; non-null = a user's Personal
        // type (cascade-deleted with the user). Key is a stable slug for the seeded standards (unique; multiple
        // NULLs coexist for user-created types under the Postgres unique index). ContentJson is jsonb on Postgres.
        builder.Entity<MeetingType>(e =>
        {
            e.HasIndex(m => m.UserId);
            e.HasIndex(m => m.Key).IsUnique();
            e.Property(m => m.Key).HasMaxLength(64);
            e.Property(m => m.GroupName).HasMaxLength(128);
            e.Property(m => m.Title).HasMaxLength(256);
            e.Property(m => m.Icon).HasMaxLength(64);
            e.Property(m => m.Color).HasMaxLength(32);
            if (isNpgsql)
                e.Property(m => m.ContentJson).HasColumnType("jsonb");
            e.HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Per-user MCP personal access tokens. Only the SHA-256 hash is stored (unique, so incoming tokens
        // can be looked up by hash); deleting a user removes their tokens. Provider-agnostic shape.
        builder.Entity<McpAccessToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.Property(t => t.Name).HasMaxLength(128);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Prefix).HasMaxLength(32);
            e.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
