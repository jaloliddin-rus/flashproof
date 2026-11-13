using FlashProof.Core;
using FlashProof.Tests.Emulator;
using static FlashProof.Tests.Emulator.Harness;

namespace FlashProof.Tests;

// Design §6.2 step 7 and §5.2: "Unplug and replug before verifying", with simulated drives.
public class ReplugTests
{
    /// <summary>
    /// The platform's side: the tested drive is unplugged, then the given drives are plugged in one after another
    /// (by default the tested one again). With none left, it waits until cancelled.
    /// </summary>
    sealed class Replug : IReplug
    {
        readonly SimulatedDevice tested;
        readonly Queue<SimulatedDevice> next;
        readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SimulatedDevice current;

        public Replug(SimulatedDevice tested, params SimulatedDevice[] arrivals)
        {
            this.tested = current = tested;
            next = new(arrivals.Length > 0 ? arrivals : [tested]);
        }

        public List<ReplugRefusal> Refusals { get; } = [];
        public int Calls { get; private set; }

        /// <summary>Nothing is ever plugged back in.</summary>
        public bool NeverComesBack { get; init; }

        /// <summary>What the tested drive had been sent when it was unplugged.</summary>
        public long WrittenWhenUnplugged { get; private set; } = -1;

        public Task Waiting => waiting.Task;

        public async Task<ITestVolume> NextAsync(CancellationToken ct)
        {
            Calls++;
            if (WrittenWhenUnplugged < 0)
                WrittenWhenUnplugged = tested.BytesWritten;
            current.Unplug();
            if (NeverComesBack || next.Count == 0)
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            current = next.Dequeue();
            current.PlugIn();
            return current;
        }

        public void Refuse(ReplugRefusal reason) => Refusals.Add(reason);
    }

    static Task<TestResult> RunAsync(SimulatedDevice device, HostFolder host, IReplug replug, CancellationToken ct = default) =>
        FullTestRunner.RunAsync(device, Request(device, host), Small, ct: ct, replug: replug);

    [Fact]
    public async Task A_RAM_cache_that_holds_the_end_of_the_test_fools_a_plain_test_but_not_a_replugged_one()
    {
        // 56 MiB of real flash; the 7.5 MiB written beyond it stay in the drive's RAM until it loses power.
        SimulatedDevice Fake() => new(64 * MiB) { Flash = FlashKind.RamCache, RealCapacity = 56 * MiB, CacheBytes = 8 * MiB };
        using var plainHost = new HostFolder();
        using var host = new HostFolder();

        var plain = await Harness.RunAsync(Fake(), plainHost);
        var device = Fake();
        var replugged = await RunAsync(device, host, new Replug(device));

        Assert.Equal(Verdict.PassedCapacityConfirmed, plain.Verdict.Verdict);
        Assert.Equal(Verdict.FakeCapacity, replugged.Verdict.Verdict);
        Assert.Equal(56 * MiB, replugged.Verdict.RealCapacityBytes);
        Assert.True(replugged.Evidence.Replugged);
        Assert.False(plain.Evidence.Replugged);
    }

    [Fact]
    public async Task A_genuine_drive_passes_and_nothing_is_written_after_it_was_unplugged()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        var replug = new Replug(device);

        var result = await RunAsync(device, host, replug);

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(1, replug.Calls);
        Assert.Equal(replug.WrittenWhenUnplugged, device.BytesWritten);
        Assert.Equal(0, device.IoAfterDisconnect); // nothing touched it while it was unplugged
        Assert.Empty(device.FileNames); // a pass still cleans up
        Assert.False(result.TestFilesKept);
        var journal = JournalStore.Load(host.JournalPath);
        Assert.Contains(journal.Checkpoints, c => c.Phase == TestPhase.Replug && c.Bytes == result.Evidence.WrittenBytes);
        Assert.True(journal.IdentityAfterReplug?.LastArrival > journal.Identity.LastArrival);
    }

    [Fact]
    public async Task Another_card_is_refused_and_never_read_as_the_tested_one()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        var otherCard = new SimulatedDevice(64 * MiB);
        otherCard.SwapCard(); // same reader, another card: its volume serial differs
        var replug = new Replug(device, otherCard, device);

        var result = await RunAsync(device, host, replug);

        Assert.Equal([ReplugRefusal.DifferentDrive], replug.Refusals);
        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(0, otherCard.BytesWritten);
        Assert.Empty(otherCard.FolderNames);
    }

    [Fact]
    public async Task A_drive_that_looks_the_same_but_lacks_the_test_files_is_refused()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB);
        var lookalike = new SimulatedDevice(64 * MiB); // the same stable fields, as a cloned card would have
        var replug = new Replug(device, lookalike, device);

        var result = await RunAsync(device, host, replug);

        Assert.Equal([ReplugRefusal.TestFilesNotFound], replug.Refusals);
        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Verdict.Verdict);
        Assert.Equal(0, lookalike.BytesWritten);
    }

    [Fact]
    public async Task Stopping_while_waiting_for_the_drive_is_interrupted_and_keeps_the_test_files()
    {
        using var host = new HostFolder();
        using var cts = new CancellationTokenSource();
        var device = new SimulatedDevice(64 * MiB);
        var replug = new Replug(device) { NeverComesBack = true };

        var run = RunAsync(device, host, replug, cts.Token);
        await replug.Waiting.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var result = await run;

        Assert.Equal(InterruptReason.Cancelled, result.Verdict.InterruptReason);
        Assert.True(result.TestFilesKept);
        Assert.NotEmpty(device.FileNames);
        Assert.False(result.Evidence.Replugged);
        Assert.Equal(JournalStatus.Finished, JournalStore.Load(host.JournalPath).Status);
    }

    [Fact]
    public async Task No_replug_is_asked_for_when_the_early_check_already_found_a_fake()
    {
        using var host = new HostFolder();
        var device = new SimulatedDevice(64 * MiB) { Flash = FlashKind.Wraparound, RealCapacity = 16 * MiB };
        var replug = new Replug(device);

        var result = await RunAsync(device, host, replug);

        Assert.Equal(FillEnd.EarlyFakeCheck, result.Evidence.FillEnd);
        Assert.Equal(Verdict.FakeCapacity, result.Verdict.Verdict);
        Assert.Equal(0, replug.Calls);
        Assert.False(result.Evidence.Replugged);
    }

    static readonly VolumeIdentity Confirmed = new(@"USBSTOR\DISK&VEN_SIM\1", "SIM Flash", "0001", 64 * MiB, 512, 512,
        new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), 1, "MBR:5EED5EED", @"\\?\Volume{5eed0000-0000-0000-0000-000000000001}\", 0x5EED0001);

    public static TheoryData<string, VolumeIdentity, bool> AfterReplug => new()
    {
        { "plugged in later", Confirmed with { LastArrival = Confirmed.LastArrival.AddMinutes(1), MediaChangeCount = 2 }, true },
        { "another port", Confirmed with { DeviceInstanceId = @"USBSTOR\DISK&VEN_SIM\2" }, true },
        { "another volume path", Confirmed with { VolumeGuidPath = @"\\?\Volume{5eed0000-0000-0000-0000-000000000002}\" }, true },
        { "another model", Confirmed with { VendorProduct = "SIM Other" }, false },
        { "another serial", Confirmed with { Serial = "0002" }, false },
        { "another size", Confirmed with { DiskSize = 32 * MiB }, false },
        { "repartitioned", Confirmed with { PartitionTableId = "MBR:12345678" }, false },
        { "another card or reformatted", Confirmed with { VolumeSerial = 0x5EED0002 }, false },
    };

    [Theory]
    [MemberData(nameof(AfterReplug))]
    public void Only_the_stable_fields_must_match_after_a_replug(string change, VolumeIdentity now, bool same)
    {
        Assert.True(Confirmed.SameDriveAs(now) == same, change);
    }
}
