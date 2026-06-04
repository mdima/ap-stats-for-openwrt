using ApStatsForOpenWrt.Data.Repositories.History;
using Xunit;

namespace ApStatsForOpenWrt.Tests.Data.Repositories.History;

/// <summary>
/// Phase 19 (L-02 / L-03) — direct unit coverage of the shared <see cref="HistoryBuckets"/> grid
/// helper. The query-service tests exercise the happy paths through SQLite; these target the helper's
/// edge branches (out-of-grid both sides, SpecifyKind, floor-to-boundary) so the ≥80% line+branch gate
/// holds on the shared helper too. <c>HistoryBuckets</c> is internal (InternalsVisibleTo).
/// </summary>
public sealed class HistoryBucketsTests
{
    private static readonly TimeSpan Span = TimeSpan.FromHours(1);

    [Fact]
    public void BucketIndex_AnchorBucket_IsLastIndex()
    {
        var anchor = new DateTime(2026, 6, 2, 10, 2, 0, DateTimeKind.Utc);
        var grid = HistoryBuckets.BuildGrid(anchor, Span);

        var idx = HistoryBuckets.BucketIndex(anchor, anchor, Span);

        Assert.Equal(grid.Count - 1, idx);
    }

    [Fact]
    public void BucketIndex_TooOld_ReturnsMinusOne()
    {
        var anchor = new DateTime(2026, 6, 2, 10, 2, 0, DateTimeKind.Utc);
        // Two hours before the anchor — older than the 1 h grid's first slot.
        var idx = HistoryBuckets.BucketIndex(anchor.AddHours(-2), anchor, Span);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void BucketIndex_TooNew_ReturnsMinusOne()
    {
        var anchor = new DateTime(2026, 6, 2, 10, 2, 0, DateTimeKind.Utc);
        // After the anchor slot — newer than the last bucket.
        var idx = HistoryBuckets.BucketIndex(anchor.AddMinutes(10), anchor, Span);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void FloorToBucket_RoundsDownToFiveMinuteBoundary()
    {
        var t = new DateTime(2026, 6, 2, 10, 7, 42, DateTimeKind.Utc);
        var floored = HistoryBuckets.FloorToBucket(t);

        Assert.Equal(new DateTime(2026, 6, 2, 10, 5, 0, DateTimeKind.Utc), floored);
        Assert.Equal(DateTimeKind.Utc, floored.Kind);
    }

    [Fact]
    public void AsUtc_ForcesUtcKind_OnUnspecified()
    {
        var unspecified = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = HistoryBuckets.AsUtc(unspecified);

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void FoldLastValue_OutOfGridRow_IsSkipped()
    {
        var anchor = new DateTime(2026, 6, 2, 10, 2, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new Row(anchor),                 // in grid (anchor bucket)
            new Row(anchor.AddHours(-5)),    // far out of the 1 h grid — must be skipped
        };

        var folded = HistoryBuckets.FoldLastValue(rows, r => r.Ts, anchor, Span);

        Assert.NotNull(folded[^1]);          // anchor row landed
        Assert.Equal(anchor, folded[^1]!.Ts);
        Assert.All(folded.Take(folded.Length - 1), slot => Assert.Null(slot)); // nothing else
    }

    private sealed record Row(DateTime Ts);
}
