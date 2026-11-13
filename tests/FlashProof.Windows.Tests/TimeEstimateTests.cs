using FlashProof.Tests.Emulator;

namespace FlashProof.Windows.Tests;

// Design §6.5: the time estimate, with a clock that moves only when told to.
public sealed class TimeEstimateTests
{
    const long MiB = 1 << 20, GiB = 1L << 30;
    readonly ManualClock clock = new();
    readonly TimeEstimate estimate;

    public TimeEstimateTests() => estimate = new TimeEstimate(clock);

    /// <summary>Progress every 4 MiB at this speed, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    void Run(WorkPhase phase, long from, long to, long total, double bytesPerSecond)
    {
        for (long done = from; done <= to; done += 4 * MiB)
        {
            estimate.Add(new ProgressMessage(phase, done, total, null));
            clock.Advance(TimeSpan.FromSeconds(4 * MiB / bytesPerSecond));
        }
    }

    static void AssertNear(double expected, double? actual) => Assert.InRange(actual ?? double.NaN, expected * 0.99, expected * 1.01);

    [Fact]
    public void There_is_no_estimate_until_10_s_of_writing()
    {
        Run(WorkPhase.Write, 0, 100 * MiB, 10 * GiB, 20e6); // about 5 s

        Assert.Null(estimate.WriteSpeed);
        Assert.Null(estimate.Left);
    }

    [Fact]
    public void While_writing_the_estimate_is_a_range_for_the_unknown_read_speed()
    {
        Run(WorkPhase.Write, 0, GiB, 10 * GiB, 20e6);

        var (fastest, slowest) = estimate.Left!.Value;
        double writing = 9 * GiB / 20e6;
        AssertNear(20e6, estimate.WriteSpeed);
        AssertNear(writing + 10 * GiB / TimeEstimate.FastestRead, fastest.TotalSeconds);
        AssertNear(writing + 10 * GiB / 20e6, slowest.TotalSeconds); // reads no slower than the writes
    }

    [Fact]
    public void The_window_forgets_a_fast_start()
    {
        Run(WorkPhase.Write, 0, 2 * GiB, 10 * GiB, 100e6); // the drive's cache
        Run(WorkPhase.Write, 2 * GiB, 6 * GiB, 10 * GiB, 10e6);

        AssertNear(10e6, estimate.WriteSpeed);
    }

    [Fact]
    public void Once_reading_back_starts_the_estimate_is_one_value()
    {
        Run(WorkPhase.Write, 0, 4 * GiB, 4 * GiB, 20e6);
        Run(WorkPhase.Verify, 0, GiB, 4 * GiB, 100e6);

        var (fastest, slowest) = estimate.Left!.Value;
        AssertNear(3 * GiB / 100e6, fastest.TotalSeconds);
        Assert.Equal(fastest, slowest);
    }

    [Fact]
    public void The_early_fake_check_counts_records_not_bytes_and_is_ignored()
    {
        Run(WorkPhase.Write, 0, GiB, 10 * GiB, 20e6);
        estimate.Add(new ProgressMessage(WorkPhase.EarlyCheck, 64, 64, null));

        AssertNear(20e6, estimate.WriteSpeed);
    }

    [Fact]
    public void Before_a_test_the_range_assumes_typical_speeds()
    {
        var (fastest, slowest) = TimeEstimate.Expected(60_000_000_000);

        AssertNear(60e9 / 40e6 + 60e9 / 150e6, fastest.TotalSeconds);
        AssertNear(60e9 / 10e6 + 60e9 / 30e6, slowest.TotalSeconds);
    }
}
