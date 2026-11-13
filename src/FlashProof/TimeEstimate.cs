using FlashProof.Windows;

namespace FlashProof;

/// <summary>
/// The time left in a full test (design §6.5): remaining bytes ÷ write speed + bytes to verify ÷ read speed. Speeds come
/// from the progress messages and this PC's clock (wall time, so pauses for the early fake check and flushing count),
/// over a rolling window of the last 2 GiB, because a drive's fast cache makes the start look too fast.
/// </summary>
public sealed class TimeEstimate(TimeProvider clock)
{
    public const long Window = 2L << 30;

    /// <summary>No speed is given from less than this much time.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(10);

    /// <summary>Until reading back starts, reads are assumed to be at most this fast, and no slower than the writes.</summary>
    public const double FastestRead = 200e6;

    /// <summary>Before a test, speeds are assumed to be in these ranges (bytes per second): typical USB flash drives and cards.</summary>
    public const double SlowWrite = 10e6, FastWrite = 40e6, SlowRead = 30e6, FastRead = 150e6;

    readonly Queue<(long Timestamp, long Done)> writes = new(), reads = new();
    long writeDone, writeTotal, readDone, readTotal;

    /// <summary>The expected time of a test of this size, before it starts (design §6.2 step 1).</summary>
    public static (TimeSpan Fastest, TimeSpan Slowest) Expected(long bytes) =>
        (TimeSpan.FromSeconds(bytes / FastWrite + bytes / FastRead), TimeSpan.FromSeconds(bytes / SlowWrite + bytes / SlowRead));

    public void Add(ProgressMessage progress)
    {
        long now = clock.GetTimestamp();
        switch (progress.Phase)
        {
            case WorkPhase.Write:
                (writeDone, writeTotal) = (progress.Done, progress.Total);
                Push(writes, now, progress.Done);
                break;
            case WorkPhase.Verify:
                (readDone, readTotal) = (progress.Done, progress.Total);
                Push(reads, now, progress.Done);
                break;
        }
    }

    /// <summary>Bytes per second over the window; null until there's enough to tell.</summary>
    public double? WriteSpeed => SpeedOf(writes);

    public double? ReadSpeed => SpeedOf(reads);

    /// <summary>The time left: a range while writing, one value once reading back is under way; null until there's enough to tell.</summary>
    public (TimeSpan Fastest, TimeSpan Slowest)? Left
    {
        get
        {
            if (reads.Count > 0)
            {
                if (ReadSpeed is not { } read)
                    return null;
                var left = TimeSpan.FromSeconds((readTotal - readDone) / read);
                return (left, left);
            }
            if (WriteSpeed is not { } write)
                return null;
            double writing = (writeTotal - writeDone) / write;
            return (TimeSpan.FromSeconds(writing + writeTotal / Math.Max(write, FastestRead)), TimeSpan.FromSeconds(writing + writeTotal / write));
        }
    }

    /// <summary>Keeps the newest point and the oldest one still needed to span the window.</summary>
    static void Push(Queue<(long Timestamp, long Done)> points, long now, long done)
    {
        points.Enqueue((now, done));
        while (points.Count > 2 && done - points.ElementAt(1).Done >= Window)
            points.Dequeue();
    }

    double? SpeedOf(Queue<(long Timestamp, long Done)> points)
    {
        if (points.Count < 2)
            return null;
        var (first, last) = (points.Peek(), points.Last());
        var span = clock.GetElapsedTime(first.Timestamp, last.Timestamp);
        return span < MinimumSpan ? null : (last.Done - first.Done) / span.TotalSeconds;
    }
}
