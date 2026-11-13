using System.Globalization;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof;

/// <summary>
/// Every piece of text the app shows or exports, in English (design §13: English only for v1, all text in one file so
/// translation can come later). Numbers use the invariant culture, so a report reads the same on every PC.
/// </summary>
public static class Text
{
    /// <summary>The app's name, decided on 14 Nov 2026 (design §13). Strings below spell it out too.</summary>
    public const string AppName = "FlashProof";

    static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

    // ---- Common ----
    public const string Back = "Back";
    public const string BackToDrives = "Back to drives";
    public const string Inspect = "Inspect";
    public const string FullTest = "Full test";
    public const string Stop = "Stop";
    public const string KeepWaiting = "Keep waiting";
    public const string Yes = "Yes";
    public const string No = "No";

    // ---- Home ----
    public const string HomeTitle = "Your drives and cards";
    public const string Refresh = "Refresh";
    public const string Scanning = "Looking for drives and cards…";
    public const string NoDrives = "No USB drives or memory cards found. Plug one in, then press Refresh.";
    public const string HomeHint = "Inspect only reads. The Full test writes to the free space, then checks every byte.";

    public const string StatusReady = "Ready to test";
    public const string StatusNoCard = "No card";
    public const string StatusNotWorking = "Not working";
    public const string StatusNotSupported = "Not supported";
    public const string StatusCantTest = "Can't test";
    public const string StatusWriteProtected = "Write protected";
    public const string StatusWritingBlocked = "Writing blocked by Windows";

    /// <summary>"61.5 GB · E: Ventoy".</summary>
    public static string CardSummary(long size, VolumeInfo? volume)
    {
        if (volume is null)
            return Bytes(size);
        string where = string.Join(" ", [.. volume.Letters, volume.Label]).Trim();
        return where.Length > 0 ? $"{Bytes(size)} · {where}" : Bytes(size);
    }

    public static string ProblemCode(int code) =>
        code == 0 ? "Windows found USB storage, but no drive appeared." : $"Windows reports problem code {code}.";

    // ---- Sizes, speeds and times ----
    public const long GB = 1_000_000_000;
    public const long GiB = 1L << 30;

    /// <summary>Decimal units, as drives are sold: "61.5 GB", "512 MB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1000 * GB => string.Format(Numbers, "{0:0.0} TB", bytes / (1000.0 * GB)),
        >= GB =>string.Format(Numbers, "{0:0.0} GB", bytes / (double)GB),
        >= 1_000_000 => string.Format(Numbers, "{0:0} MB", bytes / 1e6),
        >= 1000 => string.Format(Numbers, "{0:0} KB", bytes / 1e3),
        1 => "1 byte",
        _ => string.Format(Numbers, "{0} bytes", bytes),
    };

    /// <summary>"61.5 GB (57.3 GiB)".</summary>
    public static string BothUnits(long bytes) => string.Format(Numbers, "{0} ({1:0.0} GiB)", Bytes(bytes), bytes / (double)GiB);

    public const string UnitsExplained =
        "Drives are sold in GB (a billion bytes). Windows counts in GiB (1,073,741,824 bytes), so a 64 GB drive shows as about 59.6 GiB.";

    /// <summary>Bytes per second as "16.0 MB/s".</summary>
    public static string Speed(double bytesPerSecond) => string.Format(Numbers, "{0:0.0} MB/s", bytesPerSecond / 1e6);

    public static string Percent(double fraction) => string.Format(Numbers, "{0:0.0}%", fraction * 100);

    /// <summary>"1 h 14 min", "12 min", "under a minute".</summary>
    public static string Duration(TimeSpan t)
    {
        int minutes = (int)Math.Round(t.TotalMinutes);
        if (minutes < 1)
            return "under a minute";
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    public static string When(DateTimeOffset t) => t.ToLocalTime().ToString("d MMM yyyy HH:mm", Numbers);

    // ---- Device statuses (design §5.1) ----

    /// <summary>The short status, and what to do, for a scanned drive.</summary>
    public static (string Status, string Advice) DeviceStatus(VerdictResult? status) => status is null ? (StatusReady, "") : status.Verdict switch
    {
        Verdict.NoCardInReader => (StatusNoCard, Explanation(Verdict.NoCardInReader)),
        Verdict.DetectedButNotWorking => (StatusNotWorking, Explanation(Verdict.DetectedButNotWorking)),
        Verdict.WriteProtected => (StatusWriteProtected, Explanation(Verdict.WriteProtected)),
        Verdict.WritingBlockedByWindows => (StatusWritingBlocked, Explanation(Verdict.WritingBlockedByWindows)),
        Verdict.CantTest when status.CantTestReason is CantTestReason.ExternalSsdOrHardDrive or CantTestReason.EmbeddedStorage or CantTestReason.UnsupportedDevice
            => (StatusNotSupported, CantTestText(status.CantTestReason)),
        Verdict.CantTest => (StatusCantTest, CantTestText(status.CantTestReason)),
        _ => (Title(status.Verdict), Explanation(status.Verdict)),
    };

    public static string CantTestText(CantTestReason? reason) => reason switch
    {
        CantTestReason.NotEnoughFreeSpace => $"Less than {Bytes(Eligibility.MinimumFreeSpace)} is free. Copy some files off to make room, then test again.",
        CantTestReason.VolumeLargerThanDisk => "A partition on it is bigger than the drive itself: a sign of a fake drive or a damaged format. Copy off anything important.",
        CantTestReason.NoFileSystem => "It isn't formatted as FAT32, exFAT or NTFS, or Windows can't read its format. Formatting it in Windows fixes that, but erases it.",
        CantTestReason.BitLockerLocked => "It's encrypted with BitLocker and locked. Unlock it in File Explorer, then test again.",
        CantTestReason.ExternalSsdOrHardDrive => "External SSDs and hard drives aren't supported in this version.",
        CantTestReason.EmbeddedStorage => "Built-in storage (eMMC) isn't supported.",
        CantTestReason.UnsupportedDevice => "This kind of device isn't supported.",
        CantTestReason.HoldsSystemFiles => "It holds files that Windows or FlashProof needs while running, so FlashProof won't write to it.",
        CantTestReason.VolumeSpansDisks => "Its volume spans more than one disk, which FlashProof can't test.",
        _ => "It can't be tested.",
    };

    // ---- Verdicts (design §3) ----

    public static string Title(Verdict verdict) => verdict switch
    {
        Verdict.PassedCapacityConfirmed => "Passed: capacity confirmed",
        Verdict.PassedCapacityNotChecked => "Passed: capacity not checked",
        Verdict.FakeCapacity => "Fake capacity",
        Verdict.NotStoringData => "Not storing data",
        Verdict.DataErrors => "Data errors",
        Verdict.ConnectionProblemSuspected => "Connection problem suspected",
        Verdict.WriteProtected => "Write protected",
        Verdict.WritingBlockedByWindows => "Writing blocked by Windows",
        Verdict.NoCardInReader => "No card in the reader",
        Verdict.DetectedButNotWorking => "Detected but not working",
        Verdict.CantTest => "Can't test",
        _ => "Interrupted",
    };

    /// <summary>What the user is told. The two passes and a fake need the test's numbers: use <see cref="Explanation(VerdictResult, TestEvidence)"/>.</summary>
    public static string Explanation(Verdict verdict) => verdict switch
    {
        Verdict.NotStoringData => "This drive isn't saving what's written to it. Copy off anything important and stop using it.",
        Verdict.DataErrors => "This drive is losing or mixing up data. Copy off anything important and stop using it.",
        Verdict.ConnectionProblemSuspected => "Try another USB port or card reader, then test again.",
        Verdict.WriteProtected => "Check the lock switch on SD cards and adapters. If it's unlocked and still protected, the card has probably locked itself after failing. Copy off your files.",
        Verdict.WritingBlockedByWindows => "Windows on this PC is set to block writing to removable drives. This isn't a fault in the drive.",
        Verdict.NoCardInReader => "Insert or reseat the card.",
        Verdict.DetectedButNotWorking => "Windows sees something plugged in, but it isn't working. Try another port. If it still fails, it's probably dead. Unplug it: a drive in this state can stop some PCs from starting up.",
        Verdict.Interrupted => "No result. Don't rely on this run.",
        _ => "",
    };

    /// <param name="stayedPluggedIn">The test lost the drive, but it never left Windows' device list (see <see cref="ConnectionLost"/>).</param>
    public static string Explanation(VerdictResult v, TestEvidence e, bool stayedPluggedIn = false) => v.Verdict switch
    {
        Verdict.ConnectionProblemSuspected when ConnectionLost(e, stayedPluggedIn) is { } what => $"{what} {Explanation(v.Verdict)}",
        Verdict.DataErrors when e.LayoutChanged => $"{LayoutChanged} {Explanation(v.Verdict)}",
        Verdict.PassedCapacityConfirmed => "The drive worked correctly today and its full capacity is real. Keep backups.",
        Verdict.PassedCapacityNotChecked =>
            $"The {Bytes(e.VerifiedBytes)} tested worked correctly today. Only {Percent(v.Coverage)} of the drive was tested, so its real capacity wasn't checked. To check it, copy your files off, delete them, and test again.",
        Verdict.FakeCapacity =>
            $"Only about {Bytes(v.RealCapacityBytes ?? 0)} of the claimed {Bytes(e.DiskSize)} actually stores data. Return it if you can. Don't trust a smaller partition as a fix.",
        Verdict.CantTest => CantTestText(v.CantTestReason),
        Verdict.Interrupted => $"{Explanation(Verdict.Interrupted)} {InterruptText(v.InterruptReason)}",
        _ => Explanation(v.Verdict),
    };

    /// <summary>
    /// What happened, said before the advice of "Connection problem suspected" (design §3), so that it isn't read as
    /// "not plugged in". Null if the test didn't lose the drive.
    /// </summary>
    public static string? ConnectionLost(TestEvidence e, bool stayedPluggedIn)
    {
        if (e.Errors.FirstOrDefault(x => x.Kind == IoErrorKind.DeviceRemoved) is not { } error)
            return null;
        string when = error.Phase switch
        {
            TestPhase.Write => "while writing",
            TestPhase.EarlyCheck or TestPhase.Verify => "while reading back",
            _ => "during the test",
        };
        return stayedPluggedIn
            ? $"Windows lost contact with the drive {when}, though it stayed plugged in. This can be the USB port, a hub or cable, or the drive itself."
            : $"The drive disappeared from Windows {when}, though it wasn't unplugged. This can be the USB port, a hub or cable, or the drive itself.";
    }

    /// <summary>Said before the advice of Data errors when the drive overwrote its own start (<see cref="TestEvidence.LayoutChanged"/>).</summary>
    public const string LayoutChanged =
        "While writing, the drive damaged its own file system: its partition table or volume ID changed, though it stayed plugged in. Fake and failing drives do this.";

    public static string InterruptText(InterruptReason? reason) => reason switch
    {
        InterruptReason.Cancelled => "The test was stopped.",
        InterruptReason.DeviceChanged => "The drive or card changed during the test.",
        InterruptReason.UserUnplugged => "The drive was removed during the test.",
        InterruptReason.PcSlept => "The PC went to sleep during the test.",
        InterruptReason.AppClosed => "FlashProof was closed during the test.",
        _ => "The test stopped unexpectedly.",
    };

    public const string StorageLabel = "Storage";
    public const string CapacityLabel = "Capacity";

    /// <summary>The first of the two results: does the tested space work?</summary>
    public static string StorageResult(VerdictResult v, TestEvidence e) => v.Verdict switch
    {
        Verdict.PassedCapacityConfirmed or Verdict.PassedCapacityNotChecked => $"Passed. All {Bytes(e.VerifiedBytes)} tested worked correctly today.",
        Verdict.FakeCapacity or Verdict.NotStoringData or Verdict.DataErrors => $"Failed: {Title(v.Verdict).ToLowerInvariant()}.",
        _ => "No result.",
    };

    /// <summary>The second of the two results: is the size real?</summary>
    public static string CapacityResult(VerdictResult v, TestEvidence e) => v.Capacity switch
    {
        Core.CapacityResult.Confirmed => $"Confirmed ({Percent(v.Coverage)} of the drive tested).",
        Core.CapacityResult.Fake => $"Fake: only about {Bytes(v.RealCapacityBytes ?? 0)} of {Bytes(e.DiskSize)} is real.",
        _ when v.Verdict == Verdict.PassedCapacityNotChecked =>
            $"Not checked: {Percent(v.Coverage)} of the drive was tested, and the check needs about {Percent(VerdictEngine.CapacityCoverage)}.",
        _ => "Not checked.",
    };

    // ---- Did you unplug it? (design §3) ----
    public const string DidYouUnplugIt = "The drive disappeared during the test. Did you unplug it?";

    // ---- Inspect (design §6.3) ----
    public const string InspectTitle = "Inspect";
    public const string InspectOnlyReads = "Inspect only reads from the drive.";
    public const string NoProblemsFound = "No problems found in these checks.";
    public const string NotAHealthPass =
        "This isn't a health pass: a fake or failing drive can pass these checks. Only the Full test checks that data is stored correctly.";
    public const string ProblemsFound = "Inspect found problems:";
    public const string NoMedia = "There's no card in the reader, so nothing else could be checked.";
    public const string EventLogUnreadable = "Windows' disk error log couldn't be read, so recent errors weren't checked.";
    public const string VolumesLabel = "Partitions";
    public const string WriteProtectionLabel = "Write protection";
    public const string EventsLabel = "Disk errors Windows logged in the last 24 hours";
    public const string NoEvents = "None.";
    public const string NoLetter = "no letter";
    public const string ReadAllFiles = "Read every file";
    public const string ReadAllFilesHint = "Reads every file to check that it can be read. A file that reads fine may still hold wrong data.";
    public const string HoldsFilesAdvice =
        "This drive holds files. Copy them off before the Full test: writing to a fake drive can damage them, and only an empty drive gets its capacity checked.";

    public static string Problem(InspectProblem problem) => problem switch
    {
        InspectProblem.WriteProtected => "The drive refuses writes.",
        InspectProblem.VolumeLargerThanDisk => "A partition is bigger than the drive itself: a sign of a fake drive or a damaged format.",
        InspectProblem.RecentDiskErrors => "Windows logged disk errors for this drive in the last 24 hours.",
        _ => "Some files couldn't be read.",
    };

    /// <summary>The disk-errors problem when every counted error may have been another drive's (none was logged since this drive was plugged in).</summary>
    public const string RecentDiskErrorsUncertain = "Windows logged disk errors in the last 24 hours that may be from this drive.";

    public static string WriteProtection(DiskInfo disk, VerdictResult? status) => status?.Verdict switch
    {
        Verdict.WritingBlockedByWindows => "Windows on this PC blocks writing to removable drives.",
        Verdict.WriteProtected => "The drive refuses writes.",
        _ => disk.WriteProtected ? "The drive refuses writes." : "None: the drive accepts writes.",
    };

    /// <summary>"E: Ventoy · exFAT · 61.0 GB, 60.0 GB free".</summary>
    public static string Volume(VolumeInfo v)
    {
        string where = v.Letters.Count > 0 ? string.Join(" ", v.Letters) : $"({NoLetter})";
        string name = v.Label.Length > 0 ? $"{where} {v.Label}" : where;
        string fileSystem = v.FileSystem.Length > 0 ? v.FileSystem : "no file system";
        return $"{name} · {fileSystem} · {Bytes(v.Length)}, {Bytes(v.FreeBytes)} free";
    }

    public static string EventKind(DiskEventKind kind) => kind switch
    {
        DiskEventKind.BadBlock => "Bad block",
        DiskEventKind.ControllerError => "Controller error",
        DiskEventKind.RetriedIo => "A read or write had to be retried",
        DiskEventKind.SurpriseRemoval => "Unplugged without ejecting first (not an error)",
        _ => "The drive stopped responding and was reset",
    };

    /// <summary>The caveat that goes with an event (design §6.3); empty when it's certainly this drive.</summary>
    public static string EventCaveat(DiskEvent e) => e.DuringEjectedTest
        ? "Not counted: Windows logs these itself when a drive is ejected during a FlashProof test, while it checks whether the drive is still ready."
        : e.Match switch
        {
            DiskEventMatch.EarlierSameDiskNumber => "Logged before this drive was last plugged in, under the same disk number, so it may have been another drive.",
            DiskEventMatch.UasDriver => "The driver that logged this can't say which drive it was, so it may have been another one.",
            _ => "",
        };

    public static string Event(DiskEvent e) => $"{When(e.Time)}: {EventKind(e.Kind)}";

    /// <summary>"12 Nov 2026 05:21: Controller error (5 times)".</summary>
    public static string Repeated(string line, int count) => count == 1 ? line : string.Format(Numbers, "{0} ({1} times)", line, count);

    public static string ReadProgress(long done, long total) => $"Read {Bytes(done)} of {Bytes(total)}…";

    public static string FilesResult(FileReadResult r)
    {
        if (r.FilesRead + r.Skipped + r.Unreadable.Count == 0)
            return "There are no files on it to read.";
        string read = r.FilesRead == 1 ? $"Read 1 file ({Bytes(r.BytesRead)})." : string.Format(Numbers, "Read {0:N0} files ({1}).", r.FilesRead, Bytes(r.BytesRead));
        string unreadable = r.Unreadable.Count > 0 ? string.Format(Numbers, " {0:N0} couldn't be read:", r.Unreadable.Count)
            : r.FilesRead == 1 ? " It could be read."
            : r.FilesRead > 1 ? " All of them could be read." : "";
        string skipped = r.Skipped switch
        {
            0 => "",
            1 => " 1 was skipped because Windows wouldn't open it (in use, or access denied).",
            _ => string.Format(Numbers, " {0:N0} were skipped because Windows wouldn't open them (in use, or access denied).", r.Skipped),
        };
        return read + unreadable + skipped;
    }

    public static string Unreadable(UnreadableFile f) => $"{f.Path}{(f.IsFolder ? " (folder)" : "")}: Windows error {f.Error}";

    public const string ReadStopped = "The read was stopped.";

    // ---- The confirmation screen (design §5.3, §6.2 step 1) ----
    public const string ConfirmTitle = "Before the Full test";
    public const string ConfirmBackedUp = "My files on this drive are backed up";
    public const string StartTest = "Start the Full test";
    public const string FakeDamageWarning = "If this drive is fake, writing to it can damage the files already on it.";
    public const string StayPluggedIn = "Leave the drive plugged in until the test ends. FlashProof keeps the PC awake meanwhile.";
    public const string ConfirmReplug = "Unplug and replug before verifying (optional)";
    public const string ConfirmReplugHint =
        "Stronger proof: some fake drives keep the newest data in a small memory that empties when they're unplugged. When writing ends, FlashProof asks you to unplug the drive and plug it back in, so stay near the PC.";

    /// <summary>"SanDisk 3.2Gen1, 61.5 GB, E: Ventoy".</summary>
    public static string DeviceLine(string name, long size, VolumeInfo volume)
    {
        string where = volume.Letters.Count > 0 ? $", {string.Join(" ", volume.Letters)}" : "";
        string label = volume.Label.Length > 0 ? $" {volume.Label}" : "";
        return $"{name}, {Bytes(size)}{where}{label}";
    }

    public static string WhatIsWritten(VolumeInfo volume) =>
        $"FlashProof will fill the free space on {(volume.Letters.Count > 0 ? volume.Letters[0] : "the drive")} with test files in a folder of its own, read them all back, then delete them. Your existing files are kept.";

    /// <summary>The scope (design §6.2 step 1): "Will test 52.4 GB, which is 85% of the drive. …".</summary>
    public static string Scope(long plannedBytes, long diskSize)
    {
        double coverage = (double)plannedBytes / diskSize;
        string will = $"Will test {Bytes(plannedBytes)}, which is {Percent(coverage)} of the drive.";
        return coverage >= VerdictEngine.CapacityCoverage
            ? $"{will} That's enough to check its capacity too."
            : $"{will} The capacity check needs about {Percent(VerdictEngine.CapacityCoverage)}: copy files off to include it.";
    }

    public static string TimeRange(TimeSpan fastest, TimeSpan slowest) =>
        $"Expected time: about {Duration(fastest)} to {Duration(slowest)}. A slow card can take longer; the estimate gets better once the test is running.";

    // ---- The test (design §6.5, §5.4) ----
    public static string Testing(string name) => $"Testing {name}";

    /// <param name="replug">The test has the "Unplug and replug" step between writing and reading back.</param>
    public static string Phase(WorkPhase phase, bool replug = false) => (phase, replug) switch
    {
        (WorkPhase.Write, false) => "Step 1 of 2: writing test data",
        (WorkPhase.EarlyCheck, false) => "Step 1 of 2: checking early for a fake",
        (_, false) => "Step 2 of 2: reading everything back and checking it",
        (WorkPhase.Write, true) => "Step 1 of 3: writing test data",
        (WorkPhase.EarlyCheck, true) => "Step 1 of 3: checking early for a fake",
        _ => "Step 3 of 3: reading everything back and checking it",
    };

    // ---- Unplug and replug before verifying (design §6.2 step 7) ----
    public const string ReplugPhase = "Step 2 of 3: unplug and replug the drive";
    public const string EjectNow = "Eject now";

    public static string Replug(ReplugMessage m, string name) => m.Step switch
    {
        ReplugStep.ReadyToEject => "Writing is done. Click Eject now, then unplug the drive.",
        ReplugStep.Ejected => "Unplug the drive now, wait 5 seconds, then plug it back in. FlashProof carries on by itself when it's back.",
        ReplugStep.EjectFailed when m.Blocker is { } blocker => $"Windows couldn't eject the drive because {blocker} is using it. Close it, then click Eject now again.",
        ReplugStep.EjectFailed => "Windows couldn't eject the drive: a program may be using it. Close any windows or programs using the drive, then click Eject now again.",
        ReplugStep.EjectNotAllowed => "Windows didn't let FlashProof eject the drive. Use Safely Remove Hardware in the taskbar, or just unplug it. Then wait 5 seconds and plug it back in.",
        ReplugStep.Checking => "Found it. Checking that it's the same drive…",
        ReplugStep.WrongDrive => $"That isn't the drive being tested, so FlashProof won't touch it. Plug {name} back in.",
        _ => $"The drive that was plugged in doesn't have this test's files, or they can't be read. Plug in {name}, or press Stop. " +
             "If this is the drive you tested, it may not have kept the data: press Stop, then run the Full test again without unplugging.",
    };

    public const string Replugged = "The drive was unplugged and plugged back in before reading back.";

    public const string Starting = "Starting…";
    public const string Estimating = "Estimating the time left…";
    public const string Stalled = "Stalled: no progress right now.";
    public const string Slow = "The drive is responding slowly. FlashProof is still waiting for it.";
    public const string NotResponding = "The drive isn't responding.";
    public const string Stopping = "Stopping the test…";
    public const string StopConfirm = "Stop the test? You'll get no result from this run.";
    public const string StopYes = "Yes, stop it";
    public const string StopNo = "No, keep testing";
    public const string CloseConfirm = "Close FlashProof? This stops the test, and you'll get no result from this run.";
    public const string CloseYes = "Yes, stop and close";

    public static string Stuck(string name) => $"The drive {name} is stuck. Unplug it; pending writes may be lost.";

    public static string Done(long done, long total) => $"{Bytes(done)} of {Bytes(total)}";

    public static string TimeLeft(TimeSpan time) => $"About {Duration(time)} left";

    public static string TimeLeftRange(TimeSpan fastest, TimeSpan slowest) =>
        $"About {Duration(fastest)} to {Duration(slowest)} left. This narrows once reading back starts.";

    public static string CurrentSpeed(double bytesPerSecond) => $"Speed: {Speed(bytesPerSecond)}";

    public static string TestRefused(string reason) => $"The test couldn't start. {reason}";

    public const string WorkerLost = "FlashProof's background process stopped unexpectedly.";

    // ---- The result (design §3, §6.4) ----
    public const string ResultTitle = "Result";
    public const string SpeedLabel = "Speed";
    public const string TestedLabel = "Tested";
    public const string ChartWrite = "Writing";
    public const string ChartRead = "Reading back";

    public static string WriteSpeeds(SpeedSummary s) => s.AverageWrite is { } avg
        ? $"Write: {Speed(avg)} average; worst sustained write on this PC: {Speed(s.WorstWrite ?? avg)}"
        : "Write: not measured";

    public static string ReadSpeeds(SpeedSummary s) => s.AverageRead is { } avg
        ? $"Read: {Speed(avg)} average; worst sustained read: {Speed(s.WorstRead ?? avg)}"
        : "Read: not measured";

    /// <summary>Design §6.4: reads at or below about 40 MB/s may be capped by a USB 2.0 port or reader.</summary>
    public static string? Usb2Note(SpeedSummary s) => s.AverageRead is { } avg && avg <= 40e6
        ? "Reads were 40 MB/s or slower: they may be limited by a USB 2.0 port or card reader."
        : null;

    /// <summary>The tested share and the untested margin; the margin is explained only when the test filled all the free space.</summary>
    public static string Tested(TestEvidence e, double coverage)
    {
        string why = e.FillEnd is FillEnd.PlanReached or FillEnd.DiskFull ? ", used by the file system and existing files" : "";
        return $"{Bytes(e.VerifiedBytes)} of the drive's {Bytes(e.DiskSize)} ({Percent(coverage)}). Not tested: {Bytes(Math.Max(0, e.DiskSize - e.VerifiedBytes))}{why}.";
    }

    public static string TookTime(TestResult r) => $"Took {Duration(r.FinishedAt - r.StartedAt)}, finished {When(r.FinishedAt)}.";

    /// <param name="layoutChanged">The drive damaged its own file system (<see cref="TestEvidence.LayoutChanged"/>), so the folder may be out of reach.</param>
    public static string FilesKept(string sessionId, bool layoutChanged = false) => layoutChanged
        ? $"The test files are still on the drive, in the folder FlashProof-{sessionId}, but the drive's damaged file system may not let you reach them. Formatting the drive removes them."
        : $"The test files are still on the drive, in the folder FlashProof-{sessionId}. Delete that folder when you no longer need it.";

    public static string? FilesKeptOrNull(TestResult r) => r.TestFilesKept ? FilesKept(r.SessionId, r.Evidence.LayoutChanged) : null;

    // ---- Export (design §9) ----
    public const string CopyReport = "Copy report";
    public const string SaveReport = "Save report…";
    public const string IncludeSerials = "Include serial numbers";
    public const string Copied = "Copied.";
    public const string SerialHidden = "serial hidden";
    public const string ReportFileName = "FlashProof report";

    public static string Saved(string name) => $"Saved {name}.";

    public static string SaveFailed(string why) => $"The report couldn't be saved: {why}";

    // ---- The exported report (design §9) ----
    public const string ReportTitle = "FlashProof report";
    public const string ReportDrive = "Drive";
    public const string ReportSize = "Size";
    public const string ReportTestedOn = "Tested on";
    public const string ReportResult = "Result";
    public const string ReportTested = "Tested";
    public const string ReportTime = "Time";
    public const string ReportRecords = "Records";
    public const string ReportErrors = "Errors";
    public const string ReportFiles = "Files";
    public const string InspectReportTitle = "FlashProof Inspect report";
    public const string InspectReportFileName = "FlashProof Inspect report";
    public const string ReportStatus = "Status";
    public const string ReportFound = "Found";
    public const string ReportPartition = "Partition";
    public const string ReportWrites = "Writes";
    public const string ReportReadBack = "Read-back";
    public const string ReportNoEvents = "None logged in the last 24 hours.";
    public const string ReportNotRun = "Not run.";
    public const string ReportSession = "Session";
    public const string ReportSerial = "Serial";
    public const string ReportDeviceId = "Device ID";
    public const string ReportApp = "App";
    public const string ReportNone = "none";
    public const string ReportUserUnplugged = "The user said they unplugged the drive during the test.";

    public static string Records(RecordCounts c) =>
        $"{c.Ok:N0} OK, {c.NotOk:N0} not OK (bit errors {c.BitErrors:N0}, misplaced {c.Misplaced:N0}, stale {c.Stale:N0}, blank {c.Blank:N0}, garbage {c.Garbage:N0})";

    public static string IoErrorLine(IoError e) =>
        $"{e.Phase} at {Bytes(e.TestOffset)} into the test: {e.Kind}, Windows error {e.NativeCode} ({e.Message})";
}
