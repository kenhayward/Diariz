using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres fidelity for the folder (section) formula-run map-reduce. The included recording set
/// is resolved room-aware - the section's own placements PLUS its direct sub-sections' placements, scoped to the
/// section's RoomId, via the RoomRecordings join with an OrderBy on Recording.CreatedAt. That join + ordering
/// only translates faithfully under Npgsql (the in-memory provider ignores ordering inside a filtered Include and
/// does not enforce the room scope), so this piece is worth an integration test rather than a unit test.</summary>
[Collection(IntegrationCollection.Name)]
public class SectionFormulaRunIntegrationTests(ContainersFixture fx)
{
    private static Task<Guid> RoomOf(DiarizDbContext db, Guid owner) => new RoomScope(db).PersonalRoomIdAsync(owner);

    /// <summary>Real Postgres enforces the Section/Recording/Formula → AspNetUsers FKs, so tests seed a real user.</summary>
    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Seeds a recording with a single-version transcription + one segment (so its Transcript context is
    /// non-empty), then files it under <paramref name="sectionId"/> in <paramref name="userId"/>'s personal room
    /// via the real RoomScope placement path. <paramref name="createdAt"/> pins the map ordering.</summary>
    private async Task<Guid> SeedRecordingInFolder(
        Guid userId, Guid sectionId, string title, DateTimeOffset createdAt)
    {
        var recId = Guid.NewGuid();
        await using var db = fx.CreateDbContext();
        var rec = new Recording { Id = recId, UserId = userId, Title = title, BlobKey = "k", CreatedAt = createdAt };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = $"Content of {title}.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(recId, userId, sectionId);
        return recId;
    }

    [Fact]
    public async Task Section_run_includes_parent_and_subsection_recordings_then_reduces_room_scoped()
    {
        var userId = await SeedUser();

        // A parent folder and ONE sub-folder under it, both in the owner's personal room.
        Guid roomId, parentId = Guid.NewGuid(), childId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            roomId = await RoomOf(db, userId);
            db.Sections.Add(new Section { Id = parentId, UserId = userId, RoomId = roomId, Name = "Parent" });
            db.Sections.Add(new Section { Id = childId, UserId = userId, RoomId = roomId, Name = "Child", ParentId = parentId });
            await db.SaveChangesAsync();
        }

        // One meeting filed directly in the parent, one in the sub-folder - both must be mapped.
        await SeedRecordingInFolder(userId, parentId, "Alpha", DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedRecordingInFolder(userId, childId, "Beta", DateTimeOffset.UtcNow.AddMinutes(-5));

        // A DECOY in a SECOND user's room + section, with content, filed under a same-named "Parent" folder there.
        // It must NOT leak into this run - the room scope is the fence.
        var otherUserId = await SeedUser();
        var decoyParentId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            var otherRoom = await RoomOf(db, otherUserId);
            db.Sections.Add(new Section { Id = decoyParentId, UserId = otherUserId, RoomId = otherRoom, Name = "Parent" });
            await db.SaveChangesAsync();
        }
        await SeedRecordingInFolder(otherUserId, decoyParentId, "Gamma-decoy", DateTimeOffset.UtcNow.AddMinutes(-1));

        var formulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Formulas.Add(new Formula
            {
                Id = formulaId, Scope = FormulaScope.Personal, OwnerUserId = userId,
                Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
                Context = FormulaContext.Transcript, Enabled = true,
            });
            db.SectionFormulaResults.Add(new SectionFormulaResult
            {
                Id = resultId, SectionId = parentId, CreatedByUserId = userId,
                FormulaId = formulaId, Name = "Key Decisions", Ordinal = 0,
                Status = FormulaRunStatus.Generating,
            });
            await db.SaveChangesAsync();
        }

        var chat = new FakeChatStreamClient { StreamRounds = ["MAP-A", "MAP-B", "REDUCE"] };
        var hub = new FakeHubContext();

        await using (var db = fx.CreateDbContext())
        {
            await FormulaRunProcessor.ProcessAsync(
                db, chat, new FakeSummarizationSettingsResolver(), hub,
                new FormulaRunJob(null, parentId, resultId, formulaId, userId), 48_000, NullLogger.Instance,
                new CapturingWebhookPublisher(), "");
        }

        // 2 map calls (parent + its sub-section, room-scoped - the decoy in another room excluded) + 1 reduce.
        Assert.Equal(3, chat.Calls);

        // The reduce (3rd) call concatenates the per-meeting map outputs under "## {name}" headings, ordered by
        // CreatedAt (Alpha before Beta). The decoy's content must not appear.
        var reduceUser = chat.AllStreamMessages[2][1].Content;
        Assert.Contains("## Alpha", reduceUser);
        Assert.Contains("MAP-A", reduceUser);
        Assert.Contains("## Beta", reduceUser);
        Assert.Contains("MAP-B", reduceUser);
        Assert.DoesNotContain("Gamma-decoy", reduceUser);
        Assert.True(reduceUser.IndexOf("## Alpha", StringComparison.Ordinal)
                    < reduceUser.IndexOf("## Beta", StringComparison.Ordinal));

        // The row is flipped to Ready with the reduce output, verified from a fresh DbContext.
        await using (var verify = fx.CreateDbContext())
        {
            var persisted = await verify.SectionFormulaResults.FindAsync(resultId);
            Assert.NotNull(persisted);
            Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
            Assert.Equal("REDUCE", persisted.Text);
            Assert.Null(persisted.Error);
        }

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }
}
