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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // pgvector extension; embedding dimension is for typical local embedding models
        // (e.g. nomic-embed-text = 768). Adjust the migration if a different model is chosen.
        builder.HasPostgresExtension("vector");

        builder.Entity<Recording>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
            e.Property(r => r.Title).HasMaxLength(512);
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
            e.Property(s => s.Embedding).HasColumnType("vector(768)");
        });

        builder.Entity<Speaker>(e =>
        {
            e.HasIndex(s => new { s.RecordingId, s.Label }).IsUnique();
            e.Property(s => s.DisplayName).HasMaxLength(256);
        });
    }
}
