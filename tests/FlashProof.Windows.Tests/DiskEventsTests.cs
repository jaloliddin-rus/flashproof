using System.Globalization;
using FlashProof.Core;
using Xunit.Abstractions;

namespace FlashProof.Windows.Tests;

// Design §6.3: recent disk errors from the System log, matched to one drive by its disk number.
public class DiskEventsTests(ITestOutputHelper output)
{
    // Real events from the development PC, with its name replaced.
    const string Retried = """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='disk'/><EventID Qualifiers='32772'>153</EventID><Version>0</Version><Level>3</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-11-10T17:40:06.2677295Z'/><EventRecordID>57492</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='9436'/><Channel>System</Channel><Computer>TEST-PC</Computer><Security/></System><EventData><Data>\Device\Harddisk3\DR6</Data><Data>0x2</Data><Data>3</Data><Data>\Device\0000023e</Data><Binary>0F01040004002C00000000009900048000000000000000000000000000000000000000000000000000000928</Binary></EventData></Event>""";
    const string ControllerError = """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='disk'/><EventID Qualifiers='49156'>11</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-11-10T17:37:12.5493970Z'/><EventRecordID>57490</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='18176'/><Channel>System</Channel><Computer>TEST-PC</Computer><Security/></System><EventData><Data>\Device\Harddisk2\DR4</Data><Binary>0E03800001000000000000000B0004C0</Binary></EventData></Event>""";
    const string HyperV129 = """<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Hyper-V-Hypervisor' Guid='{52fc89f8-995e-434c-a91e-199986449890}'/><EventID>129</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000400000000000</Keywords><TimeCreated SystemTime='2026-10-14T09:48:43.7923974Z'/><EventRecordID>50205</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>TEST-PC</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='HardwarePresent'>false</Data><Data Name='HardwareEnabled'>false</Data><Data Name='Policy'>0x0</Data></EventData></Event>""";

    static readonly DateTimeOffset At = new(2026, 11, 10, 17, 40, 6, TimeSpan.Zero);

    static LoggedEvent Disk(int id, string device = @"\Device\Harddisk2\DR4", DateTimeOffset? time = null) => new("disk", id, time ?? At, [device]);

    [Fact]
    public void A_real_retried_IO_event_is_parsed()
    {
        var e = DiskEvents.Parse(Retried);

        Assert.NotNull(e);
        Assert.Equal("disk", e.Provider);
        Assert.Equal(153, e.Id);
        Assert.Equal(At.AddTicks(2_677_295), e.Time);
        Assert.Equal([@"\Device\Harddisk3\DR6", "0x2", "3", @"\Device\0000023e"], e.Data);
    }

    [Fact]
    public void Events_match_by_the_disk_number_in_the_device_path()
    {
        var arrival = At.AddHours(-1);

        Assert.Equal(new DiskEvent(DiskEventKind.RetriedIo, At.AddTicks(2_677_295), 3, DiskEventMatch.ThisPlugIn),
            DiskEvents.About(DiskEvents.Parse(Retried)!, 3, arrival, onUas: false));
        Assert.Null(DiskEvents.About(DiskEvents.Parse(Retried)!, 2, arrival, onUas: false));
        Assert.Equal(DiskEventKind.ControllerError, DiskEvents.About(DiskEvents.Parse(ControllerError)!, 2, arrival, onUas: false)?.Kind);
    }

    [Fact]
    public void Disk_1_is_not_disk_11()
    {
        Assert.Null(DiskEvents.About(Disk(7, @"\Device\Harddisk11\DR3"), 1, null, onUas: false));
        Assert.Equal(11, DiskEvents.About(Disk(7, @"\Device\Harddisk11\DR3"), 11, null, onUas: false)?.DiskNumber);
    }

    [Fact]
    public void Events_from_before_the_drive_was_plugged_in_may_be_another_drives()
    {
        Assert.Equal(DiskEventMatch.EarlierSameDiskNumber, DiskEvents.About(Disk(153), 2, At.AddSeconds(1), onUas: false)?.Match);
        Assert.Equal(DiskEventMatch.EarlierSameDiskNumber, DiskEvents.About(Disk(153), 2, null, onUas: false)?.Match);
        Assert.Equal(DiskEventMatch.ThisPlugIn, DiskEvents.About(Disk(153), 2, At, onUas: false)?.Match);
    }

    public static TheoryData<int, DiskEventKind, bool> Kinds => new()
    {
        { 7, DiskEventKind.BadBlock, true },
        { 11, DiskEventKind.ControllerError, true },
        { 153, DiskEventKind.RetriedIo, true },
        { 157, DiskEventKind.SurpriseRemoval, false },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Disk_events_and_which_count_as_errors(int id, DiskEventKind kind, bool isError)
    {
        var e = DiskEvents.About(Disk(id), 2, null, onUas: false);

        Assert.Equal(kind, e?.Kind);
        Assert.Equal(isError, e?.IsError);
    }

    [Fact]
    public void Other_disk_event_IDs_are_ignored()
    {
        Assert.Null(DiskEvents.About(Disk(51), 2, null, onUas: false));
    }

    [Fact]
    public void Only_the_UAS_driver_s_129_counts_and_only_for_a_UAS_drive()
    {
        var uas = new LoggedEvent("uaspstor", 129, At, [@"\Device\RaidPort1"]);

        Assert.Null(DiskEvents.About(DiskEvents.Parse(HyperV129)!, 2, null, onUas: true));
        Assert.Equal(new DiskEvent(DiskEventKind.UasReset, At, null, DiskEventMatch.UasDriver), DiskEvents.About(uas, 2, null, onUas: true));
        Assert.Null(DiskEvents.About(uas, 2, null, onUas: false));
    }

    // Real: the SanDisk ejected 3.5 s into a full test on 12 Nov 2026 (session e2e-20261112-012101). The test's start
    // and end from its journal, and the five controller errors Windows logged before the worker was told to let go.
    static readonly EjectedTest Ejected = new(SanDiskId, Utc("01:21:01.9190000"), Utc("01:21:10.6260000"));
    static readonly DateTimeOffset[] EjectBurst =
        [Utc("01:21:05.5183229"), Utc("01:21:06.0193032"), Utc("01:21:06.5218385"), Utc("01:21:07.0368294"), Utc("01:21:07.5395746")];

    const string SanDiskId = @"USBSTOR\DISK&VEN_USB&PROD_SANDISK_3.2GEN1\0001";

    static DateTimeOffset Utc(string time) => DateTimeOffset.Parse($"2026-11-12T{time}Z", CultureInfo.InvariantCulture);

    static DiskEvent Logged(DiskEventKind kind, DateTimeOffset time) => new(kind, time, 2, DiskEventMatch.ThisPlugIn);

    [Fact]
    public void The_controller_errors_of_a_drive_ejected_during_a_test_dont_count()
    {
        foreach (var time in EjectBurst)
        {
            var e = DiskEvents.Marked(Logged(DiskEventKind.ControllerError, time), SanDiskId, [Ejected]);

            Assert.True(e.DuringEjectedTest);
            Assert.False(e.IsError);
        }
    }

    public static TheoryData<DiskEventKind, double, bool> AroundTheEnd => new()
    {
        { DiskEventKind.ControllerError, -30, true },
        { DiskEventKind.ControllerError, 5, true },
        { DiskEventKind.ControllerError, -31, false },
        { DiskEventKind.ControllerError, 6, false },
        { DiskEventKind.RetriedIo, -3, false },
        { DiskEventKind.BadBlock, -3, false },
    };

    [Theory]
    [MemberData(nameof(AroundTheEnd))]
    public void Only_controller_errors_from_30_s_before_to_5_s_after_the_end_are_the_ejects(DiskEventKind kind, double secondsFromEnd, bool marked)
    {
        var longTest = Ejected with { Started = Ejected.Finished.AddMinutes(-10) };

        var e = DiskEvents.Marked(Logged(kind, longTest.Finished.AddSeconds(secondsFromEnd)), SanDiskId, [longTest]);

        Assert.Equal(marked, e.DuringEjectedTest);
        Assert.Equal(!marked, e.IsError);
    }

    [Fact]
    public void Controller_errors_before_the_test_started_or_on_another_drive_count()
    {
        var beforeStart = Logged(DiskEventKind.ControllerError, Ejected.Started.AddSeconds(-1));
        var during = Logged(DiskEventKind.ControllerError, EjectBurst[0]);

        Assert.True(DiskEvents.Marked(beforeStart, SanDiskId, [Ejected]).IsError);
        Assert.True(DiskEvents.Marked(during, @"USBSTOR\DISK&VEN_OTHER&PROD_STICK\0002", [Ejected]).IsError);
        Assert.True(DiskEvents.Marked(during, SanDiskId, []).IsError);
    }

    static Journal JournalOf(InterruptReason? reason, Action<TestEvidence>? found = null)
    {
        var evidence = new TestEvidence();
        found?.Invoke(evidence);
        return new Journal
        {
            SessionId = "e2e-20261112-012101",
            Identity = new VolumeIdentity(SanDiskId, "USB SanDisk 3.2Gen1", "0001", 61_524_148_224, 512, 512, Utc("01:15:27"), 0,
                "MBR:1A2B3C4D", @"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\", 1_310_785_536),
            Result = new TestResult
            {
                SessionId = "e2e-20261112-012101",
                Verdict = new VerdictResult(Verdict.Interrupted, CapacityResult.NotChecked, 0, InterruptReason: reason),
                Evidence = evidence,
                StartedAt = Ejected.Started,
                FinishedAt = Ejected.Finished,
            },
        };
    }

    [Fact]
    public void A_journal_names_an_ejected_test_only_if_the_drive_was_removed_and_nothing_was_found_wrong()
    {
        Assert.Equal(Ejected, DiskEvents.EjectedTestIn(JournalOf(InterruptReason.UserUnplugged)));

        Assert.Null(DiskEvents.EjectedTestIn(JournalOf(InterruptReason.Cancelled)));
        Assert.Null(DiskEvents.EjectedTestIn(JournalOf(InterruptReason.PcSlept)));
        Assert.Null(DiskEvents.EjectedTestIn(JournalOf(InterruptReason.UserUnplugged,
            e => e.Errors.Add(new IoError(TestPhase.Write, 4 << 20, IoErrorKind.DeviceRemoved, 1167, "The device is not connected.")))));
        Assert.Null(DiskEvents.EjectedTestIn(JournalOf(InterruptReason.UserUnplugged, e => e.Counts.Garbage = 1)));
        Assert.Null(DiskEvents.EjectedTestIn(JournalOf(InterruptReason.UserUnplugged, e => e.EarlyCheckBadSamples = 1)));
        var unfinished = JournalOf(InterruptReason.UserUnplugged);
        unfinished.Result = null;
        Assert.Null(DiskEvents.EjectedTestIn(unfinished));
    }

    [Fact]
    public void Ejected_tests_are_read_from_recent_journals_and_unreadable_ones_are_skipped()
    {
        string sessions = Path.Combine(Path.GetTempPath(), "FlashProofTests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(DiskEvents.ReadEjectedTests(sessions));

            JournalStore.Save(Path.Combine(sessions, "ejected", "journal.json"), JournalOf(InterruptReason.UserUnplugged));
            JournalStore.Save(Path.Combine(sessions, "cancelled", "journal.json"), JournalOf(InterruptReason.Cancelled));
            string old = Path.Combine(sessions, "ejected-two-days-ago", "journal.json");
            JournalStore.Save(old, JournalOf(InterruptReason.UserUnplugged));
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
            Directory.CreateDirectory(Path.Combine(sessions, "corrupt"));
            File.WriteAllText(Path.Combine(sessions, "corrupt", "journal.json"), "{");
            Directory.CreateDirectory(Path.Combine(sessions, "no-journal"));

            Assert.Equal([Ejected], DiskEvents.ReadEjectedTests(sessions));
        }
        finally
        {
            if (Directory.Exists(sessions))
                Directory.Delete(sessions, recursive: true);
        }
    }

    /// <summary>Reads this computer's System log through the event log service; no disk is opened.</summary>
    [Fact]
    public void The_log_accepts_the_query_and_returns_only_disk_events_and_UAS_resets()
    {
        var events = DiskEvents.ReadLog(TimeSpan.FromDays(60));

        Assert.NotNull(events);
        Assert.All(events, e => Assert.True(
            (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 7 or 11 or 153 or 157)
            || (e.Provider.Equals("UASPStor", StringComparison.OrdinalIgnoreCase) && e.Id == 129), $"{e.Provider} {e.Id}"));
        Assert.All(events, e => Assert.True(e.Time > DateTimeOffset.UtcNow.AddDays(-60)));
        output.WriteLine($"{events.Count} events in 60 days: {string.Join(", ", events.GroupBy(e => $"{e.Provider} {e.Id}").Select(g => $"{g.Key} ×{g.Count()}"))}");
    }
}
