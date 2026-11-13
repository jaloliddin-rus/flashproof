using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.ViewModels;

namespace FlashProof.Windows.Tests;

// Design §3 and §9: what the app says, and the exported report, with serials hidden unless asked for.
public sealed class ReportTests
{
    const string Serial = "4C530001120312345678";
    static readonly DateTimeOffset Now = new(2026, 11, 12, 14, 5, 0, TimeSpan.Zero);

    static readonly VolumeIdentity Identity = new($@"USBSTOR\DISK&VEN_USB&PROD_SANDISK_3.2GEN1\{Serial}&0", "USB SanDisk 3.2Gen1", Serial,
        61_524_148_224, 512, 512, Now.AddHours(-2), 1, "MBR:1A2B3C4D", @"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\", 0x4E210000);

    static ScannedDevice SanDisk(DiskInfo? disk = null)
    {
        var report = Inspection.Report(disk ?? EligibilityTests.SanDisk(), EligibilityTests.Pc, []);
        return new ScannedDevice(report.Disk.Device, report.Status, report, Identity, null);
    }

    static TestOutcome Passed()
    {
        var evidence = new TestEvidence { DiskSize = Identity.DiskSize, PlannedBytes = 61_400_000_000, WrittenBytes = 61_400_000_000, VerifiedBytes = 61_400_000_000 };
        evidence.Counts.Ok = 119_921_875;
        var result = new TestResult
        {
            SessionId = "20261112-130500-9f3a",
            Verdict = VerdictEngine.Decide(evidence),
            Evidence = evidence,
            Speed = new SpeedSummary { AverageWrite = 16.0e6, WorstWrite = 6.8e6, AverageRead = 165.0e6, WorstRead = 164.4e6 },
            StartedAt = Now.AddMinutes(-74),
            FinishedAt = Now,
        };
        return TestOutcome.From(new ResultMessage(result, "journal.json"));
    }

    [Fact]
    public void Serials_are_hidden_by_default()
    {
        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), Passed(), includeSerials: false, Now);

        Assert.DoesNotContain(Serial, report);
        Assert.DoesNotContain("USBSTOR", report); // the device ID contains the serial
        Assert.Contains($"SanDisk 3.2Gen1 ({Text.SerialHidden})", report);
    }

    [Fact]
    public void A_replug_is_reported_only_when_it_happened()
    {
        var replugged = Passed();
        replugged.Result!.Evidence.Replugged = true;

        Assert.Contains($"           {Text.Replugged}{Environment.NewLine}", Report.Build("SanDisk 3.2Gen1", SanDisk(), replugged, false, Now));
        Assert.DoesNotContain(Text.Replugged, Report.Build("SanDisk 3.2Gen1", SanDisk(), Passed(), false, Now));
    }

    [Fact]
    public void Serials_are_included_when_asked_for()
    {
        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), Passed(), includeSerials: true, Now);

        Assert.Contains($"Serial:    {Serial}", report);
        Assert.Contains(Identity.DeviceInstanceId, report);
    }

    [Fact]
    public void A_pass_reports_both_results_and_what_they_rest_on()
    {
        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), Passed(), includeSerials: false, Now);

        Assert.Contains("Result:    Passed: capacity confirmed", report);
        Assert.Contains("The drive worked correctly today and its full capacity is real. Keep backups.", report);
        Assert.Contains("Storage:   Passed. All 61.4 GB tested worked correctly today.", report);
        Assert.Contains("Capacity:  Confirmed (99.8% of the drive tested).", report);
        Assert.Contains("Size:      61.5 GB (57.3 GiB)", report);
        Assert.Contains("Write: 16.0 MB/s average; worst sustained write on this PC: 6.8 MB/s", report);
        Assert.Contains("Took 1 h 14 min", report);
        Assert.Contains("Errors:    none", report);
    }

    [Fact]
    public void Test_files_left_on_the_drive_get_their_own_line_not_the_errors_line()
    {
        var outcome = Passed();
        outcome.Result!.TestFilesKept = true;

        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), outcome, includeSerials: false, Now);

        Assert.Contains("Errors:    none" + Environment.NewLine + "Files:     The test files are still on the drive", report);
    }

    [Fact]
    public void An_inspect_report_says_what_Inspect_showed_with_serials_hidden()
    {
        var at = new DateTimeOffset(2026, 11, 12, 1, 21, 30, TimeSpan.Zero);
        DiskEvent[] events = [new(DiskEventKind.ControllerError, at.AddSeconds(1), 2, DiskEventMatch.EarlierSameDiskNumber), new(DiskEventKind.ControllerError, at, 2, DiskEventMatch.EarlierSameDiskNumber)];
        var report = Inspection.Report(EligibilityTests.SanDisk(), EligibilityTests.Pc, events);
        var device = new ScannedDevice(report.Disk.Device, report.Status, report, Identity, null);

        string text = Report.BuildInspect("SanDisk 3.2Gen1", device, [(EligibilityTests.Efi, new FileReadResult(992, 28_000_000, 0, []))], includeSerials: false, Now);

        Assert.StartsWith("FlashProof Inspect report, ", text);
        Assert.DoesNotContain(Serial, text);
        Assert.DoesNotContain("USBSTOR", text); // the device ID contains the serial
        Assert.Contains("Status:    Ready to test", text);
        Assert.Contains("Found:     Windows logged disk errors in the last 24 hours that may be from this drive.", text);
        Assert.Contains(": Controller error (2 times)", text);
        Assert.Contains("so it may have been another drive", text);
        Assert.Contains("Read-back: (no letter) VTOYEFI · FAT · 34 MB, 1 MB free: Read 992 files (28 MB). All of them could be read.", text);
    }

    [Fact]
    public void Disk_errors_are_called_this_drives_only_if_one_was_logged_since_it_was_plugged_in()
    {
        var at = new DateTimeOffset(2026, 11, 13, 14, 27, 32, TimeSpan.Zero);
        IReadOnlyList<string> Found(params DiskEvent[] events) =>
            InspectViewModel.ProblemLines(Inspection.Report(EligibilityTests.SanDisk(), EligibilityTests.Pc, events), []);
        DiskEvent Event(DiskEventKind kind, DiskEventMatch match, int minutes = 0) =>
            new(kind, at.AddMinutes(minutes), kind == DiskEventKind.UasReset ? null : 2, match);
        const string Certain = "Windows logged disk errors for this drive in the last 24 hours.";

        Assert.Equal([Text.RecentDiskErrorsUncertain], Found(Event(DiskEventKind.RetriedIo, DiskEventMatch.EarlierSameDiskNumber)));
        Assert.Equal([Text.RecentDiskErrorsUncertain], Found(Event(DiskEventKind.UasReset, DiskEventMatch.UasDriver)));
        // A surprise removal since the plug-in isn't counted, so it doesn't make the earlier error this drive's.
        Assert.Equal([Text.RecentDiskErrorsUncertain], Found(
            Event(DiskEventKind.SurpriseRemoval, DiskEventMatch.ThisPlugIn, 5), Event(DiskEventKind.RetriedIo, DiskEventMatch.EarlierSameDiskNumber)));
        Assert.Equal([Certain], Found(Event(DiskEventKind.RetriedIo, DiskEventMatch.ThisPlugIn)));
        Assert.Equal([Certain], Found(
            Event(DiskEventKind.RetriedIo, DiskEventMatch.ThisPlugIn, 5), Event(DiskEventKind.RetriedIo, DiskEventMatch.EarlierSameDiskNumber)));
    }

    [Fact]
    public void An_inspect_report_with_no_problems_never_reads_as_a_health_pass()
    {
        string text = Report.BuildInspect("SanDisk 3.2Gen1", SanDisk(), readBacks: [], includeSerials: true, Now);

        Assert.Contains("Found:     No problems found in these checks.", text);
        Assert.Contains(Text.NotAHealthPass, text);
        Assert.Contains("Errors:    None logged in the last 24 hours.", text);
        Assert.Contains("Read-back: Not run.", text);
        Assert.Contains($"Serial:    {Serial}", text);
    }

    [Fact]
    public void A_test_without_a_result_says_only_that()
    {
        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), TestOutcome.WithoutResult(InterruptReason.Unfinished), includeSerials: false, Now);

        Assert.Contains("Result:    Interrupted", report);
        Assert.Contains("No result. Don't rely on this run. The test stopped unexpectedly.", report);
        Assert.DoesNotContain("Storage:", report);
    }

    [Fact]
    public void The_answer_to_did_you_unplug_it_is_in_the_report()
    {
        var evidence = new TestEvidence { DiskSize = Identity.DiskSize, WrittenBytes = 8 << 20 };
        evidence.Errors.Add(new IoError(TestPhase.Write, 8 << 20, IoErrorKind.DeviceRemoved, 1167, "The device is not connected."));
        var result = new TestResult { SessionId = "s", Verdict = VerdictEngine.Decide(evidence), Evidence = evidence };
        var outcome = TestOutcome.From(new ResultMessage(result, "journal.json")).Answer(unplugged: true);

        string report = Report.Build("SanDisk 3.2Gen1", SanDisk(), outcome, includeSerials: false, Now);

        Assert.Contains(Text.ReportUserUnplugged, report);
        Assert.Contains("Windows error 1167", report);
    }

    [Fact]
    public void A_connection_problem_says_what_happened_before_the_advice()
    {
        var evidence = new TestEvidence { DiskSize = Identity.DiskSize, WrittenBytes = 52 << 20 };
        evidence.Errors.Add(new IoError(TestPhase.Write, 52 << 20, IoErrorKind.DeviceRemoved, 1006, "The volume for a file has been externally altered."));
        var result = new TestResult { SessionId = "s", Verdict = VerdictEngine.Decide(evidence), Evidence = evidence };

        var stayed = TestOutcome.From(new ResultMessage(result, "journal.json", StayedPluggedIn: true));
        string report = Report.Build("ADATA USB Flash Drive", SanDisk(), stayed, includeSerials: false, Now);
        Assert.Contains("Windows lost contact with the drive while writing, though it stayed plugged in. This can be the USB port, a hub or cable, " +
            "or the drive itself. Try another USB port or card reader, then test again.", report);

        evidence.LayoutChanged = true;
        var damaged = new TestResult { SessionId = "s", Verdict = VerdictEngine.Decide(evidence), Evidence = evidence, TestFilesKept = true };
        string damagedReport = Report.Build("ADATA USB Flash Drive", SanDisk(), TestOutcome.From(new ResultMessage(damaged, "journal.json", StayedPluggedIn: true)),
            includeSerials: false, Now);
        Assert.Contains("Result:    Data errors", damagedReport);
        Assert.Contains("While writing, the drive damaged its own file system: its partition table or volume ID changed, though it stayed plugged in. " +
            "Fake and failing drives do this. This drive is losing or mixing up data.", damagedReport);
        // The folder may be out of reach now, so the report doesn't tell the user to delete it.
        Assert.Contains("Files:     The test files are still on the drive, in the folder FlashProof-s, but the drive's damaged file system " +
            "may not let you reach them. Formatting the drive removes them.", damagedReport);
        Assert.DoesNotContain("Delete that folder", damagedReport);
        evidence.LayoutChanged = false;

        var vanished = TestOutcome.From(new ResultMessage(result, "journal.json")).Answer(unplugged: false);
        Assert.Equal("The drive disappeared from Windows while writing, though it wasn't unplugged. This can be the USB port, a hub or cable, " +
            "or the drive itself. Try another USB port or card reader, then test again.", Text.Explanation(vanished.Verdict, evidence, vanished.StayedPluggedIn));
    }

    [Fact]
    public void A_capacity_not_checked_says_how_much_was_tested_and_how_much_is_needed()
    {
        var evidence = new TestEvidence { DiskSize = 64_000_000_000, PlannedBytes = 52_400_000_000, WrittenBytes = 52_400_000_000, VerifiedBytes = 52_400_000_000 };
        var v = VerdictEngine.Decide(evidence);

        Assert.Equal("Not checked: 81.9% of the drive was tested, and the check needs about 95.0%.", Text.CapacityResult(v, evidence));
        Assert.StartsWith("The 52.4 GB tested worked correctly today. Only 81.9% of the drive was tested", Text.Explanation(v, evidence));
    }

    [Fact]
    public void The_drive_is_named_by_its_own_vendor_and_product_not_Windows_name()
    {
        Assert.Equal("SanDisk 3.2Gen1", DeviceCard.DisplayName(SanDisk()));

        var nameless = SanDisk() with { Report = null, Device = new Windows.DiskCandidate("X", "Y", " USB  SanDisk 3.2Gen1 USB Device") };
        Assert.Equal("USB SanDisk 3.2Gen1", DeviceCard.DisplayName(nameless));
    }

    [Theory]
    [InlineData(61_524_148_224, "61.5 GB")]
    [InlineData(268_435_456, "268 MB")]
    [InlineData(512_000, "512 KB")]
    [InlineData(12, "12 bytes")]
    [InlineData(0, "0 bytes")]
    public void Sizes_are_in_decimal_units_as_drives_are_sold(long bytes, string expected) => Assert.Equal(expected, Text.Bytes(bytes));

    [Fact]
    public void The_read_back_result_counts_files_in_plain_English()
    {
        Assert.Equal("Read 992 files (28 MB). All of them could be read.", Text.FilesResult(new FileReadResult(992, 28_000_000, 0, [])));
        Assert.Equal("Read 1 file (12 bytes). It could be read.", Text.FilesResult(new FileReadResult(1, 12, 0, [])));
        Assert.Equal("There are no files on it to read.", Text.FilesResult(new FileReadResult(0, 0, 0, [])));
        Assert.Equal("Read 1,204 files (3.1 GB). All of them could be read. 1 was skipped because Windows wouldn't open it (in use, or access denied).",
            Text.FilesResult(new FileReadResult(1204, 3_100_000_000, 1, [])));
    }

    [Fact]
    public void Disk_errors_that_read_the_same_are_shown_once_with_a_count()
    {
        var at = new DateTimeOffset(2026, 11, 12, 1, 21, 30, TimeSpan.Zero);
        DiskEvent Controller(DateTimeOffset time) => new(DiskEventKind.ControllerError, time, 3, DiskEventMatch.EarlierSameDiskNumber);

        var rows = InspectViewModel.EventRows([Controller(at.AddSeconds(3)), Controller(at.AddSeconds(2)), Controller(at.AddSeconds(1)), Controller(at.AddMinutes(-55))]);

        Assert.Equal(2, rows.Count);
        Assert.EndsWith(": Controller error (3 times)", rows[0].Title);
        Assert.EndsWith(": Controller error", rows[1].Title);
        Assert.All(rows, r => Assert.True(r.IsError && r.HasCaveat));
    }

    [Fact]
    public void Times_are_rounded_to_minutes()
    {
        Assert.Equal("1 h 14 min", Text.Duration(TimeSpan.FromMinutes(74.2)));
        Assert.Equal("12 min", Text.Duration(TimeSpan.FromMinutes(12)));
        Assert.Equal("under a minute", Text.Duration(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Each_status_on_the_drive_list_comes_with_what_to_do()
    {
        Assert.Equal((Text.StatusReady, ""), Text.DeviceStatus(null));
        Assert.Equal(Text.StatusNoCard, Text.DeviceStatus(new VerdictResult(Verdict.NoCardInReader, CapacityResult.NotChecked, 0)).Status);
        var ssd = Text.DeviceStatus(new VerdictResult(Verdict.CantTest, CapacityResult.NotChecked, 0, CantTestReason: CantTestReason.ExternalSsdOrHardDrive));
        Assert.Equal(Text.StatusNotSupported, ssd.Status);
        var full = Text.DeviceStatus(new VerdictResult(Verdict.CantTest, CapacityResult.NotChecked, 0, CantTestReason: CantTestReason.NotEnoughFreeSpace));
        Assert.Equal(Text.StatusCantTest, full.Status);
        Assert.Contains("268 MB", full.Advice);
    }
}
