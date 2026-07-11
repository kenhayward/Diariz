using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A single transcript match: the standard When / Who / What plus a relevance score.</summary>
public sealed record TranscriptHit(
    Guid RecordingId, string RecordingName, DateTimeOffset RecordingCreatedAt,
    long StartMs, string SpeakerName, string Text, double Similarity);

/// <summary>A recording-level result for <c>list_recordings</c>. <paramref name="BestSnippet"/> is the
/// best-matching segment text when a <c>contains</c> topic filter was used; otherwise null.</summary>
public sealed record RecordingHit(
    Guid RecordingId, string RecordingName, DateTimeOffset RecordingCreatedAt,
    string Source, long DurationMs, IReadOnlyList<string> Speakers, string? BestSnippet);

/// <summary>An exact per-speaker mention count (used by <c>count_mentions</c>).</summary>
public sealed record SpeakerCount(string Speaker, int Count);

/// <summary>An exact per-speaker talking duration in milliseconds (used by <c>speaker_talk_time</c>).</summary>
public sealed record SpeakerDuration(string Speaker, long Ms);

/// <summary>Hybrid transcript search over the user's recordings: a lexical arm (Postgres pg_trgm word-similarity
/// over segments) fused with a semantic arm (pgvector cosine KNN over <c>TranscriptChunk</c> embeddings) via
/// Reciprocal Rank Fusion. Always scoped to the owning user and the current (highest-version) transcription. When
/// no embeddings endpoint is configured (or the query embedding fails, or a speaker filter is set), the semantic
/// arm is skipped and results are pure lexical - identical to the pre-M3 behaviour. Postgres-only (raw SQL) -
/// verified by integration tests; faked in unit tests.</summary>
public interface ITranscriptSearch
{
    /// <summary>Segments/passages whose text matches <paramref name="phrase"/> lexically and/or semantically,
    /// ranked by fused relevance. Optionally restricted to <paramref name="recordingScope"/> and a
    /// <paramref name="speakerName"/> (a speaker filter forces the lexical-only path).</summary>
    Task<IReadOnlyList<TranscriptHit>> SearchAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, int limit, CancellationToken ct = default);

    /// <summary>Recordings filtered by optional date range / name / speaker / <paramref name="contains"/> topic.
    /// When <paramref name="contains"/> is set, only recordings whose transcript fuzzy-matches it are returned,
    /// ranked by the best segment match (which is surfaced as the snippet).</summary>
    Task<IReadOnlyList<RecordingHit>> ListRecordingsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, string? name, string? speaker,
        string? contains, int limit, CancellationToken ct = default);

    /// <summary>The <b>exact</b> number of segments mentioning <paramref name="phrase"/> (same fuzzy trigram
    /// match as <see cref="SearchAsync"/>), grouped by speaker - <b>no cap</b>, so counts are truthful.
    /// Optionally restricted to <paramref name="speakerName"/> and <paramref name="recordingScope"/>.</summary>
    Task<IReadOnlyList<SpeakerCount>> CountMentionsAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default);

    /// <summary>Total talking time per speaker (summed segment durations of the current transcription),
    /// owner-scoped and optionally restricted to <paramref name="recordingScope"/>. Aggregated in SQL over
    /// <b>all</b> in-scope recordings - <b>no cap</b> - so totals and percentages are correct.</summary>
    Task<IReadOnlyList<SpeakerDuration>> SpeakerTalkTimeAsync(
        Guid userId, IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default);
}

public sealed class TranscriptSearch : ITranscriptSearch
{
    /// <summary>Hard cap on rows a single passage-retrieval tool call returns (protects the chat context
    /// budget). Counting/aggregation tools (count_mentions, speaker_talk_time) are exact and ignore this.</summary>
    public const int MaxLimit = 50;

    /// <summary>word_similarity threshold (0..1): the minimum trigram word-similarity for a match. Lower is
    /// fuzzier. 0.3 is lenient enough to survive typos/partial phrases without flooding with noise.</summary>
    private const double Threshold = 0.3;

    /// <summary>The current-transcription guard, reused by both queries.</summary>
    private const string CurrentVersion =
        "t.\"Version\" = (SELECT MAX(t2.\"Version\") FROM \"Transcriptions\" t2 WHERE t2.\"RecordingId\" = r.\"Id\")";

    /// <summary>The read gate: a recording is visible when it is placed in one of the caller's rooms (their
    /// personal room plus any shared room they belong to). Replaces the old <c>r."UserId" = @userId</c> so a
    /// recording shared into a room the searcher is in is searchable. Needs <c>@roomIds uuid[]</c>.</summary>
    private const string RecordingInCallersRooms =
        "EXISTS (SELECT 1 FROM \"RoomRecordings\" rr WHERE rr.\"RecordingId\" = r.\"Id\" AND rr.\"RoomId\" = ANY(@roomIds))";

    /// <summary>The same gate keyed on a chunk's recording (the semantic arm joins on <c>c."RecordingId"</c>).</summary>
    private const string ChunkInCallersRooms =
        "EXISTS (SELECT 1 FROM \"RoomRecordings\" rr WHERE rr.\"RecordingId\" = c.\"RecordingId\" AND rr.\"RoomId\" = ANY(@roomIds))";

    private readonly DiarizDbContext _db;
    private readonly IEmbeddingClient _embeddings;
    private readonly IEmbeddingSettingsResolver _embeddingSettings;
    private readonly IRoomScope _rooms;

    public TranscriptSearch(
        DiarizDbContext db, IEmbeddingClient embeddings, IEmbeddingSettingsResolver embeddingSettings,
        IRoomScope rooms)
    {
        _db = db;
        _embeddings = embeddings;
        _embeddingSettings = embeddingSettings;
        _rooms = rooms;
    }

    public async Task<IReadOnlyList<TranscriptHit>> SearchAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, int limit, CancellationToken ct = default)
    {
        phrase = (phrase ?? "").Trim();
        if (phrase.Length == 0) return [];
        limit = Math.Clamp(limit, 1, MaxLimit);
        var scope = recordingScope is { Count: > 0 } ? recordingScope.Distinct().ToArray() : null;
        var hasSpeaker = !string.IsNullOrWhiteSpace(speakerName);
        var roomIds = (await _rooms.RoomIdsForUserAsync(userId, ct)).ToArray();

        var lexical = await LexicalSearchAsync(roomIds, phrase, speakerName, scope, limit, hasSpeaker, ct);

        // Semantic arm: only when embeddings are on and no speaker filter (a chunk spans multiple speakers, so
        // speaker-scoped queries stay lexical). Any failure degrades gracefully to lexical-only.
        var semantic = hasSpeaker ? [] : await SemanticSearchAsync(userId, roomIds, phrase, scope, limit, ct);

        // Off / no semantic hits → return today's exact lexical result (unchanged scores + order).
        return semantic.Count == 0 ? lexical : SearchFusion.Fuse(lexical, semantic, limit);
    }

    private async Task<IReadOnlyList<TranscriptHit>> LexicalSearchAsync(
        Guid[] roomIds, string phrase, string? speakerName, Guid[]? scope, int limit, bool hasSpeaker,
        CancellationToken ct)
    {
        var sql = new StringBuilder();
        sql.Append(
            "SELECT r.\"Id\", COALESCE(r.\"Name\", r.\"Title\"), r.\"CreatedAt\", s.\"StartMs\", " +
            "COALESCE(sp.\"DisplayName\", s.\"SpeakerLabel\"), COALESCE(s.\"Revised\", s.\"Original\"), " +
            "word_similarity(@phrase, COALESCE(s.\"Revised\", s.\"Original\")) AS sim " +
            "FROM \"Segments\" s " +
            "JOIN \"Transcriptions\" t ON t.\"Id\" = s.\"TranscriptionId\" " +
            "JOIN \"Recordings\" r ON r.\"Id\" = t.\"RecordingId\" " +
            "LEFT JOIN \"Speakers\" sp ON sp.\"RecordingId\" = r.\"Id\" AND sp.\"Label\" = s.\"SpeakerLabel\" " +
            "WHERE " + RecordingInCallersRooms + " AND " + CurrentVersion +
            " AND COALESCE(s.\"Revised\", s.\"Original\") %> @phrase");
        if (scope is not null) sql.Append(" AND r.\"Id\" = ANY(@scope)");
        if (hasSpeaker) sql.Append(" AND sp.\"DisplayName\" %> @speaker");
        sql.Append(" ORDER BY sim DESC, r.\"CreatedAt\" DESC LIMIT @limit");

        return await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "roomIds", roomIds);
            Add(cmd, "phrase", phrase);
            Add(cmd, "limit", limit);
            if (scope is not null) Add(cmd, "scope", scope);
            if (hasSpeaker) Add(cmd, "speaker", speakerName!.Trim());

            var hits = new List<TranscriptHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new TranscriptHit(
                    reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<float>(6)));
            return (IReadOnlyList<TranscriptHit>)hits;
        });
    }

    /// <summary>Vector arm: embeds the query and finds the nearest transcript chunks by pgvector cosine distance
    /// (<c>&lt;=&gt;</c>), owner-scoped and optionally restricted to <paramref name="scope"/>. Returns [] (and the
    /// caller falls back to lexical-only) when embeddings are unconfigured or the query embedding fails.</summary>
    private async Task<IReadOnlyList<TranscriptHit>> SemanticSearchAsync(
        Guid userId, Guid[] roomIds, string phrase, Guid[]? scope, int limit, CancellationToken ct)
    {
        var cfg = await _embeddingSettings.ResolveAsync(userId, ct);
        if (!cfg.Enabled) return [];

        string queryLiteral;
        try
        {
            // Prefix the query with the model's query task instruction (nomic: "search_query: "); empty for
            // models that don't use prefixes. Must pair with the document prefix used when embedding chunks.
            var vectors = await _embeddings.EmbedAsync(cfg, [cfg.QueryPrefix + phrase], ct);
            if (vectors.Count == 0 || vectors[0] is not { Length: > 0 } vec) return [];
            queryLiteral = "[" + string.Join(",", vec.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";
        }
        catch
        {
            // Embedding endpoint down/misconfigured → degrade to lexical-only rather than failing the search.
            return [];
        }

        var sql = new StringBuilder();
        sql.Append(
            "SELECT c.\"RecordingId\", COALESCE(r.\"Name\", r.\"Title\"), r.\"CreatedAt\", c.\"StartMs\", " +
            "c.\"SpeakerLabels\", c.\"Text\", 1 - (c.\"Embedding\" <=> @qvec::vector) AS sim " +
            "FROM \"TranscriptChunks\" c " +
            "JOIN \"Recordings\" r ON r.\"Id\" = c.\"RecordingId\" " +
            "WHERE " + ChunkInCallersRooms + " AND c.\"Embedding\" IS NOT NULL");
        if (scope is not null) sql.Append(" AND c.\"RecordingId\" = ANY(@scope)");
        sql.Append(" ORDER BY c.\"Embedding\" <=> @qvec::vector LIMIT @limit");

        return await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "roomIds", roomIds);
            Add(cmd, "qvec", queryLiteral);
            Add(cmd, "limit", limit);
            if (scope is not null) Add(cmd, "scope", scope);

            var hits = new List<TranscriptHit>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new TranscriptHit(
                    reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<double>(6)));
            return (IReadOnlyList<TranscriptHit>)hits;
        });
    }

    public async Task<IReadOnlyList<RecordingHit>> ListRecordingsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, string? name, string? speaker,
        string? contains, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var hasContains = !string.IsNullOrWhiteSpace(contains);
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasSpeaker = !string.IsNullOrWhiteSpace(speaker);
        var roomIds = (await _rooms.RoomIdsForUserAsync(userId, ct)).ToArray();

        var sql = new StringBuilder();
        sql.Append("SELECT r.\"Id\", COALESCE(r.\"Name\", r.\"Title\"), r.\"CreatedAt\", r.\"Source\", r.\"DurationMs\"");
        if (hasContains)
            sql.Append(", best.snippet, best.sim");
        sql.Append(" FROM \"Recordings\" r");
        if (hasContains)
            sql.Append(
                " JOIN LATERAL (SELECT COALESCE(s.\"Revised\", s.\"Original\") AS snippet, " +
                "word_similarity(@contains, COALESCE(s.\"Revised\", s.\"Original\")) AS sim " +
                "FROM \"Segments\" s JOIN \"Transcriptions\" t ON t.\"Id\" = s.\"TranscriptionId\" " +
                "WHERE t.\"RecordingId\" = r.\"Id\" AND " + CurrentVersion +
                " AND COALESCE(s.\"Revised\", s.\"Original\") %> @contains " +
                "ORDER BY sim DESC LIMIT 1) best ON TRUE");
        sql.Append(" WHERE " + RecordingInCallersRooms);
        if (from is not null) sql.Append(" AND r.\"CreatedAt\" >= @from");
        if (to is not null) sql.Append(" AND r.\"CreatedAt\" <= @to");
        if (hasName) sql.Append(" AND COALESCE(r.\"Name\", r.\"Title\") ILIKE @nameLike");
        if (hasSpeaker)
            sql.Append(
                " AND EXISTS (SELECT 1 FROM \"Speakers\" sp WHERE sp.\"RecordingId\" = r.\"Id\" " +
                "AND sp.\"DisplayName\" %> @speaker)");
        sql.Append(hasContains
            ? " ORDER BY best.sim DESC, r.\"CreatedAt\" DESC"
            : " ORDER BY r.\"CreatedAt\" DESC");
        sql.Append(" LIMIT @limit");

        var rows = await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "roomIds", roomIds);
            Add(cmd, "limit", limit);
            if (from is not null) Add(cmd, "from", from.Value);
            if (to is not null) Add(cmd, "to", to.Value);
            if (hasName) Add(cmd, "nameLike", "%" + Like(name!.Trim()) + "%");
            if (hasSpeaker) Add(cmd, "speaker", speaker!.Trim());
            if (hasContains) Add(cmd, "contains", contains!.Trim());

            var list = new List<(Guid Id, string Name, DateTimeOffset CreatedAt, int Source, long Dur, string? Snippet)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add((
                    reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetInt32(3), reader.GetInt64(4),
                    hasContains ? reader.GetString(5) : null));
            return list;
        });

        if (rows.Count == 0) return [];

        // Resolve speaker names for the matched recordings in one query (avoids SQL aggregation).
        var ids = rows.Select(r => r.Id).ToList();
        var speakers = await _db.Speakers
            .Where(s => ids.Contains(s.RecordingId))
            .Select(s => new { s.RecordingId, s.DisplayName })
            .ToListAsync(ct);
        var byRecording = speakers
            .GroupBy(s => s.RecordingId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.DisplayName).Distinct().ToList());

        return rows.Select(r => new RecordingHit(
            r.Id, r.Name, r.CreatedAt,
            ((RecordingSource)r.Source).ToString(), r.Dur,
            byRecording.TryGetValue(r.Id, out var sp) ? sp : [],
            r.Snippet)).ToList();
    }

    public async Task<IReadOnlyList<SpeakerCount>> CountMentionsAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default)
    {
        phrase = (phrase ?? "").Trim();
        if (phrase.Length == 0) return [];
        var scope = recordingScope is { Count: > 0 } ? recordingScope.Distinct().ToArray() : null;
        var hasSpeaker = !string.IsNullOrWhiteSpace(speakerName);
        var roomIds = (await _rooms.RoomIdsForUserAsync(userId, ct)).ToArray();

        // Same lexical match as SearchAsync (the %> trigram operator) - so "mention" stays consistent with
        // search - but COUNT(*) grouped by speaker with NO LIMIT: an exact, truthful count.
        var sql = new StringBuilder();
        sql.Append(
            "SELECT COALESCE(sp.\"DisplayName\", s.\"SpeakerLabel\") AS who, COUNT(*) AS n " +
            "FROM \"Segments\" s " +
            "JOIN \"Transcriptions\" t ON t.\"Id\" = s.\"TranscriptionId\" " +
            "JOIN \"Recordings\" r ON r.\"Id\" = t.\"RecordingId\" " +
            "LEFT JOIN \"Speakers\" sp ON sp.\"RecordingId\" = r.\"Id\" AND sp.\"Label\" = s.\"SpeakerLabel\" " +
            "WHERE " + RecordingInCallersRooms + " AND " + CurrentVersion +
            " AND COALESCE(s.\"Revised\", s.\"Original\") %> @phrase");
        if (scope is not null) sql.Append(" AND r.\"Id\" = ANY(@scope)");
        if (hasSpeaker) sql.Append(" AND sp.\"DisplayName\" %> @speaker");
        sql.Append(" GROUP BY who ORDER BY n DESC");

        return await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "roomIds", roomIds);
            Add(cmd, "phrase", phrase);
            if (scope is not null) Add(cmd, "scope", scope);
            if (hasSpeaker) Add(cmd, "speaker", speakerName!.Trim());

            var rows = new List<SpeakerCount>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new SpeakerCount(reader.GetString(0), checked((int)reader.GetInt64(1))));
            return (IReadOnlyList<SpeakerCount>)rows;
        });
    }

    public async Task<IReadOnlyList<SpeakerDuration>> SpeakerTalkTimeAsync(
        Guid userId, IReadOnlyList<Guid>? recordingScope, CancellationToken ct = default)
    {
        var scope = recordingScope is { Count: > 0 } ? recordingScope.Distinct().ToArray() : null;
        var roomIds = (await _rooms.RoomIdsForUserAsync(userId, ct)).ToArray();

        // Sum segment durations of the current transcription per speaker over ALL in-scope recordings - no cap,
        // so the totals and percentages the tool computes are correct regardless of library size.
        var sql = new StringBuilder();
        sql.Append(
            "SELECT COALESCE(sp.\"DisplayName\", s.\"SpeakerLabel\") AS who, " +
            "SUM(GREATEST(s.\"EndMs\" - s.\"StartMs\", 0)) AS ms " +
            "FROM \"Segments\" s " +
            "JOIN \"Transcriptions\" t ON t.\"Id\" = s.\"TranscriptionId\" " +
            "JOIN \"Recordings\" r ON r.\"Id\" = t.\"RecordingId\" " +
            "LEFT JOIN \"Speakers\" sp ON sp.\"RecordingId\" = r.\"Id\" AND sp.\"Label\" = s.\"SpeakerLabel\" " +
            "WHERE " + RecordingInCallersRooms + " AND " + CurrentVersion);
        if (scope is not null) sql.Append(" AND r.\"Id\" = ANY(@scope)");
        sql.Append(" GROUP BY who ORDER BY ms DESC");

        return await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "roomIds", roomIds);
            if (scope is not null) Add(cmd, "scope", scope);

            var rows = new List<SpeakerDuration>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new SpeakerDuration(reader.GetString(0), reader.GetInt64(1)));
            return (IReadOnlyList<SpeakerDuration>)rows;
        });
    }

    /// <summary>Opens a connection + transaction, applies the trigram threshold for this query, and runs the
    /// supplied reader. The threshold is set transaction-locally so the <c>%&gt;</c> operator can use the GIN
    /// index while matching our chosen sensitivity. (Harmless for the vector query, which ignores it.)</summary>
    private async Task<T> RunAsync<T>(CancellationToken ct, Func<DbCommand, Task<T>> run)
    {
        var conn = _db.Database.GetDbConnection();
        var mustClose = conn.State != ConnectionState.Open;
        if (mustClose) await conn.OpenAsync(ct);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var set = conn.CreateCommand())
            {
                set.Transaction = tx;
                set.CommandText = "SELECT set_config('pg_trgm.word_similarity_threshold', @t, true)";
                Add(set, "t", Threshold.ToString(CultureInfo.InvariantCulture));
                await set.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var result = await run(cmd);
            await tx.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (mustClose) await conn.CloseAsync();
        }
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Escapes the LIKE wildcards in user input so a name filter is treated literally.</summary>
    private static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
