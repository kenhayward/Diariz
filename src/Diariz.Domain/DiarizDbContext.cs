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
    public DbSet<SectionSummary> SectionSummaries => Set<SectionSummary>();
    public DbSet<SectionMinutes> SectionMinutes => Set<SectionMinutes>();
    public DbSet<SectionAttachment> SectionAttachments => Set<SectionAttachment>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<SpeakerProfile> SpeakerProfiles => Set<SpeakerProfile>();
    public DbSet<ProfileContribution> ProfileContributions => Set<ProfileContribution>();
    public DbSet<RecordingAction> RecordingActions => Set<RecordingAction>();
    public DbSet<RecordingTag> RecordingTags => Set<RecordingTag>();
    public DbSet<MeetingNote> MeetingNotes => Set<MeetingNote>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<RecordingCalendarLink> RecordingCalendarLinks => Set<RecordingCalendarLink>();
    public DbSet<IcsCalendarSource> IcsCalendarSources => Set<IcsCalendarSource>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<McpAccessToken> McpAccessTokens => Set<McpAccessToken>();
    public DbSet<ApiAccessToken> ApiAccessTokens => Set<ApiAccessToken>();
    public DbSet<MeetingType> MeetingTypes => Set<MeetingType>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<RoomRecording> RoomRecordings => Set<RoomRecording>();
    public DbSet<Formula> Formulas => Set<Formula>();
    public DbSet<FormulaResult> FormulaResults => Set<FormulaResult>();
    public DbSet<SectionFormulaResult> SectionFormulaResults => Set<SectionFormulaResult>();
    public DbSet<FormulaSubscription> FormulaSubscriptions => Set<FormulaSubscription>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // OAuth 2.1 authorization-server tables (applications/authorizations/scopes/tokens) for the MCP web
        // connector. Provider-agnostic (plain relational columns, no vector) so it stays outside the Npgsql
        // guard below and loads under the in-memory test provider too.
        builder.UseOpenIddict();

        // Platform authority: a user's permissions are the union of the flags on the groups they belong to.
        // Provider-agnostic (no vector, no Postgres-only types), so it stays outside the Npgsql guard below.
        builder.Entity<UserGroup>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(g => g.Name).IsUnique();
        });

        builder.Entity<UserGroupMember>(e =>
        {
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasOne(m => m.Group).WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Rooms: the workspace a recording, folder, voiceprint or chat belongs to. Provider-agnostic config
        // here; the two filtered unique indexes are relational-only and live in the Npgsql block below.
        builder.Entity<Room>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(128).IsRequired();
            e.HasOne(r => r.Owner).WithMany()
                .HasForeignKey(r => r.OwnerUserId)
                // A deleted user ORPHANS their personal room; its recordings survive in shared rooms.
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RoomMember>(e =>
        {
            e.HasKey(m => new { m.RoomId, m.PrincipalType, m.PrincipalId });
            e.HasOne(m => m.Room).WithMany(r => r.Members)
                .HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.PrincipalType, m.PrincipalId });
        });

        // The placement of a recording in a room. The folder (SectionId) belongs to the placement, so the same
        // recording can sit in different folders in different rooms.
        builder.Entity<RoomRecording>(e =>
        {
            e.HasKey(p => new { p.RoomId, p.RecordingId });
            e.HasOne(p => p.Room).WithMany()
                .HasForeignKey(p => p.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Recording).WithMany()
                .HasForeignKey(p => p.RecordingId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a folder ungroups its recordings; it never removes them from the room.
            e.HasOne(p => p.Section).WithMany()
                .HasForeignKey(p => p.SectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => new { p.RoomId, p.SectionId });
        });

        // The vector column and pgvector extension only exist on Postgres. Under other
        // providers (e.g. the EF in-memory provider used by unit tests) the embedding is
        // unmapped — it is unused before Milestone 3 anyway.
        var isNpgsql = Database.IsNpgsql();

        // pgvector extension; embedding dimension is for typical local embedding models
        // (e.g. nomic-embed-text = 768). Adjust the migration if a different model is chosen.
        if (isNpgsql)
            builder.HasPostgresExtension("vector");

        // Filtered unique indexes are relational-only, so the in-memory test provider never sees them.
        if (isNpgsql)
        {
            // One personal room per user, and any number of orphaned ones (OwnerUserId null).
            builder.Entity<Room>()
                .HasIndex(r => r.OwnerUserId)
                .IsUnique()
                .HasFilter("\"OwnerUserId\" IS NOT NULL");

            // Shared room names are identifiers; personal room names are display labels (the owner's name),
            // and two users may legitimately share a name.
            builder.Entity<Room>()
                .HasIndex(r => r.Name)
                .IsUnique()
                .HasFilter("\"Kind\" = 1");

            // Exactly one main room per recording. Two would be unrepresentable, not merely wrong.
            builder.Entity<RoomRecording>()
                .HasIndex(p => p.RecordingId)
                .IsUnique()
                .HasFilter("\"IsMainRoom\"");

            // A main placement is nobody's share: you cannot share a recording into its own home.
            builder.Entity<RoomRecording>()
                .ToTable(t => t.HasCheckConstraint(
                    "CK_RoomRecordings_MainRoomHasNoSharer",
                    "NOT \"IsMainRoom\" OR (\"SharedByUserId\" IS NULL AND \"SharedAt\" IS NULL)"));
        }

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
            e.HasMany(r => r.Tags)
                .WithOne(t => t.Recording!)
                .HasForeignKey(t => t.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Attachments)
                .WithOne(a => a.Recording!)
                .HasForeignKey(a => a.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            // The recording's folder lives on its RoomRecording placement now, not on the recording. Deleting a
            // section ungroups the placement (RoomRecording.SectionId is ON DELETE SET NULL).
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
            e.HasIndex(s => new { s.RoomId, s.Name });
            e.Property(s => s.Name).HasMaxLength(128);
            // Explicit cascade so deleting a user removes their sections (the FK was only implicit before).
            e.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // RoomId is a plain column for now (no FK / navigation): the Rooms FK + cascade land in the phase
            // that drops UserId, once every fixture sets a room. Until then a bad RoomId can't break section
            // seeding across the whole suite mid-migration.
            // Self-referential parent for one level of nesting. Deleting a parent cascades to its
            // sub-sections; each sub-section's recordings then drop to Ungrouped via the SetNull FK above.
            e.HasOne(s => s.Parent)
                .WithMany(s => s.Children)
                .HasForeignKey(s => s.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
            // Folder-level LLM artifacts, 1:1, cascade-deleted with the section (and, transitively, with a
            // parent section). Provider-agnostic (no vector/jsonb), so kept outside the Npgsql guard.
            e.HasOne(s => s.Summary)
                .WithOne(x => x.Section!)
                .HasForeignKey<SectionSummary>(x => x.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Minutes)
                .WithOne(x => x.Section!)
                .HasForeignKey<SectionMinutes>(x => x.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Folder-direct attachments (many per folder), cascade-deleted with the section.
            e.HasMany(s => s.Attachments)
                .WithOne(a => a.Section!)
                .HasForeignKey(a => a.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SectionMinutes>(e =>
        {
            // The folder's chosen template; deleting the type falls back to the General default (SetNull).
            e.HasOne(m => m.MeetingType)
                .WithMany()
                .HasForeignKey(m => m.MeetingTypeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RecordingAction>(e =>
        {
            e.HasIndex(a => new { a.RecordingId, a.Ordinal });
        });

        // Machine-extracted tag-cloud tags. Provider-agnostic (plain columns, no vector/jsonb), so it
        // stays outside the Npgsql guard and loads under the in-memory test provider too.
        builder.Entity<RecordingTag>(e =>
        {
            e.HasIndex(t => new { t.RecordingId, t.Ordinal });
            e.Property(t => t.Tag).HasMaxLength(64);
        });

        builder.Entity<MeetingNote>(e =>
        {
            e.HasIndex(n => new { n.RecordingId, n.Ordinal });
            e.HasIndex(n => new { n.UserId, n.CalendarId, n.EventId });
            e.Property(n => n.Text).HasMaxLength(2048);
            e.Property(n => n.CalendarId).HasMaxLength(256);
            e.Property(n => n.EventId).HasMaxLength(256);
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(n => n.Recording).WithMany().HasForeignKey(n => n.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Attachment>(e =>
        {
            e.HasIndex(a => new { a.RecordingId, a.Ordinal });
            e.Property(a => a.Name).HasMaxLength(512);
        });

        // Folder-direct attachments. Provider-agnostic (plain columns, no vector/jsonb), so it stays outside
        // the Npgsql guard and loads under the in-memory test provider too.
        builder.Entity<SectionAttachment>(e =>
        {
            e.HasIndex(a => new { a.SectionId, a.Ordinal });
            e.Property(a => a.Name).HasMaxLength(512);
        });

        // Saved template + chosen context, run over a recording to produce a Markdown result. Personal formulas
        // have an OwnerUserId; Platform/Diariz scopes have none. Provider-agnostic (plain columns) apart from
        // ContentJson, so it otherwise stays outside the Npgsql guard and loads under the in-memory test provider.
        builder.Entity<Formula>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(256);
            e.Property(f => f.Description).HasMaxLength(1024);
            e.Property(f => f.Enabled).HasDefaultValue(true);
            // jsonb on Postgres, plain text elsewhere - same treatment as MeetingType.ContentJson.
            if (isNpgsql)
                e.Property(f => f.ContentJson).HasColumnType("jsonb");
            // Cascade: a Personal formula belongs to its owner, so deleting the account deletes it. Platform/
            // Diariz formulas have a null OwnerUserId and are unaffected. This is the only cascade path from
            // User to Formula, so there is no conflict.
            e.HasOne(f => f.Owner)
                .WithMany()
                .HasForeignKey(f => f.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A subscriber's live link to a shared Personal formula. Two cascade paths (formula + user) are safe
        // on Postgres (see the FormulaResult block). Unique per (FormulaId, UserId) so a user can't add the
        // same formula twice; the controller is also idempotent.
        builder.Entity<FormulaSubscription>(e =>
        {
            e.HasIndex(s => new { s.FormulaId, s.UserId }).IsUnique();
            e.HasOne(s => s.Formula).WithMany()
                .HasForeignKey(s => s.FormulaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User).WithMany()
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // The Markdown document produced by running a Formula over a recording. Cascades with the recording;
        // SET NULL (not deleted) when its source Formula is later removed. Provider-agnostic (plain columns).
        builder.Entity<FormulaResult>(e =>
        {
            e.HasIndex(r => new { r.RecordingId, r.Ordinal });
            e.Property(r => r.Name).HasMaxLength(256);
            e.HasOne(r => r.Recording)
                .WithMany()
                .HasForeignKey(r => r.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Formula)
                .WithMany()
                .HasForeignKey(r => r.FormulaId)
                .OnDelete(DeleteBehavior.SetNull);
            // SetNull, not Cascade: a result authored by user A can live on user B's shared recording. If A's
            // account is deleted we keep B's document and merely drop the attribution (CreatedByUserId is
            // nullable). Postgres permits multiple cascade paths, so this is a deliberate retention choice,
            // not a workaround for a path conflict.
            e.HasOne(r => r.CreatedBy)
                .WithMany()
                .HasForeignKey(r => r.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // The Markdown document produced by running a Formula over a folder (section). Cascades with the
        // section (deleting a folder removes its results); SET NULL (not deleted) when its source Formula is
        // later removed. Mirrors the FormulaResult block, section-scoped. Provider-agnostic (plain columns).
        builder.Entity<SectionFormulaResult>(e =>
        {
            e.HasIndex(r => new { r.SectionId, r.Ordinal });
            e.Property(r => r.Name).HasMaxLength(256);
            e.HasOne(r => r.Section)
                .WithMany()
                .HasForeignKey(r => r.SectionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Formula)
                .WithMany()
                .HasForeignKey(r => r.FormulaId)
                .OnDelete(DeleteBehavior.SetNull);
            // SetNull, not Cascade: a result authored by user A can live on a folder shared with user B. If A's
            // account is deleted we keep the document and merely drop the attribution (CreatedByUserId is
            // nullable). Postgres permits multiple cascade paths, so this is a deliberate retention choice.
            e.HasOne(r => r.CreatedBy)
                .WithMany()
                .HasForeignKey(r => r.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.Property(s => s.LlmTimeoutSeconds)
                .HasDefaultValue(Entities.PlatformSettings.DefaultLlmTimeoutSeconds);
            e.HasData(new PlatformSettings
            {
                Id = Entities.PlatformSettings.SingletonId,
                StarterQuotaBytes = Entities.PlatformSettings.DefaultStarterQuotaBytes,
                MaxQuotaBytes = Entities.PlatformSettings.DefaultMaxQuotaBytes,
                MinutesGenerationMode = MinutesGenerationMode.SingleCall,
                AutoDeleteAudioEnabled = false,
                AudioRetentionDays = Entities.PlatformSettings.DefaultAudioRetentionDays,
                AudioDeletionTimeOfDay = new TimeOnly(3, 0),
                LlmTimeoutSeconds = Entities.PlatformSettings.DefaultLlmTimeoutSeconds,
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
            e.Property(s => s.Theme).HasDefaultValue(ThemePreference.Auto);
            e.Property(s => s.RecordingPlacementMode).HasDefaultValue(RecordingPlacementMode.SelectedFolder);
            e.Property(s => s.JobTitle).HasMaxLength(256);
            e.Property(s => s.CompanyName).HasMaxLength(256);
            e.Property(s => s.LinkedIn).HasMaxLength(256);
            e.Property(s => s.JobDescription).HasMaxLength(2048);
            e.Property(s => s.CompanyDescription).HasMaxLength(2048);
            if (isNpgsql)
            {
                e.Property(s => s.ChatToolOverridesJson).HasColumnType("jsonb");
                e.Property(s => s.GoogleSelectedCalendarIdsJson).HasColumnType("jsonb");
            }
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

        builder.Entity<ApiAccessToken>(e =>
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
