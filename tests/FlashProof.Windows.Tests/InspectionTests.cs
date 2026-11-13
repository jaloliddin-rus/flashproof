using FlashProof.Core;
using static FlashProof.Windows.Tests.EligibilityTests;

namespace FlashProof.Windows.Tests;

// Design §6.3: what Inspect reports, and when it may say "No problems found in these checks".
public class InspectionTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    static DiskEvent Event(DiskEventKind kind, DiskEventMatch match = DiskEventMatch.ThisPlugIn) => new(kind, Now, 2, match);

    [Fact]
    public void A_drive_with_no_events_and_no_faults_has_no_problems_found()
    {
        var report = Inspection.Report(SanDisk(), Pc, []);

        Assert.Null(report.Status);
        Assert.Empty(report.Problems);
        Assert.True(report.NoProblemsFound);
    }

    [Fact]
    public void Surprise_removals_are_listed_but_are_not_problems()
    {
        var report = Inspection.Report(SanDisk(), Pc, [Event(DiskEventKind.SurpriseRemoval)]);

        Assert.Single(report.Events!);
        Assert.True(report.NoProblemsFound);
    }

    public static TheoryData<DiskEventKind, DiskEventMatch> Errors => new()
    {
        { DiskEventKind.BadBlock, DiskEventMatch.ThisPlugIn },
        { DiskEventKind.ControllerError, DiskEventMatch.ThisPlugIn },
        { DiskEventKind.RetriedIo, DiskEventMatch.ThisPlugIn },
        { DiskEventKind.RetriedIo, DiskEventMatch.EarlierSameDiskNumber },
        { DiskEventKind.UasReset, DiskEventMatch.UasDriver },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Disk_errors_are_problems_even_when_the_match_is_uncertain(DiskEventKind kind, DiskEventMatch match)
    {
        var report = Inspection.Report(SanDisk(), Pc, [Event(DiskEventKind.SurpriseRemoval), Event(kind, match)]);

        Assert.Equal([InspectProblem.RecentDiskErrors], report.Problems);
        Assert.False(report.NoProblemsFound);
    }

    [Fact]
    public void The_controller_errors_of_an_ejected_test_are_listed_but_are_not_problems()
    {
        var ejected = Event(DiskEventKind.ControllerError) with { DuringEjectedTest = true };

        var report = Inspection.Report(SanDisk(), Pc, [ejected, ejected]);
        var withAnother = Inspection.Report(SanDisk(), Pc, [ejected, Event(DiskEventKind.ControllerError)]);

        Assert.Equal(2, report.Events!.Count);
        Assert.True(report.NoProblemsFound);
        Assert.Equal([InspectProblem.RecentDiskErrors], withAnother.Problems);
    }

    [Fact]
    public void Without_the_event_log_it_never_says_no_problems_found()
    {
        var report = Inspection.Report(SanDisk(), Pc, null);

        Assert.Empty(report.Problems);
        Assert.False(report.NoProblemsFound);
    }

    [Fact]
    public void An_empty_card_reader_never_says_no_problems_found()
    {
        var report = Inspection.Report(SanDisk(media: false), Pc, []);

        Assert.Equal(Verdict.NoCardInReader, report.Status?.Verdict);
        Assert.False(report.NoProblemsFound);
    }

    [Fact]
    public void A_write_protected_drive_is_a_problem()
    {
        var report = Inspection.Report(SanDisk(writeProtected: true), Pc, []);

        Assert.Equal(Verdict.WriteProtected, report.Status?.Verdict);
        Assert.Equal([InspectProblem.WriteProtected], report.Problems);
    }

    [Fact]
    public void Write_protection_counts_even_when_another_check_decides_the_status()
    {
        var report = Inspection.Report(SanDisk(writeProtected: true) with { Volumes = [] }, Pc, []);

        Assert.Equal(CantTestReason.NoFileSystem, report.Status?.CantTestReason);
        Assert.Equal([InspectProblem.WriteProtected], report.Problems);
    }

    [Fact]
    public void A_policy_that_blocks_all_writes_is_not_blamed_on_the_drive()
    {
        var blocked = Pc with { Policy = new WritePolicy(RemovableDisksDenyWrite: true, StorageWriteProtect: false, BitLockerDenyWrite: false) };

        var report = Inspection.Report(SanDisk(writeProtected: true), blocked, []);

        Assert.Equal(Verdict.WritingBlockedByWindows, report.Status?.Verdict);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void Unreadable_files_are_a_problem_but_ones_Windows_wouldnt_open_are_not()
    {
        var report = Inspection.Report(SanDisk(), Pc, [Event(DiskEventKind.SurpriseRemoval)]);

        var clean = Inspection.WithFiles(report, Pc, new FileReadResult(10, 1 << 20, Skipped: 2, []));
        var bad = Inspection.WithFiles(report, Pc, new FileReadResult(9, 1 << 20, 0, [new UnreadableFile(@"ISO\a.iso", IsFolder: false, 1392)]));

        Assert.True(clean.NoProblemsFound);
        Assert.Equal([InspectProblem.UnreadableFiles], bad.Problems);
        Assert.Same(report.Events, bad.Events);
    }

    [Fact]
    public void Any_volume_past_the_end_of_the_disk_is_a_problem_not_only_the_test_volume()
    {
        var disk = SanDisk();
        var efi = Efi with { Offset = disk.Size - (16 << 20) };

        var report = Inspection.Report(disk with { Volumes = [Ventoy(), efi] }, Pc, []);

        Assert.Null(report.Status);
        Assert.Equal([InspectProblem.VolumeLargerThanDisk], report.Problems);
    }
}
