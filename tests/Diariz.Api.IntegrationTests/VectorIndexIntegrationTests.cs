using System.Data.Common;
using System.Globalization;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Diariz.Api.IntegrationTests;

/// <summary>The pgvector ANN index behind the semantic search arm, against real Postgres. Covers the two
/// halves that have to hold together: the HNSW index exists and the unfenced query can plan onto it (the whole
/// point - see issue #594), and the fenced query still plans an exact scan, which is what keeps a filtered
/// search truthful. HNSW is <b>approximate</b> and pgvector <b>post-filters</b>, so a
/// selective filter can silently drop true nearest neighbours; the behavioural tests here pin that it does
/// not, for both filters the search applies (an explicit recording scope, and room membership).</summary>
[Collection(IntegrationCollection.Name)]
public class VectorIndexIntegrationTests(ContainersFixture fx)
{
    private const int Dim = 768;

    /// <summary>A unit vector at angle <paramref name="theta"/> radians in the (<paramref name="a"/>,
    /// <paramref name="b"/>) plane. Cosine distance between two such vectors is <c>1 - cos(dtheta)</c>, so a
    /// family of them is ordered by angle - which lets a test say exactly which chunk is the nearest neighbour,
    /// and by how much. Tests that must not be perturbed by chunks other tests left in the shared database pick
    /// their own axis pair: anything built on a different pair sits at cosine distance 1, behind everything
    /// here.</summary>
    private static float[] Angle(double theta, int a = 0, int b = 1)
    {
        var v = new float[Dim];
        v[a] = (float)Math.Cos(theta);
        v[b] = (float)Math.Sin(theta);
        return v;
    }

    private static string Literal(float[] v) =>
        "[" + string.Join(",", v.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";

    private static TranscriptSearch Search(DiarizDbContext db, float[] queryVector) =>
        new(db,
            new FakeEmbeddingClient { Vectors = [queryVector] },
            new FakeEmbeddingSettingsResolver
            {
                Config = new EmbeddingRequestConfig("http://emb.test/v1", "k", "m", Dim, 60, 32),
            },
            new RoomScope(db));

    /// <summary>A user with their personal room and one recording, ready to hang chunks off.</summary>
    private async Task<(Guid userId, Guid recId, Guid trId)> SeedRecording(Guid? user = null, string name = "Budget Review")
    {
        await using var db = fx.CreateDbContext();
        var userId = user ?? Guid.NewGuid();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null);
        return (userId, rec.Id, tr.Id);
    }

    /// <summary>Adds <paramref name="count"/> chunks whose angles start at <paramref name="fromTheta"/> and
    /// step by <paramref name="step"/>, so a smaller starting angle means a nearer neighbour to Angle(0).</summary>
    private async Task AddChunks(
        Guid userId, Guid recId, Guid trId, int count, double fromTheta, double step, string label,
        int axisA = 0, int axisB = 1)
    {
        await using var db = fx.CreateDbContext();
        for (var i = 0; i < count; i++)
            db.TranscriptChunks.Add(new TranscriptChunk
            {
                Id = Guid.NewGuid(), TranscriptionId = trId, RecordingId = recId, UserId = userId,
                Ordinal = i, StartMs = i * 10_000, EndMs = i * 10_000 + 9_000,
                SpeakerLabels = "Alice", Text = $"{label} passage {i}.",
                Embedding = new Pgvector.Vector(Angle(fromTheta + i * step, axisA, axisB)),
            });
        await db.SaveChangesAsync();
    }

    /// <summary>Prices sequential scans out of the planner's reach, transaction-locally.
    ///
    /// <para>Postgres chooses by cost, and at test-data scale a sequential scan plus a top-N sort genuinely is
    /// cheaper than an HNSW index scan - on CI it costed the unfenced query at 18.64 and picked the scan. That
    /// is correct behaviour, and it is the whole reason this index only starts paying at library scale, but it
    /// makes "does the planner choose the index" unassertable on a test database without seeding tens of
    /// thousands of chunks. So these tests ask a sharper question: with sequential scans priced out, is the
    /// index <b>usable</b> by this query shape at all? The unfenced query must reach for it, and the fenced one
    /// must still refuse - which is a stronger statement about the fence than cost-based avoidance would be,
    /// because it holds even when the planner actively wants the index.</para></summary>
    private const string PriceOutSeqScans = "SET LOCAL enable_seqscan = off";

    private static async Task<List<string>> ExplainAsync(
        DiarizDbContext db, string sql, Action<DbCommand> bind, bool forceIndexPreference = true)
    {
        var conn = db.Database.GetDbConnection();
        var mustClose = conn.State != System.Data.ConnectionState.Open;
        if (mustClose) await conn.OpenAsync();
        try
        {
            await using var tx = await conn.BeginTransactionAsync();
            if (forceIndexPreference)
            {
                await using var set = conn.CreateCommand();
                set.Transaction = tx;
                set.CommandText = PriceOutSeqScans;
                await set.ExecuteNonQueryAsync();
            }
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "EXPLAIN " + sql;
            bind(cmd);
            var lines = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            await tx.CommitAsync();
            return lines;
        }
        finally
        {
            if (mustClose) await conn.CloseAsync();
        }
    }

    /// <summary>Every room in the database. The ANN path is only ever taken for a caller who can see most of
    /// the corpus (<c>TranscriptSearch</c> routes everyone else to the exact path), so the tests that examine
    /// that path have to bind the room filter the way it looks for such a caller. Binding one test user's own
    /// room instead makes the filter look selective - in a database several hundred tests have written to, the
    /// planner then estimates a handful of rows and correctly declines the index, which says nothing about
    /// whether the index works.</summary>
    private static async Task<Guid[]> AllRoomIdsAsync(DiarizDbContext db) =>
        await db.RoomRecordings.Select(rr => rr.RoomId).Distinct().ToArrayAsync();

    private static void Bind(DbCommand cmd, Guid[] roomIds, float[] query, int limit, Guid[]? scope)
    {
        cmd.Parameters.Add(new NpgsqlParameter("roomIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = roomIds });
        cmd.Parameters.Add(new NpgsqlParameter("qvec", NpgsqlDbType.Text) { Value = Literal(query) });
        cmd.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });
        if (scope is not null)
            cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = scope });
    }

    // ---- the index itself -------------------------------------------------------------------------------

    [Fact]
    public async Task TranscriptChunks_HaveAnHnswCosineIndex_OnTheEmbeddingColumn()
    {
        await using var db = fx.CreateDbContext();
        var defs = await db.Database
            .SqlQuery<string>($"""
                SELECT i.indexdef AS "Value" FROM pg_indexes i
                WHERE i.tablename = 'TranscriptChunks' AND i.indexdef ILIKE '%USING hnsw%'
                """)
            .ToListAsync();

        var def = Assert.Single(defs);
        // The opclass has to match the operator the query orders by (<=> is cosine). A vector_l2_ops index
        // would exist, look right, and never be used.
        Assert.Contains("vector_cosine_ops", def);
        Assert.Contains("\"Embedding\"", def);
    }

    // ---- what the two query shapes plan to --------------------------------------------------------------

    // These two are deliberately an A/B pair: identical SQL inputs, identical bindings, identical planner
    // settings, differing only in `exact`. That isolates the OFFSET 0 fence as the single cause of the plan
    // changing. Both bind every room, and both price out sequential scans (see PriceOutSeqScans), so neither
    // can pass for an incidental reason: delete the fence and the fenced test starts planning onto the index
    // and fails immediately.

    [Fact]
    public async Task UnfencedSemanticSql_CanPlanOntoTheAnnIndex()
    {
        var (userId, recId, trId) = await SeedRecording();
        await AddChunks(userId, recId, trId, 200, 0.01, 0.004, "Index");
        await using var db = fx.CreateDbContext();
        await Analyze(db);

        var roomIds = await AllRoomIdsAsync(db);

        var plan = string.Join("\n", await ExplainAsync(
            db, TranscriptSearch.BuildSemanticSql(hasScope: false, exact: false),
            cmd => Bind(cmd, roomIds, Angle(0), 10, null)));

        // The query shape, the opclass and the operator all line up, so the index is reachable by this query.
        // Whether the planner then picks it at a given size is its own cost decision - and correctly, it does
        // not until the table is large enough for the index to pay for itself.
        Assert.Contains("hnsw", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FencedSemanticSql_PlansAnExactScan_SoAFilteredSearchStaysTruthful()
    {
        var (userId, recId, trId) = await SeedRecording();
        await AddChunks(userId, recId, trId, 200, 0.01, 0.004, "Fence");
        await using var db = fx.CreateDbContext();
        await Analyze(db);

        var roomIds = await AllRoomIdsAsync(db);

        var plan = string.Join("\n", await ExplainAsync(
            db, TranscriptSearch.BuildSemanticSql(hasScope: false, exact: true),
            cmd => Bind(cmd, roomIds, Angle(0), 10, null)));

        // The fence must hold even here, where the filter is wide open and the planner has been told sequential
        // scans are prohibitive - that is, where it actively wants the index. Post-filtering an approximate walk
        // is what silently loses true neighbours once a real caller's filter is narrow.
        Assert.DoesNotContain("hnsw", plan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both tables the semantic query plans over. Without stats the planner guesses, and which path it
    /// guesses into is exactly what these tests assert.</summary>
    private static async Task Analyze(DiarizDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"TranscriptChunks\", \"RoomRecordings\", \"Recordings\"");

    // ---- recall ----------------------------------------------------------------------------------------

    [Fact]
    public async Task UnfencedSemanticSql_ReturnsTheSameNeighbours_AsTheExactScan()
    {
        // Recall of the ANN path, measured against the database's own exact answer rather than a hand-computed
        // expectation - the fenced query IS the exact answer, so this is a true recall check.
        //
        // On its own axis pair (6, 7), so that chunks other tests in this collection left behind sit at cosine
        // distance 1 and cannot displace any of these: the ANN path is being measured here, not the strategy
        // that decides when to take it.
        const int A = 6, B = 7;
        var (userId, recId, trId) = await SeedRecording();
        await AddChunks(userId, recId, trId, 300, 0.01, 0.004, "Recall", A, B);
        await using var db = fx.CreateDbContext();
        await Analyze(db);
        var roomIds = await AllRoomIdsAsync(db);
        var query = Angle(0, A, B);

        var annSql = TranscriptSearch.BuildSemanticSql(hasScope: false, exact: false);
        // Recall only means something if the approximate run actually went through the index. A seq scan would
        // match the exact answer trivially and the assertion below would prove nothing.
        var plan = string.Join("\n", await ExplainAsync(db, annSql, cmd => Bind(cmd, roomIds, query, 20, null)));
        Assert.Contains("hnsw", plan, StringComparison.OrdinalIgnoreCase);

        var approx = await StartMsAsync(db, annSql, roomIds, query, ann: true);
        var exact = await StartMsAsync(db, TranscriptSearch.BuildSemanticSql(hasScope: false, exact: true), roomIds, query, ann: false);

        Assert.Equal(20, exact.Count);
        Assert.Equal(exact, approx); // same neighbours, same order
    }

    /// <summary>Runs one of the two query shapes and returns the StartMs of each hit, in rank order.
    /// <paramref name="ann"/> applies the same <c>hnsw.ef_search</c> production applies on the ANN path, so the
    /// recall this measures is the recall users get - not the recall of pgvector's default of 40.</summary>
    private async Task<List<long>> StartMsAsync(
        DiarizDbContext db, string sql, Guid[] roomIds, float[] query, bool ann)
    {
        const int Limit = 20;
        var conn = db.Database.GetDbConnection();
        var mustClose = conn.State != System.Data.ConnectionState.Open;
        if (mustClose) await conn.OpenAsync();
        try
        {
            await using var tx = await conn.BeginTransactionAsync();
            if (ann)
            {
                await using var set = conn.CreateCommand();
                set.Transaction = tx;
                // ef_search exactly as production sets it, so the recall measured is the recall users get -
                // plus the same seq-scan pricing the plan tests use, so the run really does go through the
                // index. Measuring "recall" of a sequential scan would be a tautology.
                set.CommandText = $"SET LOCAL hnsw.ef_search = {TranscriptSearch.EfSearch(Limit)}; {PriceOutSeqScans}";
                await set.ExecuteNonQueryAsync();
            }
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            Bind(cmd, roomIds, query, Limit, null);
            var rows = new List<long>();
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync()) rows.Add(reader.GetInt64(3));
            await tx.CommitAsync();
            return rows;
        }
        finally
        {
            if (mustClose) await conn.CloseAsync();
        }
    }

    // ---- the two filters, end to end through SearchAsync ------------------------------------------------

    [Fact]
    public async Task Search_WithARecordingScope_StillFindsItsChunks_WhenOtherRecordingsRankCloser()
    {
        // The post-filter trap: 400 chunks in recording B all rank nearer the query than anything in A, and
        // the caller scopes to A. An HNSW walk that stops after ef_search candidates would never reach A's
        // chunks and would return nothing at all.
        var (userId, recA, trA) = await SeedRecording(name: "A");
        await AddChunks(userId, recA, trA, 5, 1.0, 0.01, "Wanted"); // far from the query
        var (_, recB, trB) = await SeedRecording(userId, name: "B");
        await AddChunks(userId, recB, trB, 400, 0.001, 0.002, "Decoy"); // all much nearer

        await using var db = fx.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"TranscriptChunks\"");
        var hits = await Search(db, Angle(0)).SearchAsync(userId, "wanted passage", null, [recA], 20);

        Assert.Equal(5, hits.Count(h => h.RecordingId == recA));
        Assert.All(hits, h => Assert.Equal(recA, h.RecordingId));
    }

    [Fact]
    public async Task Search_WhenAnotherUsersChunksRankCloser_StillFindsTheCallersOwn()
    {
        // The same trap via the room filter rather than an explicit scope: a busy multi-tenant instance where
        // the caller can see only a sliver of the corpus. Their own chunks must not be starved out.
        var (mine, recMine, trMine) = await SeedRecording(name: "Mine");
        await AddChunks(mine, recMine, trMine, 5, 1.0, 0.01, "Mine"); // far from the query
        var (theirs, recTheirs, trTheirs) = await SeedRecording(name: "Theirs");
        await AddChunks(theirs, recTheirs, trTheirs, 400, 0.001, 0.002, "Theirs"); // all much nearer

        await using var db = fx.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"TranscriptChunks\"");
        var hits = await Search(db, Angle(0)).SearchAsync(mine, "mine passage", null, null, 20);

        Assert.Equal(5, hits.Count(h => h.RecordingId == recMine));
        Assert.DoesNotContain(hits, h => h.RecordingId == recTheirs);
    }
}
