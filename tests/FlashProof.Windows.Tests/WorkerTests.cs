using System.Text;
using System.Threading.Channels;
using FlashProof.Core;
using FlashProof.Tests.Emulator;

namespace FlashProof.Windows.Tests;

// Design §4, §5.4 and §7: the worker's protocol and its safety rules, through its stdin and stdout, with simulated
// drives. Every message the worker writes is parsed back, so each test also checks the JSON.
public sealed class WorkerTests : IAsyncLifetime
{
    const long MiB = 1 << 20;
    readonly Lines input = new();
    readonly Messages output = new();
    readonly FakeDevices devices = new();
    Task run = Task.CompletedTask;

    public Task InitializeAsync()
    {
        run = new Worker(input, output, devices).RunAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        input.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        devices.Dispose();
    }

    StartSpec Spec(string session = "session-1", long? maxTestBytes = null) => devices.Spec(session, maxTestBytes);

    /// <summary>Starts a test on a drive that stops at 20 MiB until it's cancelled, and waits until it's stuck there.</summary>
    async Task StartHangingTestAsync()
    {
        devices.Drive = new SimulatedDevice(64 * MiB) { HangAt = 20 * MiB, HangHonoursCancellation = true };
        input.Send(new StartCommand(Spec()));
        await devices.Drive.HangReached.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Hello_comes_first_then_a_heartbeat_every_second()
    {
        var hello = await output.Next<HelloMessage>();
        await output.Next<HeartbeatMessage>(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkerProtocol.Version, hello.Protocol);
        Assert.IsType<HelloMessage>(output.Seen[0]);
    }

    [Fact]
    public async Task Scan_sends_the_devices_with_their_Inspect_reports()
    {
        var report = Inspection.Report(EligibilityTests.SanDisk(), EligibilityTests.Pc, []);
        devices.ScanResult = new DevicesMessage([new ScannedDevice(report.Disk.Device, null, report, devices.Drive.ReadIdentity(), null)],
            [new ProblemDevice(@"USB\VID_05DC&PID_A838\X", "Lexar", 43)]);

        input.Send(new ScanCommand());
        var message = await output.Next<DevicesMessage>();

        var device = Assert.Single(message.Devices);
        Assert.Equal("USB SanDisk 3.2Gen1 USB Device", device.Device.Name);
        Assert.True(device.Report?.NoProblemsFound);
        Assert.Equal("Ventoy", device.Report?.Disk.Volumes[0].Label);
        Assert.Equal(devices.Drive.ReadIdentity(), device.Identity); // survives the JSON round trip, so the app can send it back
        Assert.Equal(43, Assert.Single(message.ProblemDevices).ProblemCode);
    }

    [Fact]
    public async Task A_full_test_sends_progress_and_speed_samples_then_its_result()
    {
        input.Send(new StartCommand(Spec()));
        var result = await output.Next<ResultMessage>();

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Result.Verdict.Verdict);
        Assert.Equal(Path.Combine(devices.SessionsFolder, "session-1", "journal.json"), result.JournalPath);
        Assert.Equal(JournalStatus.Finished, JournalStore.Load(result.JournalPath).Status);
        long tested = result.Result.Evidence.VerifiedBytes;
        var progress = output.Seen.OfType<ProgressMessage>().ToList();
        Assert.Contains(progress, p => p.Phase == WorkPhase.Write && p.Done == tested && p.Total == tested);
        Assert.Contains(progress, p => p.Phase == WorkPhase.Verify && p.Done == tested && p.Total == tested);
        Assert.Contains(output.Seen.OfType<SampleMessage>(), s => s.Phase == TestPhase.Write);
        Assert.Contains(output.Seen.OfType<SampleMessage>(), s => s.Phase == TestPhase.Verify);
        Assert.True(devices.LifetimeDisposed);
    }

    [Fact]
    public async Task A_test_can_be_limited_for_hardware_testing()
    {
        input.Send(new StartCommand(Spec(maxTestBytes: 16 * MiB)));
        var result = await output.Next<ResultMessage>();

        Assert.Equal(Verdict.PassedCapacityNotChecked, result.Result.Verdict.Verdict);
        Assert.Equal(16 * MiB, result.Result.Evidence.VerifiedBytes);
    }

    public static TheoryData<string> BadSessionIds => new() { "", "..", @"..\x", "a/b", "a b", "C:", "x.y", new string('a', 65) };

    [Theory]
    [MemberData(nameof(BadSessionIds))]
    public async Task Session_IDs_that_could_leave_their_folder_are_refused_before_any_device_is_opened(string id)
    {
        input.Send(new StartCommand(Spec(id)));
        var error = await output.Next<ErrorMessage>();

        Assert.Equal(WorkerErrors.BadSessionId, error.Code);
        Assert.Equal(0, devices.Opened);
    }

    [Fact]
    public async Task An_existing_session_is_never_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(devices.SessionsFolder, "session-1"));

        input.Send(new StartCommand(Spec()));
        var error = await output.Next<ErrorMessage>();

        Assert.Equal(WorkerErrors.SessionExists, error.Code);
        Assert.Equal(0, devices.Opened);
    }

    [Fact]
    public async Task A_drive_the_device_checks_refuse_gets_no_journal_and_no_writes()
    {
        input.Send(new StartCommand(Spec() with { DeviceInstanceId = @"SCSI\DISK&VEN_NVME\0" }));
        var error = await output.Next<ErrorMessage>();

        Assert.Equal(WorkerErrors.NotACandidate, error.Code);
        Assert.False(Directory.Exists(Path.Combine(devices.SessionsFolder, "session-1")));
        Assert.Equal(0, devices.Drive.BytesWritten);
    }

    [Fact]
    public async Task A_card_swapped_between_confirmation_and_start_gets_no_writes()
    {
        var confirmed = Spec();
        devices.Drive.SwapCard();

        input.Send(new StartCommand(confirmed));
        var result = await output.Next<ResultMessage>();

        Assert.Equal(InterruptReason.DeviceChanged, result.Result.Verdict.InterruptReason);
        Assert.Equal(0, devices.Drive.BytesWritten);
        Assert.Empty(devices.Drive.FolderNames);
    }

    [Fact]
    public async Task Only_one_operation_runs_at_a_time()
    {
        await StartHangingTestAsync();

        input.Send(new ScanCommand());
        Assert.Equal(WorkerErrors.Busy, (await output.Next<ErrorMessage>()).Code);
        input.Send(new StartCommand(Spec("session-2")));
        Assert.Equal(WorkerErrors.Busy, (await output.Next<ErrorMessage>()).Code);
        input.Send(new CancelCommand());
        var result = await output.Next<ResultMessage>();

        Assert.Equal(Verdict.Interrupted, result.Result.Verdict.Verdict);
        Assert.Equal(InterruptReason.Cancelled, result.Result.Verdict.InterruptReason);
        Assert.False(Directory.Exists(Path.Combine(devices.SessionsFolder, "session-2")));
    }

    [Fact]
    public async Task Sleep_during_a_test_interrupts_it()
    {
        await StartHangingTestAsync();

        devices.SleepOrWake!();
        var result = await output.Next<ResultMessage>();

        Assert.Equal(InterruptReason.PcSlept, result.Result.Verdict.InterruptReason);
        Assert.True(result.Result.TestFilesKept); // evidence is kept; nothing is resumed
    }

    [Fact]
    public async Task Safely_removing_the_drive_ends_the_test_before_Windows_is_answered()
    {
        await StartHangingTestAsync();

        devices.QueryRemove!(); // what Windows' removal callback runs; the handles are closed right after it returns
        string journal = Path.Combine(devices.SessionsFolder, "session-1", "journal.json");
        Assert.Equal(JournalStatus.Finished, JournalStore.Load(journal).Status);
        var result = await output.Next<ResultMessage>();

        Assert.Equal(InterruptReason.UserUnplugged, result.Result.Verdict.InterruptReason);
    }

    [Fact]
    public async Task When_the_app_goes_away_the_test_stops_and_the_worker_exits()
    {
        await StartHangingTestAsync();

        input.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var journal = JournalStore.Load(Path.Combine(devices.SessionsFolder, "session-1", "journal.json"));
        Assert.Equal(JournalStatus.Finished, journal.Status);
        Assert.Equal(InterruptReason.AppClosed, journal.Result?.Verdict.InterruptReason);
    }

    [Fact]
    public async Task The_PC_is_kept_awake_only_during_a_test()
    {
        await StartHangingTestAsync();
        await devices.WaitForAwakeAsync(true);

        input.Send(new CancelCommand());
        await output.Next<ResultMessage>();
        await devices.WaitForAwakeAsync(false);
    }

    // ---- Unplug and replug before verifying (design §6.2 step 7)

    StartSpec ReplugSpec() => Spec() with { ReplugBeforeVerify = true };

    [Fact]
    public async Task A_replug_test_lets_go_of_the_drive_ejects_it_and_verifies_it_once_it_is_back()
    {
        // The last 7.5 MiB written are only in the drive's RAM: a plain test would pass it.
        devices.Drive = new SimulatedDevice(64 * MiB) { Flash = FlashKind.RamCache, RealCapacity = 56 * MiB, CacheBytes = 8 * MiB };
        input.Send(new StartCommand(ReplugSpec()));
        Assert.Equal(ReplugStep.ReadyToEject, (await output.Next<ReplugMessage>()).Step);
        Assert.True(devices.LifetimeDisposed); // handles closed, removal watch gone: the eject isn't "unplugged"
        long written = devices.Drive.BytesWritten;

        input.Send(new EjectCommand());
        Assert.Equal(ReplugStep.Ejected, (await output.Next<ReplugMessage>()).Step);
        devices.PlugIn();
        Assert.Equal(ReplugStep.Checking, (await output.Next<ReplugMessage>()).Step);
        var result = await output.Next<ResultMessage>();

        Assert.Equal(Verdict.FakeCapacity, result.Result.Verdict.Verdict);
        Assert.True(result.Result.Evidence.Replugged);
        Assert.Equal(written, devices.Drive.BytesWritten); // verification only read
        Assert.Equal(1, devices.Ejects);
    }

    [Fact]
    public async Task Other_drives_plugged_in_while_waiting_are_refused_and_never_written_to()
    {
        input.Send(new StartCommand(ReplugSpec()));
        Assert.Equal(ReplugStep.ReadyToEject, (await output.Next<ReplugMessage>()).Step);
        var tested = devices.Drive;
        devices.Unplug(); // without "Eject now": that works too

        devices.PlugInAnother();
        Assert.Equal(ReplugStep.WrongDrive, (await output.Next<ReplugMessage>()).Step);
        var otherCard = new SimulatedDevice(64 * MiB);
        otherCard.SwapCard();
        devices.PlugIn(otherCard); // another card in the same reader: the same device ID
        Assert.Equal(ReplugStep.Checking, (await output.Next<ReplugMessage>()).Step);
        Assert.Equal(ReplugStep.WrongDrive, (await output.Next<ReplugMessage>()).Step);
        devices.Unplug();
        devices.PlugIn(tested);
        var result = await output.Next<ResultMessage>();

        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Result.Verdict.Verdict);
        Assert.Equal(0, otherCard.BytesWritten);
        Assert.Empty(otherCard.FolderNames);
    }

    [Fact]
    public async Task An_eject_Windows_refuses_says_what_blocked_it_and_can_be_tried_again()
    {
        devices.EjectAnswer = new ReplugMessage(ReplugStep.EjectFailed, "notepad.exe");
        input.Send(new StartCommand(ReplugSpec()));
        await output.Next<ReplugMessage>();

        input.Send(new EjectCommand());
        var refused = await output.Next<ReplugMessage>();
        devices.EjectAnswer = new ReplugMessage(ReplugStep.Ejected);
        input.Send(new EjectCommand());
        var ejected = await output.Next<ReplugMessage>();
        devices.PlugIn();
        var result = await output.Next<ResultMessage>();

        Assert.Equal(new ReplugMessage(ReplugStep.EjectFailed, "notepad.exe"), refused);
        Assert.Equal(ReplugStep.Ejected, ejected.Step);
        Assert.Equal(Verdict.PassedCapacityConfirmed, result.Result.Verdict.Verdict);
    }

    [Fact]
    public async Task Eject_is_refused_unless_a_test_is_waiting_for_a_replug()
    {
        input.Send(new EjectCommand());
        Assert.Equal(WorkerErrors.NothingToEject, (await output.Next<ErrorMessage>()).Code);
        await StartHangingTestAsync();
        input.Send(new EjectCommand());
        Assert.Equal(WorkerErrors.NothingToEject, (await output.Next<ErrorMessage>()).Code);

        Assert.Equal(0, devices.Ejects);
        input.Send(new CancelCommand());
        await output.Next<ResultMessage>();
    }

    [Fact]
    public async Task Stop_while_waiting_for_the_replug_keeps_the_test_files()
    {
        input.Send(new StartCommand(ReplugSpec()));
        await output.Next<ReplugMessage>();

        input.Send(new CancelCommand());
        var result = await output.Next<ResultMessage>();

        Assert.Equal(InterruptReason.Cancelled, result.Result.Verdict.InterruptReason);
        Assert.True(result.Result.TestFilesKept);
        Assert.NotEmpty(devices.Drive.FileNames);
    }

    [Fact]
    public async Task The_read_back_sends_progress_then_its_result()
    {
        input.Send(new ReadFilesCommand(FakeDevices.DriveId, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\"));
        var files = await output.Next<FilesMessage>();

        Assert.Equal(3, files.Result.FilesRead);
        Assert.Contains(output.Seen.OfType<ProgressMessage>(), p => p.Phase == WorkPhase.ReadFiles && p.Done == 300);
    }

    [Fact]
    public async Task The_next_command_is_accepted_as_soon_as_the_last_message_arrives()
    {
        // The app sends its next command when an operation's last message arrives (design §4); the worker must be free by then.
        for (int i = 0; i < 200; i++)
        {
            input.Send(new ScanCommand());
            await output.Next<DevicesMessage>(); // fails the test on a "busy" error
        }
        input.Send(new ReadFilesCommand(FakeDevices.DriveId, FakeDevices.VolumeGuidPath));
        await output.Next<FilesMessage>();
        input.Send(new StartCommand(Spec()));
        await output.Next<ResultMessage>();
        input.Send(new ScanCommand());
        await output.Next<DevicesMessage>();
    }

    [Fact]
    public async Task Cancel_with_nothing_running_is_an_error()
    {
        input.Send(new CancelCommand());

        Assert.Equal(WorkerErrors.NothingToCancel, (await output.Next<ErrorMessage>()).Code);
    }

    [Fact]
    public async Task Bad_lines_are_refused_and_the_worker_carries_on()
    {
        input.SendRaw("not json");
        Assert.Equal(WorkerErrors.BadCommand, (await output.Next<ErrorMessage>()).Code);
        input.SendRaw("""{"type":"format"}""");
        Assert.Equal(WorkerErrors.BadCommand, (await output.Next<ErrorMessage>()).Code);

        input.Send(new ScanCommand());
        await output.Next<DevicesMessage>();
    }

    [Fact]
    public void Commands_parse_from_hand_written_JSON_in_any_property_order()
    {
        Assert.IsType<CancelCommand>(WorkerProtocol.ParseCommand("""{"type":"cancel"}"""));
        var read = Assert.IsType<ReadFilesCommand>(WorkerProtocol.ParseCommand("""{"deviceInstanceId":"USBSTOR\\X","type":"readFiles","volumeGuidPath":"V"}"""));
        Assert.Equal(@"USBSTOR\X", read.DeviceInstanceId);
    }

    /// <summary>The worker's stdin: lines the test sends, until closed.</summary>
    sealed class Lines : TextReader
    {
        readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public void Send(WorkerCommand command) => lines.Writer.TryWrite(WorkerProtocol.Serialize(command));

        public void SendRaw(string line) => lines.Writer.TryWrite(line);

        public override async Task<string?> ReadLineAsync() =>
            await lines.Reader.WaitToReadAsync() && lines.Reader.TryRead(out string? line) ? line : null;

        protected override void Dispose(bool disposing) => lines.Writer.TryComplete();
    }

    /// <summary>The worker's stdout, parsed back into messages.</summary>
    sealed class Messages : TextWriter
    {
        readonly Channel<WorkerMessage> messages = Channel.CreateUnbounded<WorkerMessage>();

        /// <summary>Every message taken so far, in order.</summary>
        public List<WorkerMessage> Seen { get; } = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => messages.Writer.TryWrite(WorkerProtocol.ParseMessage(value!));

        /// <summary>The next message of type T, skipping others. An unexpected error fails the test with its text.</summary>
        public async Task<T> Next<T>(TimeSpan? timeout = null) where T : WorkerMessage
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            while (true)
            {
                var message = await messages.Reader.ReadAsync(cts.Token);
                Seen.Add(message);
                if (message is T wanted)
                    return wanted;
                if (message is ErrorMessage error)
                    Assert.Fail($"The worker sent error {error.Code}: {error.Text}");
            }
        }
    }

}
