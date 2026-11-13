using FlashProof.Core;
using FlashProof.Tests.Emulator;
using static FlashProof.Tests.Emulator.Harness;

namespace FlashProof.Tests;

// Design §8: the full test on every emulated behaviour, with the required results. Drives are 64 MiB; the volume's
// data area starts at 512 KiB, so an empty drive has 63.5 MiB free and the test covers 63 MiB (98% of the disk).
public class EmulatorTests
{
    // ---- Genuine drive

    [Fact]
    public async Task Genuine_empty_drive_passes_with_capacity_confirmed()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.Confirmed, result.Verdict.Capacity);
        Assert.Equal(63 * MiB, result.Evidence.VerifiedBytes);
        Assert.Equal(FillEnd.PlanReached, result.Evidence.FillEnd);
        Assert.Equal(0, result.Evidence.EarlyCheckBadSamples);

        // After a pass the test files and their folder are gone. The journal is on the host, never on the device.
        Assert.False(result.TestFilesKept);
        Assert.Empty(device.FileNames);
        Assert.Empty(device.FolderNames);
        var journal = JournalStore.Load(host.JournalPath);
        Assert.Equal(JournalStatus.Finished, journal.Status);
        Assert.Equal(8, journal.Files.Count);
        Assert.Equal(Verdict.PassedCapacityConfirmed, journal.Result!.Verdict.Verdict);
    }

    [Fact]
    public async Task Genuine_drive_keeps_existing_files_byte_identical_and_capacity_is_not_confirmed_under_95_percent()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        device.AddExistingFile("old.bin", 5 * MiB, seed: 1);
        var photos = device.AddExistingFile("photos.bin", 3 * MiB, seed: 2);
        var notes = device.AddExistingFile("notes.txt", 2 * MiB, seed: 3);
        device.DeleteFile("old.bin"); // leaves a hole, so the test files are fragmented around the user's files

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.PassedCapacityNotChecked, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
        Assert.InRange(result.Verdict.Coverage, 0.90, 0.95); // 58 MiB of 64 MiB
        Assert.Equal(photos, await device.ReadFileFromDiskAsync("photos.bin"));
        Assert.Equal(notes, await device.ReadFileFromDiskAsync("notes.txt"));
        Assert.Equal(["notes.txt", "photos.bin"], device.FileNames.Order());
        Assert.True(JournalStore.Load(host.JournalPath).Files[0].Extents.Count > 1);
    }

    [Fact]
    public async Task Test_files_can_be_kept_after_a_pass_and_are_only_in_the_session_folder()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);

        var result = await RunAsync(device, host, keepTestFiles: true);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.True(result.TestFilesKept);
        Assert.Equal(8, device.FileNames.Count);
        Assert.All(device.FileNames, name => Assert.StartsWith("FlashProof-test", name));
    }

    [Fact]
    public async Task Disk_full_ends_the_fill_normally()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { ExtraReportedFreeSpace = 8 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(FillEnd.DiskFull, result.Evidence.FillEnd);
        Assert.Equal(71 * MiB, result.Evidence.PlannedBytes);
        Assert.Equal(127 * MiB / 2, result.Evidence.WrittenBytes); // everything that fits
        Assert.Equal(127 * MiB / 2, result.Evidence.VerifiedBytes);
    }

    // ---- Fakes

    [Theory]
    [InlineData(16 * MiB)]
    [InlineData(20 * MiB + 1536)] // not a power of two, nor a multiple of 4 KiB
    public async Task Wraparound_fake_is_fake_capacity_with_its_real_size(long realCapacity)
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.Wraparound, RealCapacity = realCapacity };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.Fake, result.Verdict.Capacity);
        Assert.Equal(realCapacity, result.Verdict.RealCapacityBytes);
        Assert.Equal(FillEnd.EarlyFakeCheck, result.Evidence.FillEnd);
        Assert.True(result.Evidence.WrittenBytes < result.Evidence.PlannedBytes);
        Assert.True(result.TestFilesKept);
    }

    [Fact]
    public async Task Wraparound_fake_is_caught_by_the_early_check_at_the_design_sizes()
    {
        // A "4 GiB" drive with about 100 MiB of real flash, with the design's 1 GiB files and 4 MiB I/Os.
        // The first early check stops the fill; the data written by then has wrapped about ten times.
        using var host = new HostFolder();
        const long real = 100 * MiB + 1536;
        var device = new SimulatedDevice(4096 * MiB) { Flash = FlashKind.Wraparound, RealCapacity = real };

        var result = await RunAsync(device, host, new FullTestOptions());

        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(real, result.Verdict.RealCapacityBytes);
        Assert.Equal(FillEnd.EarlyFakeCheck, result.Evidence.FillEnd);
        Assert.Equal(1024 * MiB, result.Evidence.WrittenBytes);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public async Task Limbo_fake_is_fake_capacity_from_where_it_drops_writes(byte erased)
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB, erasedByte: erased) { Flash = FlashKind.Limbo, RealCapacity = 24 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(24 * MiB, result.Verdict.RealCapacityBytes);
        Assert.True(result.Evidence.Counts.Blank > 0);
        Assert.Equal(FillEnd.EarlyFakeCheck, result.Evidence.FillEnd);
    }

    [Fact]
    public async Task Limbo_fake_returning_an_older_sessions_records_is_fake_capacity()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.Limbo, RealCapacity = 24 * MiB };
        device.FillWithStaleRecords(oldSeed: 42, offset: 24 * MiB, length: 40 * MiB);

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(24 * MiB, result.Verdict.RealCapacityBytes);
        Assert.True(result.Evidence.Counts.Stale > 0);
        Assert.Equal(0, result.Evidence.Counts.Blank);
    }

    [Fact]
    public async Task RAM_cache_fake_with_no_real_flash_is_not_storing_data()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.RamCache, RealCapacity = 0, CacheBytes = 1 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.NotStoringData, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
        Assert.Equal(1 * MiB / TestRecord.Size, result.Evidence.Counts.Ok); // only the cached last 1 MiB read back
    }

    [Fact]
    public async Task RAM_cache_fake_with_some_real_flash_is_fake_capacity()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.RamCache, RealCapacity = 24 * MiB, CacheBytes = 2 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(24 * MiB, result.Verdict.RealCapacityBytes);
    }

    [Fact]
    public async Task Wraparound_fake_whose_real_flash_is_filled_by_files_never_gets_capacity_confirmed()
    {
        // The test space all maps onto the real flash that the user's file occupies, so every record reads back
        // correctly. File mode can't tell this drive from a genuine one; what it must not do is confirm the capacity.
        // The user's file is destroyed, which is why the app asks for a backup first (design §5.3).
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.Wraparound, RealCapacity = 32 * MiB };
        var original = device.AddExistingFile("movie.mkv", 63 * MiB / 2, seed: 7);

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.PassedCapacityNotChecked, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
        Assert.True(result.Verdict.Coverage < 0.5);
        Assert.NotEqual(original, await device.ReadFileFromDiskAsync("movie.mkv"));
    }

    [Fact]
    public async Task Limbo_fake_whose_real_flash_is_filled_by_files_is_not_storing_data()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.Limbo, RealCapacity = 32 * MiB };
        device.AddExistingFile("movie.mkv", 63 * MiB / 2, seed: 7);

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.NotStoringData, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
    }

    // ---- Failing drives

    [Fact]
    public async Task Bit_flips_are_data_errors()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        device.BitFlips.Add(5 * MiB + 100);
        device.BitFlips.Add(30 * MiB + 7);
        for (int i = 0; i < 65; i++)
            device.BitFlips.Add(40 * MiB + i * 7); // 65 flipped bits in one record: GARBAGE rather than BIT_ERRORS

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.DataErrors, result.Verdict.Verdict);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
        Assert.Equal(2, result.Evidence.Counts.BitErrors);
        Assert.Equal(1, result.Evidence.Counts.Garbage);
        Assert.Equal(3, result.Evidence.Counts.NotOk);
        Assert.True(result.TestFilesKept);
    }

    [Fact]
    public async Task Unreadable_range_is_data_errors_with_the_read_error_recorded()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        device.UnreadableRanges.Add(new DiskRange(40 * MiB, 4096));

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.DataErrors, result.Verdict.Verdict);
        var error = Assert.Single(result.Evidence.Errors);
        Assert.Equal(23, error.NativeCode);
        Assert.True(result.Evidence.VerifiedBytes < result.Evidence.WrittenBytes); // stopped at the error, no retries
    }

    [Fact]
    public async Task Slow_drive_passes_and_its_speeds_are_measured()
    {
        using var host = new HostFolder();
        var clock = new ManualClock();
        var device = new SimulatedDevice(64 * MiB) { Clock = clock, WriteBytesPerSecond = 4e6, ReadBytesPerSecond = 20e6 };

        var result = await RunAsync(device, host, clock: clock);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(4e6, result.Speed.AverageWrite!.Value, tolerance: 4e3);
        Assert.Equal(4e6, result.Speed.WorstWrite!.Value, tolerance: 4e3);
        Assert.Equal(20e6, result.Speed.AverageRead!.Value, tolerance: 2e4);
        Assert.Equal(16, result.Speed.WriteSlices.Count); // 63 MiB in 4 MiB slices
        Assert.Equal(16, result.Speed.ReadSlices.Count);
    }

    // ---- Hangs and disconnects

    [Fact]
    public async Task Hang_that_honours_cancellation_ends_as_interrupted_when_stopped()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { HangAt = 20 * MiB, HangHonoursCancellation = true };
        using var cts = new CancellationTokenSource();

        var run = RunAsync(device, host, ct: cts.Token);
        await device.HangReached;
        Assert.False(run.IsCompleted);
        cts.Cancel();
        var result = await run;

        Assert.Equal(Verdict.Interrupted, result.Verdict.Verdict);
        Assert.Equal(InterruptReason.Cancelled, result.Verdict.InterruptReason);
        Assert.Equal(CapacityResult.NotChecked, result.Verdict.Capacity);
        Assert.True(result.TestFilesKept);
        Assert.Equal(JournalStatus.Finished, JournalStore.Load(host.JournalPath).Status);
    }

    [Fact]
    public async Task Hang_that_ignores_cancellation_leaves_an_unfinished_journal_that_counts_as_interrupted()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { HangAt = 20 * MiB };
        using var cts = new CancellationTokenSource();

        var run = RunAsync(device, host, ct: cts.Token);
        await device.HangReached;
        cts.Cancel();

        // The I/O can't be forced to finish, so the run can't end either: only killing the worker contains it (design §4).
        Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(300)));

        // If the worker is killed now, the host journal shows an unfinished session with files 1 and 2 checkpointed.
        var journal = JournalStore.Load(host.JournalPath);
        Assert.Equal(JournalStatus.Running, journal.Status);
        Assert.Equal(2, journal.Checkpoints.Count);
        var recovered = FullTestRunner.ResultOf(journal);
        Assert.Equal(Verdict.Interrupted, recovered.Verdict.Verdict);
        Assert.Equal(InterruptReason.Unfinished, recovered.Verdict.InterruptReason);
        Assert.True(recovered.TestFilesKept);

        device.ReleaseHang();
        await run;
    }

    [Fact]
    public async Task Disconnect_mid_test_is_a_connection_problem_unless_the_user_unplugged_it()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { DisconnectAfterBytesWritten = 30 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.ConnectionProblemSuspected, result.Verdict.Verdict);
        Assert.True(result.Evidence.DeviceRemoved);
        Assert.Equal(0, device.IoAfterDisconnect); // no further I/O once the device is gone (design §5.4)
        Assert.True(result.TestFilesKept);

        var answered = VerdictEngine.Decide(result.Evidence, userSaysUnplugged: true);
        Assert.Equal(Verdict.Interrupted, answered.Verdict);
        Assert.Equal(InterruptReason.UserUnplugged, answered.InterruptReason);
    }

    // ---- Card swaps

    [Fact]
    public async Task Card_swapped_mid_test_is_interrupted()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { SwapCardAfterBytesWritten = 12 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.Interrupted, result.Verdict.Verdict);
        Assert.Equal(InterruptReason.DeviceChanged, result.Verdict.InterruptReason);
        Assert.Equal(2, JournalStore.Load(host.JournalPath).Files.Count); // seen before a third file was created
        Assert.Equal(16 * MiB, device.BytesWritten);
    }

    [Fact]
    public async Task Card_swapped_between_confirmation_and_writing_gets_no_writes()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        var request = Request(device, host); // the user confirms this card…
        device.SwapCard();                   // …and another one is in the reader when the test starts

        var result = await FullTestRunner.RunAsync(device, request, Small);

        Assert.Equal(Verdict.Interrupted, result.Verdict.Verdict);
        Assert.Equal(InterruptReason.DeviceChanged, result.Verdict.InterruptReason);
        Assert.Equal(0, device.BytesWritten);
        Assert.Empty(device.FileNames);
        Assert.Empty(JournalStore.Load(host.JournalPath).Files);
    }

    [Fact]
    public async Task Card_swap_inside_a_file_is_caught_by_the_30_second_recheck()
    {
        // At 256 KiB/s an 8 MiB file takes 32 s, so the timed re-check fires before the file is finished.
        using var host = new HostFolder();
        var clock = new ManualClock();
        var device = new SimulatedDevice(64 * MiB) { Clock = clock, WriteBytesPerSecond = 256 << 10, SwapCardAfterBytesWritten = 1 * MiB };

        var result = await RunAsync(device, host, clock: clock);

        Assert.Equal(InterruptReason.DeviceChanged, result.Verdict.InterruptReason);
        Assert.Single(JournalStore.Load(host.JournalPath).Files);
        Assert.True(device.BytesWritten < 8 * MiB);
    }

    // ---- Drives that lie about their size or alignment, or refuse writes

    [Fact]
    public async Task Volume_running_past_the_end_of_the_disk_cant_be_tested()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB, volumeSize: 128 * MiB);

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.CantTest, result.Verdict.Verdict);
        Assert.Equal(CantTestReason.VolumeLargerThanDisk, result.Verdict.CantTestReason);
        Assert.Equal(0, device.BytesWritten);
    }

    [Fact]
    public async Task Drive_that_under_reports_its_sector_size_still_passes()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { RequiredAlignment = 4096 }; // reports 512-byte sectors
        await Assert.ThrowsAsync<StorageIoException>(() => device.ReadAsync(512, new byte[512], CancellationToken.None).AsTask());

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(0, result.Evidence.EarlyCheckBadSamples);
    }

    [Fact]
    public async Task Short_transfer_is_caught_as_data_errors()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { ShortTransferAt = 10 * MiB };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.DataErrors, result.Verdict.Verdict);
        var error = Assert.Single(result.Evidence.Errors);
        Assert.Equal(TestPhase.Write, error.Phase);
        Assert.Equal(FillEnd.Stopped, result.Evidence.FillEnd);
    }

    [Fact]
    public async Task Write_protected_drive_is_write_protected()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { IsWriteProtected = true };

        var result = await RunAsync(device, host);

        Assert.Equal(Verdict.WriteProtected, result.Verdict.Verdict);
        Assert.Equal(0, device.BytesWritten);
    }

    [Fact]
    public async Task Less_than_256_MiB_free_cant_be_tested()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(200 * MiB);

        var result = await RunAsync(device, host, new FullTestOptions());

        Assert.Equal(Verdict.CantTest, result.Verdict.Verdict);
        Assert.Equal(CantTestReason.NotEnoughFreeSpace, result.Verdict.CantTestReason);
        Assert.Equal(0, device.BytesWritten);
    }

    // ---- Following a test (design §7)

    sealed class Observer : IFullTestObserver
    {
        public List<(TestPhase Phase, long Done, long Total)> Steps { get; } = [];
        public List<(TestPhase Phase, SpeedSlice Slice)> Slices { get; } = [];
        public InterruptReason? StopReason { get; set; }

        public void Progress(TestPhase phase, long done, long total) => Steps.Add((phase, done, total));

        public void Slice(TestPhase phase, SpeedSlice slice) => Slices.Add((phase, slice));
    }

    [Fact]
    public async Task The_observer_sees_every_phase_reach_its_total_and_every_slice()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        var observer = new Observer();

        var result = await FullTestRunner.RunAsync(device, Request(device, host), Small, observer: observer);

        long tested = result.Evidence.VerifiedBytes;
        Assert.Equal((TestPhase.Write, tested, tested), observer.Steps.Last(s => s.Phase == TestPhase.Write));
        Assert.Equal((TestPhase.Verify, tested, tested), observer.Steps.Last(s => s.Phase == TestPhase.Verify));
        Assert.Contains(observer.Steps, s => s.Phase == TestPhase.EarlyCheck && s.Total == Small.EarlyCheckSamples);
        Assert.Equal(result.Speed.WriteSlices, observer.Slices.Where(s => s.Phase == TestPhase.Write).Select(s => s.Slice));
        Assert.Equal(result.Speed.ReadSlices, observer.Slices.Where(s => s.Phase == TestPhase.Verify).Select(s => s.Slice));
    }

    [Fact]
    public async Task A_stop_reason_makes_the_result_interrupted_even_with_errors()
    {
        using var host = new HostFolder();
        // Unplugged during sleep: the errors around the resume aren't counted against the drive (design §5.4).
        var device = new SimulatedDevice(64 * MiB) { DisconnectAfterBytesWritten = 30 * MiB };
        var observer = new Observer { StopReason = InterruptReason.PcSlept };

        var result = await FullTestRunner.RunAsync(device, Request(device, host), Small, observer: observer);

        Assert.True(result.Evidence.DeviceRemoved);
        Assert.Equal(Verdict.Interrupted, result.Verdict.Verdict);
        Assert.Equal(InterruptReason.PcSlept, result.Verdict.InterruptReason);
        Assert.Equal(InterruptReason.PcSlept, JournalStore.Load(host.JournalPath).Result?.Verdict.InterruptReason);
    }
}
