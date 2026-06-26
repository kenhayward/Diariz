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
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

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
        });

        // Per-user settings: 1:1 with the user via a shared primary key (UserId). Provider-agnostic
        // (no vector column), so it must stay outside the Npgsql guard or the in-memory test model breaks.
        builder.Entity<UserSettings>(e =>
        {
            e.HasKey(s => s.UserId);
            e.Property(s => s.SummaryApiBase).HasMaxLength(512);
            e.Property(s => s.SummaryModel).HasMaxLength(256);
            e.HasOne(s => s.User)
                .WithOne(u => u.Settings)
                .HasForeignKey<UserSettings>(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
