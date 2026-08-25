using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The labelled-decision log, against real Postgres.
///
/// <para>This table is the point of the confirmation band. Every accept or reject is a labelled pair with the
/// distance that was on offer, and rejections are the <b>only</b> source of hard negatives the platform has -
/// the manually-linked speakers are all positives. A threshold sweep is exactly as good as this table.</para>
///
/// <para>The distance is recorded as it stood at the moment of the decision, never recomputed later: the
/// gallery moves as people are enrolled, so a recomputed number would describe a different question than the
/// one the person actually answered.</para></summary>
[Collection(IntegrationCollection.Name)]
public class SpeakerIdentityDecisionSchemaTests(ContainersFixture fx)
{
    private static async Task<(Guid speakerId, Guid personId, Guid userId)> SeedAsync(ContainersFixture fx)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
        };
        db.AddRange(user, person, rec, speaker);
        await db.SaveChangesAsync();
        return (speaker.Id, person.Id, user.Id);
    }

    [Fact]
    public async Task A_decision_round_trips_with_the_distance_that_was_on_offer()
    {
        var (speakerId, personId, userId) = await SeedAsync(fx);
        var id = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerIdentityDecisions.Add(new SpeakerIdentityDecision
            {
                Id = id,
                SpeakerId = speakerId,
                PersonId = personId,
                Decision = IdentityDecisionKind.Rejected,
                Distance = 0.47,
                DecidedByUserId = userId,
            });
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        var row = await read.SpeakerIdentityDecisions.SingleAsync(d => d.Id == id);
        Assert.Equal(IdentityDecisionKind.Rejected, row.Decision);
        Assert.Equal(0.47, row.Distance, 3);
        Assert.Equal(userId, row.DecidedByUserId);
        Assert.NotEqual(default, row.DecidedAt);
    }

    [Fact]
    public async Task Deleting_the_speaker_takes_its_decisions_with_it()
    {
        // The decision is about that speaker in that recording. Once the recording is gone the pair means
        // nothing, and a dangling row would be counted by a later sweep as evidence about a voice nobody can
        // listen to any more.
        var (speakerId, personId, _) = await SeedAsync(fx);

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerIdentityDecisions.Add(new SpeakerIdentityDecision
            {
                Id = Guid.NewGuid(), SpeakerId = speakerId, PersonId = personId,
                Decision = IdentityDecisionKind.Accepted, Distance = 0.2,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Speakers.Remove(await db.Speakers.SingleAsync(s => s.Id == speakerId));
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        Assert.Empty(await read.SpeakerIdentityDecisions.Where(d => d.SpeakerId == speakerId).ToListAsync());
    }

    [Fact]
    public async Task Deleting_the_decider_keeps_the_decision()
    {
        // Who decided is provenance, not the point: the labelled pair stays valid evidence whether or not
        // that account still exists.
        //
        // The decider here is deliberately NOT the recording's owner. Delete the owner and the decision goes
        // with it anyway - user, recording, speaker, decision all cascade - which the previous test covers.
        // This one isolates the DecidedByUserId foreign key itself.
        var (speakerId, personId, _) = await SeedAsync(fx);
        var id = Guid.NewGuid();
        var deciderId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = deciderId, UserName = $"{Guid.NewGuid()}@x.test" });
            db.SpeakerIdentityDecisions.Add(new SpeakerIdentityDecision
            {
                Id = id, SpeakerId = speakerId, PersonId = personId,
                Decision = IdentityDecisionKind.Rejected, Distance = 0.45, DecidedByUserId = deciderId,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == deciderId));
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        var row = await read.SpeakerIdentityDecisions.SingleAsync(d => d.Id == id);
        Assert.Null(row.DecidedByUserId);
        Assert.Equal(0.45, row.Distance, 3);
    }

    [Fact]
    public async Task The_pair_is_indexed_because_every_rescan_reads_it()
    {
        // The rejected-pair guard runs once per speaker per re-scan. Without the index that is a sequential
        // scan of the whole log inside a loop over every speaker in the platform.
        await using var db = fx.CreateDbContext();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        cmd.CommandText = """
            SELECT indexdef FROM pg_indexes WHERE tablename = 'SpeakerIdentityDecisions'
            """;
        var defs = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) defs.Add(reader.GetString(0));

        Assert.Contains(defs, d => d.Contains("SpeakerId") && d.Contains("ProfileId"));
    }
}
