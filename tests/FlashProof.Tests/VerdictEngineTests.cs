using FlashProof.Core;

namespace FlashProof.Tests;

// Design §3: the verdict rules and which evidence wins.
public class VerdictEngineTests
{
    const long Disk = 1000 * TestRecord.Size;

    /// <summary>A clean run that verified this many records of a 1000-record disk.</summary>
    static TestEvidence Clean(long records = 980) => new()
    {
        DiskSize = Disk,
        PlannedBytes = records * TestRecord.Size,
        WrittenBytes = records * TestRecord.Size,
        VerifiedBytes = records * TestRecord.Size,
        FillEnd = FillEnd.PlanReached,
        Counts = { Ok = records },
    };

    static IoError Error(IoErrorKind kind, int code = 0) => new(TestPhase.Write, 0, kind, code, "");

    [Theory]
    [InlineData(1000, Verdict.PassedCapacityConfirmed, CapacityResult.Confirmed)]
    [InlineData(950, Verdict.PassedCapacityConfirmed, CapacityResult.Confirmed)]
    [InlineData(949, Verdict.PassedCapacityNotChecked, CapacityResult.NotChecked)]
    [InlineData(300, Verdict.PassedCapacityNotChecked, CapacityResult.NotChecked)]
    public void Capacity_is_confirmed_only_with_95_percent_coverage(long records, Verdict verdict, CapacityResult capacity)
    {
        var result = VerdictEngine.Decide(Clean(records));

        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(capacity, result.Capacity);
        Assert.Equal(records / 1000.0, result.Coverage, tolerance: 1e-12);
    }

    [Fact]
    public void Disk_full_before_the_plan_still_passes()
    {
        var e = Clean();
        e.PlannedBytes *= 2;
        e.FillEnd = FillEnd.DiskFull;

        Assert.Equal(Verdict.PassedCapacityConfirmed, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void A_consistent_wrap_size_is_fake_capacity()
    {
        var e = Clean();
        e.Counts.Misplaced = 500;
        e.WrapSize = 400 * TestRecord.Size;

        var result = VerdictEngine.Decide(e);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict);
        Assert.Equal(CapacityResult.Fake, result.Capacity);
        Assert.Equal(400 * TestRecord.Size, result.RealCapacityBytes);
    }

    [Fact]
    public void A_boundary_well_into_the_test_is_fake_capacity()
    {
        var e = Clean();
        e.Counts.Blank = 500;
        e.Boundary = new Boundary(DiskOffset: 480 * TestRecord.Size, TestedBytesBelow: 480 * TestRecord.Size);

        var result = VerdictEngine.Decide(e);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict);
        Assert.Equal(480 * TestRecord.Size, result.RealCapacityBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)] // 4% of the 980 tested records
    public void A_boundary_near_the_start_of_the_test_is_not_storing_data(long recordsBelow)
    {
        var e = Clean();
        e.Counts.Blank = 900;
        e.Boundary = new Boundary(DiskOffset: 600 * TestRecord.Size, TestedBytesBelow: recordsBelow * TestRecord.Size);

        var result = VerdictEngine.Decide(e);

        Assert.Equal(Verdict.NotStoringData, result.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Capacity);
    }

    [Theory]
    [InlineData(RecordClass.BitErrors)]
    [InlineData(RecordClass.Garbage)]
    [InlineData(RecordClass.Misplaced)] // with no consistent wrap size
    [InlineData(RecordClass.Blank)]     // with no clear boundary
    [InlineData(RecordClass.Stale)]
    public void Any_bad_record_without_a_fake_pattern_is_data_errors(RecordClass c)
    {
        var e = Clean();
        e.Counts.Add(c, 1);

        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(e).Verdict);
    }

    [Theory]
    [InlineData(IoErrorKind.Other, 23)]            // unreadable sector
    [InlineData(IoErrorKind.FileSystemCorrupt, 1392)]
    [InlineData(IoErrorKind.FileSystemCorrupt, 1393)]
    public void IO_and_file_system_errors_are_data_errors(IoErrorKind kind, int code)
    {
        var e = Clean();
        e.Errors.Add(Error(kind, code));

        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void Bad_early_check_samples_are_data_errors_even_if_verification_was_clean()
    {
        var e = Clean();
        e.EarlyCheckBadSamples = 1;
        e.FillEnd = FillEnd.EarlyFakeCheck;

        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void Unverified_data_is_not_a_pass()
    {
        var e = Clean();
        e.VerifiedBytes -= TestRecord.Size;

        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void Write_protection_is_write_protected()
    {
        var e = Clean(0);
        e.Errors.Add(Error(IoErrorKind.WriteProtected, 19));

        Assert.Equal(Verdict.WriteProtected, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void A_removed_device_needs_the_users_answer()
    {
        var e = Clean();
        e.Errors.Add(Error(IoErrorKind.DeviceRemoved, 1167));

        Assert.Equal(Verdict.ConnectionProblemSuspected, VerdictEngine.Decide(e).Verdict);
        var unplugged = VerdictEngine.Decide(e, userSaysUnplugged: true);
        Assert.Equal(Verdict.Interrupted, unplugged.Verdict);
        Assert.Equal(InterruptReason.UserUnplugged, unplugged.InterruptReason);
    }

    [Fact]
    public void A_drive_that_overwrote_its_own_layout_has_data_errors_not_a_connection_problem()
    {
        var e = Clean();
        e.Errors.Add(Error(IoErrorKind.DeviceRemoved, 34));
        e.LayoutChanged = true;

        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(e).Verdict);
    }

    [Fact]
    public void A_changed_device_overrides_fake_evidence()
    {
        // Data read back from a swapped card says nothing about the confirmed one.
        var e = Clean();
        e.WrapSize = 400 * TestRecord.Size;
        e.Interrupted = InterruptReason.DeviceChanged;

        var result = VerdictEngine.Decide(e);

        Assert.Equal(Verdict.Interrupted, result.Verdict);
        Assert.Equal(InterruptReason.DeviceChanged, result.InterruptReason);
        Assert.Equal(CapacityResult.NotChecked, result.Capacity);
    }

    [Fact]
    public void Cant_test_comes_first()
    {
        var e = new TestEvidence { DiskSize = Disk, CantTest = CantTestReason.NotEnoughFreeSpace };

        var result = VerdictEngine.Decide(e);

        Assert.Equal(Verdict.CantTest, result.Verdict);
        Assert.Equal(CantTestReason.NotEnoughFreeSpace, result.CantTestReason);
    }

    [Fact]
    public void Nothing_written_is_not_a_pass() =>
        Assert.Equal(Verdict.DataErrors, VerdictEngine.Decide(Clean(0)).Verdict);
}
