using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.Tests.Emulator;
using FlashProof.ViewModels;

namespace FlashProof.Windows.Tests;

// Design §3, §4 and §5.4: the app's coordinator. Scripted workers and a clock that moves only when told to check the
// watchdog and Stop to the second; the real worker over a simulated drive checks whole operations.
public sealed class CoordinatorTests : IAsyncLifetime
{
    const long MiB = 1 << 20;
    readonly TestClock clock = new();
    readonly ManualDispatcher dispatcher = new();
    readonly FakeDevices devices = new();
    readonly List<ScriptedWorker> scripted = [];
    readonly List<InProcessWorker> real = [];
    readonly Coordinator coordinator;
    Func<IWorkerConnection> startWorker;
    int changes;

    public CoordinatorTests()
    {
        startWorker = () =>
        {
            var worker = new ScriptedWorker();
            scripted.Add(worker);
            return worker;
        };
        coordinator = new Coordinator(() => startWorker(), clock, dispatcher.Post);
        coordinator.Changed += () => changes++;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        coordinator.Dispose(); // closes each worker's stdin, which ends it
        await Task.WhenAll(real.Select(w => w.Run)).WaitAsync(TimeSpan.FromSeconds(30));
        devices.Dispose();
    }

    ScriptedWorker Worker => scripted[^1];

    /// <summary>Workers from now on run the real worker loop over <see cref="devices"/>.</summary>
    void UseRealWorker() => startWorker = () =>
    {
        var worker = new InProcessWorker(devices);
        real.Add(worker);
        return worker;
    };

    /// <summary>The scripted worker sends these, and the coordinator handles them.</summary>
    async Task EmitAsync(params WorkerMessage[] messages)
    {
        foreach (var message in messages)
            Worker.Emit(message);
        await dispatcher.RunAsync(messages.Length);
    }

    /// <summary>Time passes with the worker alive but no progress, and the watchdog looks every second.</summary>
    async Task HeartbeatsForAsync(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await EmitAsync(new HeartbeatMessage());
            coordinator.Tick();
        }
    }

    void After(double seconds)
    {
        clock.Advance(TimeSpan.FromSeconds(seconds));
        coordinator.Tick();
    }

    Task UntilIdleAsync() => dispatcher.RunUntilAsync(() => coordinator.State == CoordinatorState.Idle);

    static ResultMessage Result(VerdictResult verdict, TestEvidence? evidence = null) =>
        new(new TestResult { SessionId = "session-1", Verdict = verdict, Evidence = evidence ?? new() }, "journal.json");

    static VerdictResult Interrupted(InterruptReason why) => new(Verdict.Interrupted, CapacityResult.NotChecked, 0, InterruptReason: why);

    static readonly VerdictResult Passed = new(Verdict.PassedCapacityConfirmed, CapacityResult.Confirmed, 0.99);

    [Fact]
    public async Task A_scan_keeps_the_devices_and_returns_to_idle()
    {
        coordinator.Scan();
        Assert.Equal(CoordinatorState.Scanning, coordinator.State);
        Assert.IsType<ScanCommand>(Assert.Single(Worker.Received));

        await EmitAsync(new HelloMessage(WorkerProtocol.Version, "1"), new DevicesMessage([], [new ProblemDevice(@"USB\VID_05DC&PID_A838\X", "Lexar", 43)]));

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(43, Assert.Single(coordinator.Devices!.ProblemDevices).ProblemCode);
    }

    [Fact]
    public async Task Closing_the_window_during_a_test_asks_first()
    {
        var main = new MainViewModel(coordinator, clock, new NoFiles());
        await EmitAsync(new HelloMessage(WorkerProtocol.Version, "1"), new DevicesMessage([], []));
        Assert.True(main.CanClose()); // nothing runs
        var report = Inspection.Report(EligibilityTests.SanDisk(), EligibilityTests.Pc, []);
        main.StartTest(new ScannedDevice(report.Disk.Device, report.Status, report, null, null), devices.Spec());
        var test = Assert.IsType<TestViewModel>(main.Current);
        int closes = 0;
        main.CloseConfirmed += () => closes++;

        Assert.False(main.CanClose());
        Assert.True(test.ConfirmingStop);
        Assert.Equal(Text.CloseConfirm, test.StopQuestion);
        test.StopNoCommand.Execute(null);
        Assert.False(test.ConfirmingStop);

        Assert.False(main.CanClose());
        test.StopYesCommand.Execute(null);
        Assert.Equal(1, closes);
        Assert.True(main.CanClose());
        Assert.DoesNotContain(Worker.Received, c => c is CancelCommand); // closing stops it as "app closed" (design §5.4)
    }

    [Fact]
    public async Task No_progress_for_15_s_is_slow_and_for_60_s_not_responding()
    {
        coordinator.StartTest(devices.Spec());

        await HeartbeatsForAsync(14);
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        await HeartbeatsForAsync(1);
        Assert.Equal(Responsiveness.Slow, coordinator.Responsiveness);
        await HeartbeatsForAsync(44);
        Assert.Equal(Responsiveness.Slow, coordinator.Responsiveness);
        await HeartbeatsForAsync(1);
        Assert.Equal(Responsiveness.NotResponding, coordinator.Responsiveness);
    }

    [Fact]
    public async Task Progress_clears_the_notice()
    {
        coordinator.StartTest(devices.Spec());
        await HeartbeatsForAsync(60);
        Assert.Equal(Responsiveness.NotResponding, coordinator.Responsiveness);

        await EmitAsync(new ProgressMessage(WorkPhase.Write, 4 * MiB, 64 * MiB, 1e6));
        coordinator.Tick();

        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        Assert.Equal(4 * MiB, coordinator.Progress?.Done);
    }

    [Fact]
    public async Task Progress_that_doesnt_move_isnt_progress()
    {
        coordinator.StartTest(devices.Spec());
        var same = new ProgressMessage(WorkPhase.Write, 4 * MiB, 64 * MiB, 0);
        await EmitAsync(same);

        for (int i = 0; i < 15; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await EmitAsync(same);
            coordinator.Tick();
        }

        Assert.Equal(Responsiveness.Slow, coordinator.Responsiveness);
    }

    [Fact]
    public void No_heartbeat_for_10_s_is_not_responding()
    {
        coordinator.StartTest(devices.Spec());

        After(9.5);
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        After(0.5);
        Assert.Equal(Responsiveness.NotResponding, coordinator.Responsiveness);
    }

    [Fact]
    public void Keep_waiting_restarts_the_watchdog()
    {
        coordinator.StartTest(devices.Spec());
        After(10);

        coordinator.KeepWaiting();
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        After(9.5);
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        After(0.5);
        Assert.Equal(Responsiveness.NotResponding, coordinator.Responsiveness);
    }

    [Fact]
    public void The_watchdog_sleeps_while_nothing_runs()
    {
        After(120);

        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Stop_sends_cancel_and_the_result_ends_the_test()
    {
        coordinator.StartTest(devices.Spec());

        coordinator.Stop();
        Assert.Equal(CoordinatorState.Stopping, coordinator.State);
        Assert.IsType<CancelCommand>(Worker.Received[^1]);
        await EmitAsync(Result(Interrupted(InterruptReason.Cancelled)));

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(InterruptReason.Cancelled, coordinator.Outcome?.Verdict.InterruptReason);
        Assert.False(Worker.Killed);
    }

    [Fact]
    public async Task A_worker_that_ignores_Stop_is_killed_after_10_s_and_the_test_is_interrupted()
    {
        coordinator.StartTest(devices.Spec());
        coordinator.Stop();

        After(9.5);
        Assert.False(Worker.Killed);
        After(0.5);
        Assert.True(Worker.Killed);
        await dispatcher.RunAsync(); // the worker exits

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(Verdict.Interrupted, coordinator.Outcome?.Verdict.Verdict);
        Assert.Equal(InterruptReason.Cancelled, coordinator.Outcome?.Verdict.InterruptReason);
        Assert.Null(coordinator.Outcome?.Result);
        Assert.Null(coordinator.Error);
        Assert.False(coordinator.Stuck);
    }

    [Fact]
    public async Task A_worker_still_there_30_s_after_Stop_means_the_drive_is_stuck()
    {
        coordinator.StartTest(devices.Spec());
        Worker.ExitsWhenKilled = false; // stuck in I/O that can't be cancelled
        coordinator.Stop();

        After(29.5);
        Assert.True(Worker.Killed);
        Assert.False(coordinator.Stuck);
        After(0.5);
        Assert.True(coordinator.Stuck);
        Assert.Equal(CoordinatorState.Stopping, coordinator.State);

        Worker.Exit(); // the user unplugged the drive, so the I/O failed and the worker could exit
        await dispatcher.RunAsync();
        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.False(coordinator.Stuck);
    }

    [Fact]
    public async Task A_worker_that_dies_mid_test_leaves_it_unfinished_and_the_next_operation_starts_a_new_one()
    {
        coordinator.StartTest(devices.Spec());

        Worker.Exit();
        await dispatcher.RunAsync();

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(Coordinator.WorkerLost, coordinator.Error?.Code);
        Assert.Equal(InterruptReason.Unfinished, coordinator.Outcome?.Verdict.InterruptReason);
        Assert.True(scripted[0].Disposed);

        coordinator.Scan();
        Assert.Equal(2, scripted.Count);
        Assert.IsType<ScanCommand>(Assert.Single(Worker.Received));
    }

    [Fact]
    public async Task A_refused_start_returns_to_idle_with_the_reason()
    {
        coordinator.StartTest(devices.Spec());

        await EmitAsync(new ErrorMessage(WorkerErrors.NotReady, "The drive can't be tested."));

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(WorkerErrors.NotReady, coordinator.Error?.Code);
        Assert.Null(coordinator.Outcome);
    }

    [Fact]
    public async Task A_test_stopped_before_it_began_is_interrupted()
    {
        coordinator.StartTest(devices.Spec());
        coordinator.Stop();

        await EmitAsync(new ErrorMessage(WorkerErrors.Cancelled, "Stopped before it started."));

        Assert.Equal(InterruptReason.Cancelled, coordinator.Outcome?.Verdict.InterruptReason);
    }

    [Fact]
    public async Task Replies_to_a_command_dont_end_the_operation()
    {
        coordinator.StartTest(devices.Spec());

        await EmitAsync(new ErrorMessage(WorkerErrors.NothingToCancel, "Nothing is running."));

        Assert.Equal(CoordinatorState.Testing, coordinator.State);
        Assert.Null(coordinator.Error);
    }

    [Fact]
    public void Only_one_operation_runs_at_a_time()
    {
        coordinator.Scan();

        Assert.Throws<InvalidOperationException>(() => coordinator.StartTest(devices.Spec()));
        Assert.Single(Worker.Received);
    }

    [Fact]
    public async Task Speed_samples_are_kept_for_the_chart_until_the_next_test()
    {
        coordinator.StartTest(devices.Spec());
        await EmitAsync(new SampleMessage(TestPhase.Write, 0, 4 * MiB, 0.5), Result(Passed));

        Assert.Single(coordinator.Samples);
        coordinator.StartTest(devices.Spec("session-2"));
        Assert.Empty(coordinator.Samples);
        Assert.Null(coordinator.Outcome);
    }

    [Fact]
    public async Task Closing_tells_the_worker_to_stop_and_ignores_what_it_still_sends()
    {
        coordinator.StartTest(devices.Spec());

        coordinator.Dispose();
        Assert.True(Worker.Disposed);
        int before = changes;
        await EmitAsync(Result(Interrupted(InterruptReason.AppClosed)));

        Assert.Equal(before, changes);
        Assert.Null(coordinator.Outcome);
    }

    [Fact]
    public void A_worker_that_cant_start_is_reported()
    {
        startWorker = () => throw new Win32Exception(2);

        coordinator.Scan();

        Assert.Equal(CoordinatorState.Idle, coordinator.State);
        Assert.Equal(Coordinator.WorkerDidNotStart, coordinator.Error?.Code);
    }

    [Fact]
    public void A_drive_that_vanished_mid_test_is_asked_about()
    {
        var evidence = new TestEvidence { DiskSize = 64 * MiB, WrittenBytes = 8 * MiB };
        evidence.Errors.Add(new IoError(TestPhase.Write, 8 * MiB, IoErrorKind.DeviceRemoved, 1167, "The device is not connected."));
        var outcome = TestOutcome.From(Result(VerdictEngine.Decide(evidence), evidence));

        Assert.True(outcome.AskIfUnplugged);
        Assert.Equal(Verdict.ConnectionProblemSuspected, outcome.Verdict.Verdict);
        var yes = outcome.Answer(unplugged: true);
        Assert.Equal(InterruptReason.UserUnplugged, yes.Verdict.InterruptReason);
        Assert.False(yes.AskIfUnplugged);
        Assert.Equal(Verdict.ConnectionProblemSuspected, outcome.Answer(unplugged: false).Verdict.Verdict);
    }

    [Fact]
    public void Safely_remove_isnt_asked_about()
    {
        var outcome = TestOutcome.From(Result(Interrupted(InterruptReason.UserUnplugged)));

        Assert.False(outcome.AskIfUnplugged);
    }

    [Fact]
    public async Task A_full_test_runs_through_the_real_worker_to_its_verdict()
    {
        UseRealWorker();

        coordinator.StartTest(devices.Spec());
        await UntilIdleAsync();

        Assert.Null(coordinator.Error);
        Assert.Equal(Verdict.PassedCapacityConfirmed, coordinator.Outcome?.Verdict.Verdict);
        Assert.Contains(coordinator.Samples, s => s.Phase == TestPhase.Write);
        Assert.Contains(coordinator.Samples, s => s.Phase == TestPhase.Verify);
        Assert.Equal(WorkPhase.Verify, coordinator.Progress?.Phase);
    }

    [Fact]
    public async Task Stop_ends_a_real_test_as_cancelled()
    {
        UseRealWorker();
        devices.Drive = new SimulatedDevice(64 * MiB) { HangAt = 20 * MiB, HangHonoursCancellation = true };
        coordinator.StartTest(devices.Spec());
        await devices.Drive.HangReached.WaitAsync(TimeSpan.FromSeconds(30));

        coordinator.Stop();
        await UntilIdleAsync();

        Assert.Equal(InterruptReason.Cancelled, coordinator.Outcome?.Verdict.InterruptReason);
        Assert.NotNull(coordinator.Outcome?.Result); // the worker's own result, with its evidence
    }

    [Fact]
    public async Task A_drive_pulled_out_mid_test_is_asked_about()
    {
        UseRealWorker();
        devices.Drive = new SimulatedDevice(64 * MiB) { DisconnectAfterBytesWritten = 20 * MiB };

        coordinator.StartTest(devices.Spec());
        await UntilIdleAsync();

        Assert.True(coordinator.Outcome?.AskIfUnplugged);
        Assert.False(coordinator.Outcome?.StayedPluggedIn);
    }

    [Fact]
    public async Task A_drive_that_lost_its_volume_but_stayed_plugged_in_isnt_asked_about()
    {
        // The ADATA on 13 Nov: a retried I/O, then error 1006, and the drive never left Windows' device list.
        UseRealWorker();
        devices.Drive = new SimulatedDevice(64 * MiB) { VolumeLostAfterBytesWritten = 20 * MiB };

        coordinator.StartTest(devices.Spec());
        await UntilIdleAsync();

        Assert.False(coordinator.Outcome?.AskIfUnplugged);
        Assert.True(coordinator.Outcome?.StayedPluggedIn);
        Assert.Equal(Verdict.ConnectionProblemSuspected, coordinator.Outcome?.Verdict.Verdict);
    }

    [Fact]
    public async Task A_drive_that_overwrote_its_own_layout_gets_data_errors_in_the_result_and_the_journal()
    {
        // The ADATA's retest on 13 Nov: the volume was lost while it stayed plugged in, and its partition table was gone.
        UseRealWorker();
        devices.Drive = new SimulatedDevice(64 * MiB) { VolumeLostAfterBytesWritten = 20 * MiB };
        devices.LayoutChangedAnswer = true;

        coordinator.StartTest(devices.Spec());
        await UntilIdleAsync();

        Assert.False(coordinator.Outcome?.AskIfUnplugged);
        Assert.Equal(Verdict.DataErrors, coordinator.Outcome?.Verdict.Verdict);
        Assert.True(coordinator.Outcome?.Result?.Evidence.LayoutChanged);
        var journal = JournalStore.Load(coordinator.Outcome!.JournalPath!);
        Assert.Equal(Verdict.DataErrors, journal.Result?.Verdict.Verdict);
        Assert.True(journal.Result?.Evidence.LayoutChanged);
    }

    [Fact]
    public void A_result_from_a_drive_that_stayed_plugged_in_isnt_asked_about()
    {
        var evidence = new TestEvidence { DiskSize = 64 * MiB, WrittenBytes = 8 * MiB };
        evidence.Errors.Add(new IoError(TestPhase.Write, 8 * MiB, IoErrorKind.DeviceRemoved, 1006, "The volume for a file has been externally altered."));
        var outcome = TestOutcome.From(Result(VerdictEngine.Decide(evidence), evidence) with { StayedPluggedIn = true });

        Assert.False(outcome.AskIfUnplugged);
        Assert.True(outcome.StayedPluggedIn);
        Assert.Equal(Verdict.ConnectionProblemSuspected, outcome.Verdict.Verdict);
    }

    [Fact]
    public async Task Waiting_for_a_replug_isnt_a_stall_but_a_silent_worker_still_is()
    {
        coordinator.StartTest(devices.Spec() with { ReplugBeforeVerify = true });
        await EmitAsync(new ReplugMessage(ReplugStep.ReadyToEject));

        await HeartbeatsForAsync(120); // the user takes their time
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
        Assert.Equal(ReplugStep.ReadyToEject, coordinator.Replug?.Step);
        coordinator.Eject();
        Assert.IsType<EjectCommand>(Worker.Received[^1]);
        await EmitAsync(new ErrorMessage(WorkerErrors.NothingToEject, "The drive is open."));
        Assert.Equal(CoordinatorState.Testing, coordinator.State);
        After(10);
        Assert.Equal(Responsiveness.NotResponding, coordinator.Responsiveness);

        await EmitAsync(new ProgressMessage(WorkPhase.Verify, 4 * MiB, 64 * MiB, 1e6));
        coordinator.Tick();
        Assert.Null(coordinator.Replug);
        Assert.Equal(Responsiveness.Ok, coordinator.Responsiveness);
    }

    [Fact]
    public async Task The_test_screen_walks_the_user_through_the_replug()
    {
        var main = new MainViewModel(coordinator, clock, new NoFiles());
        await EmitAsync(new HelloMessage(WorkerProtocol.Version, "1"), new DevicesMessage([], []));
        var report = Inspection.Report(EligibilityTests.SanDisk(), EligibilityTests.Pc, []);
        main.StartTest(new ScannedDevice(report.Disk.Device, report.Status, report, null, null), devices.Spec() with { ReplugBeforeVerify = true });
        var test = Assert.IsType<TestViewModel>(main.Current);

        await EmitAsync(new ProgressMessage(WorkPhase.Write, 4 * MiB, 64 * MiB, 1e6));
        Assert.Equal("Step 1 of 3: writing test data", test.PhaseText);
        Assert.False(test.IsReplugging);

        await EmitAsync(new ReplugMessage(ReplugStep.ReadyToEject));
        Assert.Equal(Text.ReplugPhase, test.PhaseText);
        Assert.True(test.CanEject);
        test.EjectCommand.Execute(null);
        Assert.IsType<EjectCommand>(Worker.Received[^1]);
        await EmitAsync(new ReplugMessage(ReplugStep.EjectFailed, "notepad.exe"));
        Assert.Contains("notepad.exe is using it", test.ReplugText);
        Assert.True(test.CanEject);
        await EmitAsync(new ReplugMessage(ReplugStep.Ejected));
        Assert.False(test.CanEject);
        Assert.StartsWith("Unplug the drive now", test.ReplugText);
        Assert.True(test.CanStop); // Stop is the way out of the wait

        await EmitAsync(new ProgressMessage(WorkPhase.Verify, 4 * MiB, 64 * MiB, 1e6));
        Assert.False(test.IsReplugging);
        Assert.Equal("Step 3 of 3: reading everything back and checking it", test.PhaseText);
    }

    [Fact]
    public async Task A_replugged_test_runs_through_the_real_worker()
    {
        UseRealWorker();
        coordinator.StartTest(devices.Spec() with { ReplugBeforeVerify = true });
        await dispatcher.RunUntilAsync(() => coordinator.Replug?.Step == ReplugStep.ReadyToEject);

        coordinator.Eject();
        await dispatcher.RunUntilAsync(() => coordinator.Replug?.Step == ReplugStep.Ejected);
        devices.PlugIn();
        await UntilIdleAsync();

        Assert.Null(coordinator.Error);
        Assert.Equal(Verdict.PassedCapacityConfirmed, coordinator.Outcome?.Verdict.Verdict);
        Assert.True(coordinator.Outcome?.Result?.Evidence.Replugged);
        Assert.Null(coordinator.Replug);
    }

    [Fact]
    public async Task Operations_follow_each_other_without_waiting()
    {
        UseRealWorker();

        for (int i = 0; i < 20; i++)
        {
            coordinator.Scan();
            await UntilIdleAsync();
            Assert.Null(coordinator.Error);
        }
        coordinator.ReadFiles(FakeDevices.DriveId, FakeDevices.VolumeGuidPath);
        await UntilIdleAsync();
        coordinator.StartTest(devices.Spec());
        await UntilIdleAsync();

        Assert.Equal(3, coordinator.Files?.FilesRead);
        Assert.Equal(Verdict.PassedCapacityConfirmed, coordinator.Outcome?.Verdict.Verdict);
        Assert.Single(real); // one worker did it all
    }

    /// <summary>A clock that moves only when told to, with no timer: the tests run the watchdog with <see cref="Coordinator.Tick"/>.</summary>
    sealed class TestClock : TimeProvider
    {
        long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>The UI thread's queue: the coordinator's work runs only when the test lets it.</summary>
    sealed class ManualDispatcher
    {
        readonly Channel<Action> queue = Channel.CreateUnbounded<Action>();

        public void Post(Action action) => queue.Writer.TryWrite(action);

        /// <summary>Runs the next actions, waiting for each: one per message the worker sent, or for its exit.</summary>
        public async Task RunAsync(int count = 1)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            for (int i = 0; i < count; i++)
                (await queue.Reader.ReadAsync(cts.Token))();
        }

        public async Task RunUntilAsync(Func<bool> done)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!done())
                (await queue.Reader.ReadAsync(cts.Token))();
        }
    }

    sealed class NoFiles : IUserFiles
    {
        public Task CopyAsync(string text) => Task.CompletedTask;

        public Task<string?> SaveAsync(string suggestedName, string text) => Task.FromResult<string?>(null);
    }

    /// <summary>A worker the test speaks for.</summary>
    sealed class ScriptedWorker : IWorkerConnection
    {
        readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public List<WorkerCommand> Received { get; } = [];
        public bool ExitsWhenKilled { get; set; } = true;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public void Emit(WorkerMessage message) => lines.Writer.TryWrite(WorkerProtocol.Serialize(message));

        public void Exit() => lines.Writer.TryComplete();

        public async Task<string?> ReadLineAsync() => await lines.Reader.WaitToReadAsync() && lines.Reader.TryRead(out string? line) ? line : null;

        public void Send(string line) => Received.Add(WorkerProtocol.ParseCommand(line));

        public void Kill()
        {
            Killed = true;
            if (ExitsWhenKilled)
                Exit();
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The real worker loop, in this process, with lines passed through memory instead of pipes.</summary>
    sealed class InProcessWorker : IWorkerConnection
    {
        readonly Channel<string> input = Channel.CreateUnbounded<string>();
        readonly Channel<string> output = Channel.CreateUnbounded<string>();

        public InProcessWorker(IWorkerDevices devices) =>
            Run = Task.Run(() => new Worker(new LineReader(input.Reader), new LineWriter(output.Writer), devices).RunAsync())
                .ContinueWith(_ => output.Writer.TryComplete(), TaskScheduler.Default);

        /// <summary>The worker loop; it ends once its stdin is closed.</summary>
        public Task Run { get; }

        public async Task<string?> ReadLineAsync() => await output.Reader.WaitToReadAsync() && output.Reader.TryRead(out string? line) ? line : null;

        public void Send(string line) => input.Writer.TryWrite(line);

        /// <summary>As if the process ended; nothing in this process can end the worker's own threads.</summary>
        public void Kill() => output.Writer.TryComplete();

        public void Dispose() => input.Writer.TryComplete();

        sealed class LineReader(ChannelReader<string> lines) : TextReader
        {
            public override async Task<string?> ReadLineAsync() => await lines.WaitToReadAsync() && lines.TryRead(out string? line) ? line : null;
        }

        sealed class LineWriter(ChannelWriter<string> lines) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string? value) => lines.TryWrite(value!);
        }
    }
}
