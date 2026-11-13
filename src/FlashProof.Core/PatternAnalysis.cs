namespace FlashProof.Core;

/// <summary>Where BLANK/STALE records start on the disk, and how many tested bytes lie below that point.</summary>
public sealed record Boundary(long DiskOffset, long TestedBytesBelow);

/// <summary>
/// Looks for the two patterns that mark a fake (design §3 and §6.1): a consistent wrap size among MISPLACED records,
/// and BLANK/STALE records from a clear boundary onward.
/// </summary>
public static class PatternAnalysis
{
    /// <summary>Percentage of MISPLACED records that must agree on one wrap size.</summary>
    public const int WrapAgreementPercent = 90;

    /// <summary>Share of all BLANK/STALE bytes that must lie at or above the boundary.</summary>
    public const double BoundaryConcentration = 0.90;

    /// <summary>
    /// Share of the tested bytes above the boundary that must be BLANK/STALE. It is lower than the others because a
    /// RAM-cache fake still returns its most recent writes correctly.
    /// </summary>
    public const double BoundaryTailBlank = 0.50;

    /// <summary>
    /// Returns the wrap size, or null if the MISPLACED records show no consistent one.
    /// </summary>
    /// <param name="misplacedDeltas">
    /// For each MISPLACED record, the distance on disk between where it was found and where it was written,
    /// counted by distance. Zero means the distance is unknown.
    /// </param>
    /// <param name="okRecords">Records that verified OK.</param>
    /// <remarks>
    /// A record found k wraps away is k × the wrap size away, so a distance agrees with a wrap size when it is a
    /// multiple of it. A wrap size is only plausible if it could hold every record that verified OK: a drive with
    /// m bytes of real flash can't return more than m bytes correctly. That rules out a few misdirected writes on
    /// a genuine but failing drive, which can agree with each other by chance.
    /// </remarks>
    public static long? FindWrapSize(IReadOnlyDictionary<long, long> misplacedDeltas, long okRecords)
    {
        long total = misplacedDeltas.Values.Sum();
        if (total == 0)
            return null;
        foreach (long size in misplacedDeltas.Keys.Where(d => d > 0).Order())
        {
            if (okRecords * TestRecord.Size > size)
                continue;
            long agreeing = misplacedDeltas.Where(kv => kv.Key > 0 && kv.Key % size == 0).Sum(kv => kv.Value);
            if (agreeing * 100 >= total * WrapAgreementPercent)
                return size;
        }
        return null;
    }

    /// <summary>
    /// Returns the disk offset from which the tested space is BLANK/STALE, or null if there's no clear boundary.
    /// </summary>
    /// <param name="tested">The disk ranges that were verified; they don't overlap.</param>
    /// <param name="blankOrStale">The disk ranges that verified as BLANK or STALE; a subset of <paramref name="tested"/>.</param>
    /// <remarks>
    /// The boundary is the point that best splits the tested space into "works" below and "BLANK/STALE" above,
    /// counting every byte on the wrong side as an error. It counts as clear only if nearly all BLANK/STALE bytes
    /// lie above it and most of the space above it is BLANK/STALE. Scattered or mid-drive damage fails these checks.
    /// </remarks>
    public static Boundary? FindBoundary(IReadOnlyList<DiskRange> tested, IReadOnlyList<DiskRange> blankOrStale)
    {
        var t = new SortedRanges(tested);
        var b = new SortedRanges(blankOrStale);
        if (b.Total == 0)
            return null;

        long bestErrors = b.Total; // no boundary: every BLANK/STALE byte is an error
        long? best = null;
        foreach (var range in b.Ranges)
        {
            long blankBelow = b.Below(range.Offset);
            long workingAbove = (t.Total - t.Below(range.Offset)) - (b.Total - blankBelow);
            if (blankBelow + workingAbove < bestErrors)
            {
                bestErrors = blankBelow + workingAbove;
                best = range.Offset;
            }
        }
        if (best is not { } boundary)
            return null;

        long blankAbove = b.Total - b.Below(boundary);
        long testedAbove = t.Total - t.Below(boundary);
        if (blankAbove < BoundaryConcentration * b.Total || blankAbove < BoundaryTailBlank * testedAbove)
            return null;
        return new Boundary(boundary, t.Below(boundary));
    }

    /// <summary>Non-overlapping ranges sorted by offset, with running totals.</summary>
    sealed class SortedRanges
    {
        public readonly DiskRange[] Ranges;
        readonly long[] before; // bytes in Ranges[0 .. i)

        public SortedRanges(IEnumerable<DiskRange> ranges)
        {
            Ranges = [.. ranges.OrderBy(r => r.Offset)];
            before = new long[Ranges.Length + 1];
            for (int i = 0; i < Ranges.Length; i++)
                before[i + 1] = before[i] + Ranges[i].Length;
        }

        public long Total => before[^1];

        /// <summary>Bytes of the ranges that lie below a disk offset.</summary>
        public long Below(long offset)
        {
            int lo = 0, hi = Ranges.Length; // count the ranges that start below offset
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Ranges[mid].Offset < offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            if (lo == 0)
                return 0;
            var last = Ranges[lo - 1];
            return before[lo - 1] + Math.Min(last.Length, offset - last.Offset);
        }
    }
}
