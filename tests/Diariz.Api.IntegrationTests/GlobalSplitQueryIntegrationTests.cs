using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>The cartesian-Include bug was fixed site by site twice - 0.228.2 found four in one file and called
/// it done; 0.228.3 found nine across six. Each round the remaining sites were invisible until production
/// traffic hit them. This pins the class shut instead: `QuerySplittingBehavior.SplitQuery` is the app-wide
/// default, so a query that Includes two sibling collections cannot return their product even if nobody
/// remembers to write `.AsSplitQuery()`.
///
/// It is set in <see cref="DiarizDbContext.OnConfiguring"/> rather than at the `AddDbContext` call, because
/// there are a dozen `UseNpgsql` sites across the app, the design-time factory and the test suite - and
/// "remember to add it at every call site" is the exact failure mode this is meant to end. `OnConfiguring`
/// runs for every context however its options were built, which is what the first test here proves.</summary>
[Collection(IntegrationCollection.Name)]
public class GlobalSplitQueryIntegrationTests(ContainersFixture fx)
{
    /// <summary>Options built the way every test fixture and the design-time factory build them: externally,
    /// with no knowledge of the splitting default. If `OnConfiguring` did not apply, this context would
    /// single-query and the assertion would fail - which is the point.</summary>
    [Fact]
    public async Task ExternallyConfiguredContext_SplitsSiblingCollections_WithNoAsSplitQuery()
    {
        Guid recId;
        await using (var seed = fx.CreateDbContext())
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test",
            };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            seed.AddRange(user, rec, tr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "one", StartMs = 0, EndMs = 10 },
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 1, SpeakerLabel = "SPEAKER_01", Original = "two", StartMs = 10, EndMs = 20 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada" },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Grace" },
                new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "ship it", Ordinal = 0 },
                new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "write it up", Ordinal = 1 });
            await seed.SaveChangesAsync();
            recId = rec.Id;
        }

        var sql = new RecordsSql();
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(sql)
            .Options;

        await using (var db = new DiarizDbContext(options))
        {
            // Deliberately NO .AsSplitQuery() - the whole point is that it is no longer needed.
            var rec = await db.Recordings
                .Include(r => r.Speakers)
                .Include(r => r.Actions)
                .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
                .FirstOrDefaultAsync(r => r.Id == recId);

            // The graph still arrives intact, each row exactly once.
            Assert.NotNull(rec);
            Assert.Equal(2, rec!.Speakers.Count);
            Assert.Equal(2, rec.Actions.Count);
            Assert.Equal(2, Assert.Single(rec.Transcriptions).Segments.Count);
        }

        var offenders = sql.Statements
            .Where(s => (s.Contains("JOIN \"Segments\"") || s.Contains("FROM \"Segments\""))
                        && (s.Contains("JOIN \"Speakers\"") || s.Contains("JOIN \"RecordingActions\"")))
            .ToList();
        Assert.True(offenders.Count == 0,
            "A context whose options were configured externally still single-queried sibling collections, so " +
            "the global splitting default is not reaching every context:\n\n" + string.Join("\n\n", offenders));
    }

    /// <summary>The other half of the guarantee: that PRODUCTION gets it. The test above would pass just as
    /// well if the setting only ever applied to contexts the test suite builds, so this resolves the context
    /// out of the real `Program.cs` DI container and reads the option back off it.</summary>
    [Fact]
    public void TheRealAppHost_ConfiguresSplitQueryByDefault()
    {
        using var factory = new DiarizWebAppFactory(fx);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();

        var relational = db.GetService<IDbContextOptions>()
            .Extensions.OfType<RelationalOptionsExtension>()
            .Single();

        Assert.Equal(QuerySplittingBehavior.SplitQuery, relational.QuerySplittingBehavior);
    }
}
