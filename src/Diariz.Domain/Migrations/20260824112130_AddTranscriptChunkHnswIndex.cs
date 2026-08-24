using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>Adds the ANN index behind semantic transcript search (issue #594). Until now every semantic
    /// search sequentially scanned every chunk and detoasted every vector - a vector(768) is 3,076 bytes and
    /// pgvector stores the type EXTERNAL, so each one is reassembled from TOAST before a distance is computed.
    ///
    /// <para>NOT built CONCURRENTLY: EF runs each migration in a transaction and CREATE INDEX CONCURRENTLY
    /// cannot run inside one. A plain build takes a SHARE lock, so chunk writes (transcription callbacks and
    /// the embedding backfill) block while it runs - seconds on a small corpus, longer on a large one. The API
    /// applies migrations at startup, before it serves traffic, so this is startup latency rather than a
    /// user-visible stall.</para></summary>
    public partial class AddTranscriptChunkHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TranscriptChunks_Embedding",
                table: "TranscriptChunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TranscriptChunks_Embedding",
                table: "TranscriptChunks");
        }
    }
}
