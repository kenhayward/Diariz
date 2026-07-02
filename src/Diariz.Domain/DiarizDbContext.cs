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
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

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
        builder.Entity<PlatformSettings>().HasData(new PlatformSettings
        {
            Id = Entities.PlatformSettings.SingletonId,
            StarterQuotaBytes = Entities.PlatformSettings.DefaultStarterQuotaBytes,
            MaxQuotaBytes = Entities.PlatformSettings.DefaultMaxQuotaBytes,
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
    }
}
