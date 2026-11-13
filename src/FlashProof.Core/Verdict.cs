namespace FlashProof.Core;

/// <summary>
/// The verdicts of design §3. The last three come from the device checks before any test, not from a test run.
/// New members go at the end, so values saved in journals keep their meaning.
/// </summary>
public enum Verdict
{
    PassedCapacityConfirmed,
    PassedCapacityNotChecked,
    FakeCapacity,
    NotStoringData,
    DataErrors,
    ConnectionProblemSuspected,
    WriteProtected,
    CantTest,
    Interrupted,

    /// <summary>A Windows policy blocks writing to removable drives; not a fault in the drive.</summary>
    WritingBlockedByWindows,

    /// <summary>A card reader with no card in it.</summary>
    NoCardInReader,

    /// <summary>A USB device with a problem code, or USB storage that never produced a disk.</summary>
    DetectedButNotWorking,
}

/// <summary>The second result of a full test: is the size real?</summary>
public enum CapacityResult
{
    Confirmed,
    NotChecked,
    Fake,
}

public enum CantTestReason
{
    NotEnoughFreeSpace,

    /// <summary>The volume runs past the end of the disk: a sign of a fake or a bad format (design §5.1).</summary>
    VolumeLargerThanDisk,

    /// <summary>No supported file system (FAT32, exFAT or NTFS): unformatted, RAW, or another file system.</summary>
    NoFileSystem,

    BitLockerLocked,

    /// <summary>Not supported in v1: an external SSD or hard drive, which reports fixed media (design §5.1).</summary>
    ExternalSsdOrHardDrive,

    /// <summary>Not supported in v1: SD or MMC storage that isn't removable, treated as built-in eMMC (design §5.1).</summary>
    EmbeddedStorage,

    /// <summary>Not a USB, SD or MMC disk.</summary>
    UnsupportedDevice,

    /// <summary>The disk holds Windows, the system partition, a page file, or the app itself (design §5.1).</summary>
    HoldsSystemFiles,

    /// <summary>The volume spans more than one disk.</summary>
    VolumeSpansDisks,
}

public enum InterruptReason
{
    Cancelled,
    DeviceChanged,
    UserUnplugged,

    /// <summary>The journal shows a session that never finished: the app or worker crashed or was killed.</summary>
    Unfinished,

    /// <summary>The PC went to sleep during the test; errors around the resume aren't counted against the drive (design §5.4).</summary>
    PcSlept,

    /// <summary>The app closed or crashed, so the worker stopped the test (design §5.4: worker self-exit).</summary>
    AppClosed,
}

/// <summary>Both results of a test, plus what they rest on.</summary>
/// <param name="Coverage">Verified bytes ÷ disk size.</param>
/// <param name="RealCapacityBytes">For a fake: about how much of the disk really stores data.</param>
public sealed record VerdictResult(
    Verdict Verdict,
    CapacityResult Capacity,
    double Coverage,
    long? RealCapacityBytes = null,
    CantTestReason? CantTestReason = null,
    InterruptReason? InterruptReason = null);

public enum TestPhase
{
    Preflight,
    Write,
    EarlyCheck,
    Verify,

    /// <summary>Waiting for the drive to be unplugged and plugged back in before verifying (design §6.2 step 7).</summary>
    Replug,
}

/// <summary>How the fill ended.</summary>
public enum FillEnd
{
    NotStarted,
    PlanReached,

    /// <summary>The disk filled up before the plan was reached; a normal end (design §6.2).</summary>
    DiskFull,

    /// <summary>The early fake check found MISPLACED, BLANK or STALE records.</summary>
    EarlyFakeCheck,

    /// <summary>An error, a cancellation or a changed device stopped the test.</summary>
    Stopped,
}

/// <param name="TestOffset">Position in the test, in bytes (record index × 512).</param>
public sealed record IoError(TestPhase Phase, long TestOffset, IoErrorKind Kind, int NativeCode, string Message);

/// <summary>A run of consecutive records with the same class.</summary>
public readonly record struct RecordRun(long StartIndex, long Count, RecordClass Class);

public sealed class RecordCounts
{
    public long Ok { get; set; }
    public long BitErrors { get; set; }
    public long Misplaced { get; set; }
    public long Stale { get; set; }
    public long Blank { get; set; }
    public long Garbage { get; set; }

    public long NotOk => BitErrors + Misplaced + Stale + Blank + Garbage;

    public void Add(RecordClass c, long n)
    {
        switch (c)
        {
            case RecordClass.Ok: Ok += n; break;
            case RecordClass.BitErrors: BitErrors += n; break;
            case RecordClass.Misplaced: Misplaced += n; break;
            case RecordClass.Stale: Stale += n; break;
            case RecordClass.Blank: Blank += n; break;
            default: Garbage += n; break;
        }
    }
}

/// <summary>What a full test found. The verdict engine decides from this alone.</summary>
public sealed class TestEvidence
{
    public long DiskSize { get; set; }
    public long PlannedBytes { get; set; }
    public long WrittenBytes { get; set; }
    public long VerifiedBytes { get; set; }
    public FillEnd FillEnd { get; set; }
    public RecordCounts Counts { get; set; } = new();

    /// <summary>Records sampled by the early fake check that weren't OK.</summary>
    public long EarlyCheckBadSamples { get; set; }

    /// <summary>The I/O error that stopped the test, if any. Disk full isn't an error.</summary>
    public List<IoError> Errors { get; set; } = [];

    /// <summary>Disk distance between where each MISPLACED record was found and where it was written, counted by distance.</summary>
    public Dictionary<long, long> MisplacedDeltas { get; set; } = new();

    /// <summary>Runs of records that weren't OK, in test order.</summary>
    public List<RecordRun> BadRuns { get; set; } = [];

    /// <summary>True if there were too many runs to keep; the boundary analysis is then skipped.</summary>
    public bool BadRunsTruncated { get; set; }

    public long? WrapSize { get; set; }
    public Boundary? Boundary { get; set; }
    public CantTestReason? CantTest { get; set; }
    public InterruptReason? Interrupted { get; set; }

    /// <summary>The drive was unplugged and plugged back in between writing and verifying (design §6.2 step 7).</summary>
    public bool Replugged { get; set; }

    /// <summary>
    /// The test lost the drive's volume while the drive stayed plugged in, and afterwards its partition table or its
    /// volume serial had changed: the drive overwrote its own start, as a fake or failing drive does. Not a connection problem.
    /// </summary>
    public bool LayoutChanged { get; set; }

    public bool DeviceRemoved => Errors.Any(e => e.Kind == IoErrorKind.DeviceRemoved);
}

/// <summary>The fixed verdict rules of design §3. No heuristics beyond the documented thresholds.</summary>
public static class VerdictEngine
{
    /// <summary>"Capacity confirmed" needs at least this share of the disk tested.</summary>
    public const double CapacityCoverage = 0.95;

    /// <summary>
    /// BLANK/STALE from a boundary with at most this share of the tested bytes below it counts as "from (near) the
    /// start of the test": Not storing data rather than Fake capacity.
    /// </summary>
    public const double NearStart = 0.05;

    /// <summary>
    /// Decides both results. If the device disappeared, the app asks "Did you unplug it?" (design §3) and passes the
    /// answer; until then the result is Connection problem suspected.
    /// </summary>
    public static VerdictResult Decide(TestEvidence e, bool userSaysUnplugged = false)
    {
        double coverage = e.DiskSize > 0 ? (double)e.VerifiedBytes / e.DiskSize : 0;
        VerdictResult Result(Verdict v, CapacityResult c = CapacityResult.NotChecked, long? real = null, InterruptReason? why = null) =>
            new(v, c, coverage, real, e.CantTest, why);

        if (e.CantTest is not null)
            return Result(Verdict.CantTest);
        if (e.Interrupted is { } reason)
            return Result(Verdict.Interrupted, why: reason);
        if (e.DeviceRemoved && !e.LayoutChanged) // a drive that changed its own layout falls through to Data errors
            return userSaysUnplugged ? Result(Verdict.Interrupted, why: InterruptReason.UserUnplugged) : Result(Verdict.ConnectionProblemSuspected);
        if (e.Errors.Any(x => x.Kind == IoErrorKind.WriteProtected))
            return Result(Verdict.WriteProtected);
        if (e.WrapSize is { } wrap)
            return Result(Verdict.FakeCapacity, CapacityResult.Fake, wrap);
        if (e.Boundary is { } b)
        {
            return b.TestedBytesBelow <= NearStart * e.VerifiedBytes
                ? Result(Verdict.NotStoringData)
                : Result(Verdict.FakeCapacity, CapacityResult.Fake, b.DiskOffset);
        }
        if (e.Counts.NotOk > 0 || e.Errors.Count > 0 || e.EarlyCheckBadSamples > 0 || e.WrittenBytes == 0 || e.VerifiedBytes < e.WrittenBytes)
            return Result(Verdict.DataErrors);
        return coverage >= CapacityCoverage
            ? Result(Verdict.PassedCapacityConfirmed, CapacityResult.Confirmed)
            : Result(Verdict.PassedCapacityNotChecked);
    }
}
