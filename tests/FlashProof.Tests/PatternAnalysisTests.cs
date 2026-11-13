using FlashProof.Core;

namespace FlashProof.Tests;

// Design §6.1 wrap-size analysis and the §3 "clear boundary" rule.
public class PatternAnalysisTests
{
    const long R = 20 * 1024 * 1024 + 1536; // a wrap size that isn't a power of two
    const long RRecords = R / TestRecord.Size;

    static Dictionary<long, long> Deltas(params (long Delta, long Count)[] items) => items.ToDictionary(i => i.Delta, i => i.Count);

    // ---- Wrap size

    [Fact]
    public void Agreeing_distances_give_the_wrap_size() =>
        Assert.Equal(R, PatternAnalysis.FindWrapSize(Deltas((R, 1000)), okRecords: RRecords));

    [Fact]
    public void Records_several_wraps_away_agree_with_the_wrap_size() =>
        Assert.Equal(R, PatternAnalysis.FindWrapSize(Deltas((R, 500), (2 * R, 300), (3 * R, 200)), okRecords: RRecords));

    [Fact]
    public void Exactly_90_percent_agreement_is_enough() =>
        Assert.Equal(R, PatternAnalysis.FindWrapSize(Deltas((R, 9), (777 * 512, 1)), okRecords: 0));

    [Fact]
    public void Less_than_90_percent_agreement_is_no_wrap() =>
        Assert.Null(PatternAnalysis.FindWrapSize(Deltas((R, 8), (777 * 512, 1), (1234 * 512, 1)), okRecords: 0));

    [Fact]
    public void Unknown_distances_count_against_agreement() =>
        Assert.Null(PatternAnalysis.FindWrapSize(Deltas((R, 8), (0, 2)), okRecords: 0));

    [Fact]
    public void A_wrap_size_too_small_to_hold_the_OK_records_is_ruled_out()
    {
        // A genuine but failing drive: one 4 MiB write landed 1 MiB off. Its 8192 records all agree, but a drive with
        // 1 MiB of real flash couldn't have returned 60 MiB correctly.
        Assert.Null(PatternAnalysis.FindWrapSize(Deltas((1024 * 1024, 8192)), okRecords: 60 * 2048));
    }

    [Fact]
    public void A_small_stray_distance_doesnt_hide_the_real_wrap_size() =>
        Assert.Equal(R, PatternAnalysis.FindWrapSize(Deltas((512, 5), (R, 995)), okRecords: RRecords));

    [Fact]
    public void No_misplaced_records_is_no_wrap() =>
        Assert.Null(PatternAnalysis.FindWrapSize(Deltas(), okRecords: 100));

    // ---- Boundary

    static DiskRange[] Ranges(params (long Offset, long Length)[] r) => [.. r.Select(x => new DiskRange(x.Offset, x.Length))];

    [Fact]
    public void Blank_from_a_point_to_the_end_is_a_boundary() =>
        Assert.Equal(new Boundary(60, 60), PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((60, 40))));

    [Fact]
    public void Blank_from_the_start_is_a_boundary_with_nothing_below() =>
        Assert.Equal(new Boundary(0, 0), PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((0, 100))));

    [Fact]
    public void A_correctly_read_cached_tail_still_leaves_a_boundary() =>
        Assert.Equal(new Boundary(60, 60), PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((60, 30))));

    [Fact]
    public void Tested_bytes_below_are_counted_across_gaps_left_by_existing_files() =>
        Assert.Equal(new Boundary(60, 40), PatternAnalysis.FindBoundary(Ranges((0, 30), (50, 50)), Ranges((60, 40))));

    [Fact]
    public void Scattered_blank_records_are_no_boundary() =>
        Assert.Null(PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((10, 1), (50, 1), (90, 1))));

    [Fact]
    public void A_dead_patch_in_the_middle_is_no_boundary() =>
        Assert.Null(PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((40, 20))));

    [Fact]
    public void A_tail_that_is_mostly_readable_is_no_boundary() =>
        Assert.Null(PatternAnalysis.FindBoundary(Ranges((0, 100)), Ranges((60, 10), (95, 1))));

    [Fact]
    public void No_blank_records_is_no_boundary() =>
        Assert.Null(PatternAnalysis.FindBoundary(Ranges((0, 100)), []));
}
