using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A single transcript-segment match: the standard When / Who / What plus a relevance score.</summary>
public sealed record TranscriptHit(
    Guid RecordingId, string RecordingName, DateTimeOffset RecordingCreatedAt,
    long StartMs, string SpeakerName, string Text, double Similarity);

/// <summary>A recording-level result for <c>list_recordings</c>. <paramref name="BestSnippet"/> is the
/// best-matching segment text when a <c>contains</c> topic filter was used; otherwise null.</summary>
public sealed record RecordingHit(
    Guid RecordingId, string RecordingName, DateTimeOffset RecordingCreatedAt,
    string Source, long DurationMs, IReadOnlyList<string> Speakers, string? BestSnippet);

/// <summary>Fuzzy transcript search over the user's recordings, backed by the Postgres pg_trgm GIN index on
/// <c>coalesce("Revised","Original")</c>. Always scoped to the owning user and the current (highest-version)
/// transcription. Postgres-only (raw SQL) — verified by integration tests; faked in unit tests.</summary>
public interface ITranscriptSearch
{
    /// <summary>Segments whose effective text fuzzy-matches <paramref name="phrase"/>, ranked by similarity.
    /// Optionally restricted to <paramref name="recordingScope"/> and a <paramref name="speakerName"/>.</summary>
    Task<IReadOnlyList<TranscriptHit>> SearchAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, int limit, CancellationToken ct = default);

    /// <summary>Recordings filtered by optional date range / name / speaker / <paramref name="contains"/> topic.
    /// When <paramref name="contains"/> is set, only recordings whose transcript fuzzy-matches it are returned,
    /// ranked by the best segment match (which is surfaced as the snippet).</summary>
    Task<IReadOnlyList<RecordingHit>> ListRecordingsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, string? name, string? speaker,
        string? contains, int limit, CancellationToken ct = default);
}

public sealed class TranscriptSearch : ITranscriptSearch
{
    /// <summary>Hard cap on rows a single tool call returns (protects the chat context budget).</summary>
    public const int MaxLimit = 20;

    /// <summary>word_similarity threshold (0..1): the minimum trigram word-similarity for a match. Lower is
    /// fuzzier. 0.3 is lenient enough to survive typos/partial phrases without flooding with noise.</summary>
    private const double Threshold = 0.3;

    /// <summary>The current-transcription guard, reused by both queries.</summary>
    private const string CurrentVersion =
        "t.\"Version\" = (SELECT MAX(t2.\"Version\") FROM \"Transcriptions\" t2 WHERE t2.\"RecordingId\" = r.\"Id\")";

    private readonly DiarizDbContext _db;

    public TranscriptSearch(DiarizDbContext db) => _db = db;

    public async Task<IReadOnlyList<TranscriptHit>> SearchAsync(
        Guid userId, string phrase, string? speakerName,
        IReadOnlyList<Guid>? recordingScope, int limit, CancellationToken ct = default)
    {
        phrase = (phrase ?? "").Trim();
        if (phrase.Length == 0) return [];
        limit = Math.Clamp(limit, 1, MaxLimit);
        var scope = recordingScope is { Count: > 0 } ? recordingScope.Distinct().ToArray() : null;
        var hasSpeaker = !string.IsNullOrWhiteSpace(speakerName);

        var sql = new StringBuilder();
        sql.Append(
            "SELECT r.\"Id\", COALESCE(r.\"Name\", r.\"Title\"), r.\"CreatedAt\", s.\"StartMs\", " +
            "COALESCE(sp.\"DisplayName\", s.\"SpeakerLabel\"), COALESCE(s.\"Revised\", s.\"Original\"), " +
            "word_similarity(@phrase, COALESCE(s.\"Revised\", s.\"Original\")) AS sim " +
            "FROM \"Segments\" s " +
            "JOIN \"Transcriptions\" t ON t.\"Id\" = s.\"TranscriptionId\" " +
            "JOIN \"Recordings\" r ON r.\"Id\" = t.\"RecordingId\" " +
            "LEFT JOIN \"Speakers\" sp ON sp.\"RecordingId\" = r.\"Id\" AND sp.\"Label\" = s.\"SpeakerLabel\" " +
            "WHERE r.\"UserId\" = @userId AND " + CurrentVersion +
            " AND COALESCE(s.\"Revised\", s.\"Original\") %> @phrase");
        if (scope is not null) sql.Append(" AND r.\"Id\" = ANY(@scope)");
        if (hasSpeaker) sql.Append(" AND sp.\"DisplayName\" %> @speaker");
        sql.Append(" ORDER BY sim DESC, r.\"CreatedAt\" DESC LIMIT @limit");

        return await RunAsync(ct, async cmd =>
        {
            cmd.CommandText = sql.ToString();
            Add(cmd, "userId", userId);
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

    public async Task<IReadOnlyList<RecordingHit>> ListRecordingsAsync(
        Guid userId, DateTimeOffset? from, DateTimeOffset? to, string? name, string? speaker,
        string? contains, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var hasContains = !string.IsNullOrWhiteSpace(contains);
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasSpeaker = !string.IsNullOrWhiteSpace(speaker);

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
        sql.Append(" WHERE r.\"UserId\" = @userId");
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
            Add(cmd, "userId", userId);
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

    /// <summary>Opens a connection + transaction, applies the trigram threshold for this query, and runs the
    /// supplied reader. The threshold is set transaction-locally so the <c>%&gt;</c> operator can use the GIN
    /// index while matching our chosen sensitivity.</summary>
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
