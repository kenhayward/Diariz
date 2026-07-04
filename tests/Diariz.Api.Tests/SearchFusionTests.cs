using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SearchFusionTests
{
    private static TranscriptHit Hit(Guid rec, long startMs, string text = "x", double sim = 0.5) =>
        new(rec, "Rec", DateTimeOffset.UnixEpoch, startMs, "Alice", text, sim);

    [Fact]
    public void Fuse_EmptyBoth_ReturnsEmpty()
    {
        Assert.Empty(SearchFusion.Fuse([], [], limit: 10));
    }

    [Fact]
    public void Fuse_LexicalOnly_PreservesOrder()
    {
        var rec = Guid.NewGuid();
        var lexical = new[] { Hit(rec, 0), Hit(rec, 1000), Hit(rec, 2000) };

        var fused = SearchFusion.Fuse(lexical, [], limit: 10);

        Assert.Equal([0L, 1000L, 2000L], fused.Select(h => h.StartMs).ToArray());
    }

    [Fact]
    public void Fuse_IncludesSemanticOnlyItems()
    {
        var rec = Guid.NewGuid();
        var lexical = new[] { Hit(rec, 0) };
        var semantic = new[] { Hit(rec, 5000, "semantic-only") };

        var fused = SearchFusion.Fuse(lexical, semantic, limit: 10);

        Assert.Contains(fused, h => h.StartMs == 5000);
        Assert.Contains(fused, h => h.StartMs == 0);
    }

    [Fact]
    public void Fuse_RanksItemInBothArmsHighest()
    {
        var rec = Guid.NewGuid();
        // 'shared' (StartMs 1000) is rank-1 lexical and rank-0 semantic → it gets both contributions and wins.
        var lexical = new[] { Hit(rec, 0, "lex-top"), Hit(rec, 1000, "shared") };
        var semantic = new[] { Hit(rec, 1000, "shared"), Hit(rec, 9000, "sem-top") };

        var fused = SearchFusion.Fuse(lexical, semantic, limit: 10);

        Assert.Equal(1000, fused[0].StartMs); // appears in both lists → highest fused score
    }

    [Fact]
    public void Fuse_DedupsByRecordingAndStart_KeepingLexicalHit()
    {
        var rec = Guid.NewGuid();
        var lexical = new[] { Hit(rec, 1000, "the exact segment text") };
        var semantic = new[] { Hit(rec, 1000, "the whole chunk passage") };

        var fused = SearchFusion.Fuse(lexical, semantic, limit: 10);

        var hit = Assert.Single(fused);
        Assert.Equal("the exact segment text", hit.Text); // lexical (segment-precise) wins the dedup
    }

    [Fact]
    public void Fuse_SameStartDifferentRecordings_AreNotMerged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var fused = SearchFusion.Fuse([Hit(a, 1000)], [Hit(b, 1000)], limit: 10);

        Assert.Equal(2, fused.Count); // keyed by (RecordingId, StartMs), so different recordings stay distinct
    }

    [Fact]
    public void Fuse_HonoursLimit()
    {
        var rec = Guid.NewGuid();
        var lexical = Enumerable.Range(0, 30).Select(i => Hit(rec, i * 1000)).ToArray();

        var fused = SearchFusion.Fuse(lexical, [], limit: 5);

        Assert.Equal(5, fused.Count);
    }
}
