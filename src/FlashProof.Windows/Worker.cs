using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>What the worker does to devices and to the PC. <see cref="WindowsDevices"/> is the real one; tests use the emulator.</summary>
public interface IWorkerDevices
{
    /// <summary>The scan (design §4): every candidate inspected with a timeout, and the USB devices that failed.</summary>
    DevicesMessage Scan();

    /// <summary>
    /// Opens the volume for a full test after checking, whatever the app says, that it is the test volume of a Ready
    /// USB or SD drive. Dispose the lifetime only after the test's I/O has ended.
    /// </summary>
    /// <param name="onQueryRemove">Called when Windows asks to remove the drive; it must stop the test and wait for it.</param>
    /// <exception cref="WorkerRefusal">The drive isn't Ready, or the volume isn't its test volume.</exception>
    (ITestVolume Volume, IDisposable Lifetime) OpenForTest(StartSpec spec, Action onQueryRemove);

    /// <summary>Inspect's read-back, only on a volume of a USB or SD drive; progress is (bytes read, bytes in use).</summary>
    Task<FileReadResult> ReadFilesAsync(ReadFilesCommand command, Action<long, long> progress, CancellationToken ct);

    /// <summary>Calls back when the PC goes to sleep or wakes up.</summary>
    IDisposable WatchPower(Action onSleepOrWake);

    /// <summary>Keeps the PC awake, or lets it sleep again. Called only from the heartbeat thread, which lives as long as the worker.</summary>
    void KeepAwake(bool awake);

    /// <summary>Where session journals go: on the PC's own disk, never on the device under test (design §5.4).</summary>
    string SessionsFolder { get; }

    /// <summary>The full test's sizes: the design's on real drives, smaller on the emulator.</summary>
    FullTestOptions TestOptions { get; }

    /// <summary>The USB and SD disks in Windows' device list now, with when each was last plugged in. Opens nothing.</summary>
    IReadOnlyList<PluggedDisk> PluggedDisks();

    /// <summary>
    /// Opens the test volume of a drive plugged back in for "Unplug and replug before verifying" (design §6.2 step 7),
    /// to read its test files; null while Windows hasn't mounted it yet. Dispose the lifetime after the test's I/O has ended.
    /// </summary>
    /// <param name="onQueryRemove">As for <see cref="OpenForTest"/>.</param>
    (ITestVolume Volume, IDisposable Lifetime)? OpenAgain(string deviceInstanceId, Action onQueryRemove);

    /// <summary>Windows' "Safely remove" for the drive, without admin rights: <see cref="ReplugStep.Ejected"/>, or why not.</summary>
    ReplugMessage Eject(string deviceInstanceId);

    /// <summary>How often the device list is read: while waiting for a replug, and after a test lost its drive.</summary>
    TimeSpan ReplugPollInterval { get; }

    /// <summary>
    /// After a test lost the volume of a drive that stayed plugged in: re-reads that drive alone (its partition table,
    /// and its test volume's serial if mounted) and says whether either differs from <paramref name="identity"/>.
    /// False if it can't tell, so the verdict stays Connection problem suspected.
    /// </summary>
    bool LayoutChanged(VolumeIdentity identity);
}

/// <summary>A command the worker refuses; <see cref="Code"/> is one of <see cref="WorkerErrors"/>.</summary>
public sealed class WorkerRefusal(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The worker process (design §4, §7): runs the scan, Inspect's read-back and the full test for the app. It reads
/// commands from stdin and writes messages to stdout, one JSON object per line, and runs one operation at a time, so
/// nothing polls other devices during a test (design §5.4). It ends when stdin closes, which happens when the app dies.
/// </summary>
public sealed partial class Worker(TextReader input, TextWriter output, IWorkerDevices devices)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long stopping waits for the test's I/O to end. The app kills the worker after 10 s (design §5.4).</summary>
    static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    readonly Lock writeLock = new();
    readonly Lock stateLock = new();
    Operation? current;
    bool outputBroken;

    /// <summary>Runs until stdin closes, then stops any running operation as Interrupted (the app closed).</summary>
    public async Task RunAsync()
    {
        Send(new HelloMessage(WorkerProtocol.Version, typeof(Worker).Assembly.GetName().Version?.ToString() ?? "0"));
        using var stopHeartbeat = new ManualResetEventSlim();
        var heartbeat = new Thread(() => Heartbeat(stopHeartbeat)) { IsBackground = true, Name = "FlashProof heartbeat" };
        heartbeat.Start();
        using (devices.WatchPower(() => Current?.Stop(InterruptReason.PcSlept)))
        {
            while (await input.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                    Handle(line);
            }
            if (Current is { } running)
            {
                running.Stop(InterruptReason.AppClosed);
                await Task.WhenAny(running.Completion, Task.Delay(StopWait));
            }
        }
        stopHeartbeat.Set();
        heartbeat.Join();
    }

    Operation? Current
    {
        get
        {
            lock (stateLock)
                return current;
        }
    }

    void Handle(string line)
    {
        WorkerCommand command;
        try
        {
            command = WorkerProtocol.ParseCommand(line);
        }
        catch (JsonException ex)
        {
            Send(new ErrorMessage(WorkerErrors.BadCommand, ex.Message));
            return;
        }
        switch (command)
        {
            case ScanCommand:
                Begin(_ => Task.FromResult<WorkerMessage>(devices.Scan()));
                break;
            case StartCommand start:
                Begin(op => RunTestAsync(start.Spec, op));
                break;
            case ReadFilesCommand read:
                Begin(async op => new FilesMessage(await devices.ReadFilesAsync(read, (done, total) => op.Report(WorkPhase.ReadFiles, done, total), op.Token)));
                break;
            case CancelCommand:
                if (Current is { } op)
                    op.Stop(null);
                else
                    Send(new ErrorMessage(WorkerErrors.NothingToCancel, "Nothing is running."));
                break;
            case EjectCommand:
                // Off the command loop: Windows asks every program using the drive, which can take seconds.
                var replug = Current?.Replug;
                _ = Task.Run(() =>
                {
                    if (replug?.TryEject() != true)
                        Send(new ErrorMessage(WorkerErrors.NothingToEject, "No drive is waiting to be ejected."));
                });
                break;
        }
    }

    /// <summary>
    /// Starts an operation unless one is running. The operation returns its last message, which is sent only once the
    /// worker is free again, so the app can send its next command as soon as that message arrives.
    /// </summary>
    void Begin(Func<Operation, Task<WorkerMessage>> work)
    {
        lock (stateLock)
        {
            if (current is not null)
            {
                Send(new ErrorMessage(WorkerErrors.Busy, "Another operation is running."));
                return;
            }
            var op = current = new Operation(this);
            op.Completion = Task.Run(async () =>
            {
                WorkerMessage last;
                try
                {
                    last = await work(op);
                }
                catch (WorkerRefusal ex)
                {
                    last = new ErrorMessage(ex.Code, ex.Message);
                }
                catch (StorageIoException ex)
                {
                    last = new ErrorMessage(WorkerErrors.IoFailed, ex.Message, ex.NativeCode);
                }
                catch (OperationCanceledException) when (op.Token.IsCancellationRequested)
                {
                    last = new ErrorMessage(WorkerErrors.Cancelled, "Stopped before it started.");
                }
                catch (Exception ex)
                {
                    last = new ErrorMessage(WorkerErrors.Internal, $"{ex.GetType().Name}: {ex.Message}");
                }
                lock (stateLock)
                    current = null;
                Send(last);
            });
        }
    }

    async Task<WorkerMessage> RunTestAsync(StartSpec spec, Operation op)
    {
        if (!SessionIdPattern().IsMatch(spec.SessionId))
            throw new WorkerRefusal(WorkerErrors.BadSessionId, "A session ID has 1 to 64 letters, digits or '-'.");
        string folder = Path.Combine(devices.SessionsFolder, spec.SessionId);
        if (Directory.Exists(folder))
            throw new WorkerRefusal(WorkerErrors.SessionExists, "That session already exists; its journal is never overwritten.");

        void OnQueryRemove()
        {
            op.Stop(InterruptReason.UserUnplugged);
            op.WaitForTest(StopWait);
        }

        var (volume, lifetime) = devices.OpenForTest(spec, OnQueryRemove);
        using var lease = new Lease(lifetime); // released after the test's I/O, and never from the removal callback
        try
        {
            op.Token.ThrowIfCancellationRequested();
            var options = devices.TestOptions;
            if (spec.MaxTestBytes is { } max)
                options = options with { ReserveBytes = Math.Max(options.ReserveBytes, volume.FreeSpace - max) };
            var request = new FullTestRequest(spec.SessionId, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), spec.ConfirmedIdentity,
                Path.Combine(folder, "journal.json"), spec.KeepTestFiles);
            var replug = spec.ReplugBeforeVerify ? new ReplugWait(this, devices, spec, lease, OnQueryRemove) : null;
            op.Replug = replug;
            op.IsTest = true;
            var run = FullTestRunner.RunAsync(volume, request, options, ct: op.Token, observer: op, replug: replug);
            op.TestRun = run;
            var result = await run;
            var identity = LastIdentity(spec, result, request.JournalPath);
            bool stayed = result.Evidence.DeviceRemoved && identity is not null && await StayedPluggedInAsync(spec.DeviceInstanceId, identity);
            if (stayed && devices.LayoutChanged(identity!))
                Decide(result, request.JournalPath);
            return new ResultMessage(result, request.JournalPath, stayed);
        }
        finally
        {
            op.IsTest = false;
            op.Replug = null;
        }
    }

    /// <summary>
    /// Whether a drive the test lost is still in Windows' device list with the plug-in time it had when the test last
    /// opened it, so it never left (error 1006: its file system dropped the volume). Read four times over about three
    /// poll intervals, because a pulled-out drive can leave the list a moment after its last I/O failed.
    /// </summary>
    async Task<bool> StayedPluggedInAsync(string instanceId, VolumeIdentity identity)
    {
        for (int i = 0; i < 4; i++)
        {
            if (i > 0)
                await Task.Delay(devices.ReplugPollInterval);
            if (!devices.PluggedDisks().Any(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase) && d.LastArrival == identity.LastArrival))
                return false;
        }
        return true;
    }

    /// <summary>The identity the test last opened the drive with: the confirmed one, or the one read after a replug.</summary>
    static VolumeIdentity? LastIdentity(StartSpec spec, TestResult result, string journalPath) =>
        result.Evidence.Replugged ? JournalStore.Load(journalPath).IdentityAfterReplug : spec.ConfirmedIdentity;

    /// <summary>
    /// The drive overwrote its own partition table or volume ID while it stayed plugged in: Data errors, not a
    /// connection problem (design §3). The journal is updated to match what the app is told.
    /// </summary>
    static void Decide(TestResult result, string journalPath)
    {
        result.Evidence.LayoutChanged = true;
        result.Verdict = VerdictEngine.Decide(result.Evidence);
        var journal = JournalStore.Load(journalPath);
        journal.Result = result;
        JournalStore.Save(journalPath, journal);
    }

    /// <summary>The handles and removal watch the test holds on its drive; a replug lets go of them and takes new ones.</summary>
    sealed class Lease(IDisposable lifetime) : IDisposable
    {
        IDisposable? current = lifetime;

        public bool Held => current is not null;

        public void Hold(IDisposable next) => current = next;

        public void Release()
        {
            current?.Dispose();
            current = null;
        }

        public void Dispose() => Release();
    }

    /// <summary>
    /// "Unplug and replug before verifying" (design §6.2 step 7), the worker's part. It lets go of the drive, ejects it
    /// when the user asks, and watches Windows' device list until the same device is plugged in again; only then is
    /// anything opened, and only that device. Another drive appearing is reported, never opened. The runner decides
    /// whether what came back is the tested drive.
    /// </summary>
    sealed class ReplugWait(Worker worker, IWorkerDevices devices, StartSpec spec, Lease lease, Action onQueryRemove) : IReplug
    {
        readonly Lock gate = new();
        Dictionary<string, DateTimeOffset?>? seen;

        IWorkerDevices Devices => devices;

        public async Task<ITestVolume> NextAsync(CancellationToken ct)
        {
            bool first;
            lock (gate)
            {
                lease.Release(); // so the removal that follows isn't taken for the user pulling the drive mid-test
                first = seen is null;
                seen ??= Snapshot(Devices.PluggedDisks());
            }
            if (first)
                worker.Send(new ReplugMessage(ReplugStep.ReadyToEject));
            bool back = false, checking = false;
            while (true)
            {
                await Task.Delay(Devices.ReplugPollInterval, ct);
                var now = Devices.PluggedDisks();
                foreach (var disk in now)
                {
                    if (seen.TryGetValue(disk.InstanceId, out var before) && before == disk.LastArrival)
                        continue; // not newly plugged in
                    if (IsTested(disk.InstanceId))
                        back = true;
                    else
                        worker.Send(new ReplugMessage(ReplugStep.WrongDrive));
                }
                back &= now.Any(d => IsTested(d.InstanceId));
                seen = Snapshot(now);
                if (!back)
                    continue;
                if (!checking)
                {
                    worker.Send(new ReplugMessage(ReplugStep.Checking));
                    checking = true;
                }
                lock (gate)
                {
                    if (Devices.OpenAgain(spec.DeviceInstanceId, onQueryRemove) is { } opened)
                    {
                        lease.Hold(opened.Lifetime);
                        return opened.Volume;
                    }
                }
            }
        }

        public void Refuse(ReplugRefusal reason)
        {
            lock (gate)
                lease.Release();
            worker.Send(new ReplugMessage(reason == ReplugRefusal.DifferentDrive ? ReplugStep.WrongDrive : ReplugStep.NotThisTest));
        }

        /// <summary>Ejects the drive; false while the worker has it open (before writing ends, or while checking it).</summary>
        public bool TryEject()
        {
            lock (gate)
            {
                if (seen is null || lease.Held)
                    return false;
                worker.Send(Devices.Eject(spec.DeviceInstanceId));
                return true;
            }
        }

        bool IsTested(string instanceId) => instanceId.Equals(spec.DeviceInstanceId, StringComparison.OrdinalIgnoreCase);

        static Dictionary<string, DateTimeOffset?> Snapshot(IReadOnlyList<PluggedDisk> disks) =>
            disks.DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).ToDictionary(d => d.InstanceId, d => d.LastArrival, StringComparer.OrdinalIgnoreCase);
    }

    void Heartbeat(ManualResetEventSlim stop)
    {
        bool awake = false;
        while (!stop.Wait(HeartbeatInterval))
        {
            Send(new HeartbeatMessage());
            bool testing = Current?.IsTest == true;
            if (testing != awake)
                devices.KeepAwake(awake = testing);
        }
        if (awake)
            devices.KeepAwake(false);
    }

    void Send(WorkerMessage message)
    {
        string line = WorkerProtocol.Serialize(message);
        lock (writeLock)
        {
            if (outputBroken)
                return;
            try
            {
                output.WriteLine(line);
                output.Flush();
            }
            catch (IOException)
            {
                outputBroken = true; // the app is gone; stdin closing ends the worker
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SessionIdPattern();

    /// <summary>A running scan, read-back or test, which follows the test for progress messages.</summary>
    sealed class Operation(Worker worker) : IFullTestObserver
    {
        readonly CancellationTokenSource cts = new();
        readonly Lock stopLock = new();
        InterruptReason? stopReason;
        WorkPhase? lastPhase;
        long lastDone, lastTimestamp;

        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>The full test's own task, once it runs.</summary>
        public Task? TestRun { get; set; }

        /// <summary>A full test is running: the PC is kept awake.</summary>
        public volatile bool IsTest;

        /// <summary>The test's replug, for the eject command; null without one.</summary>
        public volatile ReplugWait? Replug;

        public CancellationToken Token => cts.Token;

        public InterruptReason? StopReason
        {
            get
            {
                lock (stopLock)
                    return stopReason;
            }
        }

        /// <summary>Cancels the operation; the first reason given is kept. Null is a plain cancel.</summary>
        public void Stop(InterruptReason? reason)
        {
            lock (stopLock)
                stopReason ??= reason;
            cts.Cancel();
        }

        public void WaitForTest(TimeSpan timeout)
        {
            try
            {
                TestRun?.Wait(timeout);
            }
            catch (AggregateException)
            {
                // it ended; how is reported by the operation itself
            }
        }

        public void Progress(TestPhase phase, long done, long total) =>
            Report(phase switch { TestPhase.Write => WorkPhase.Write, TestPhase.EarlyCheck => WorkPhase.EarlyCheck, _ => WorkPhase.Verify }, done, total);

        public void Slice(TestPhase phase, SpeedSlice slice) => worker.Send(new SampleMessage(phase, slice.Offset, slice.Bytes, slice.Seconds));

        /// <summary>At most one progress message per half second for a phase, except its first and when it's done.</summary>
        public void Report(WorkPhase phase, long done, long total)
        {
            long now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(lastTimestamp, now);
            bool samePhase = phase == lastPhase;
            if (samePhase && done != total && elapsed < ProgressInterval)
                return;
            double? speed = samePhase && phase != WorkPhase.EarlyCheck && elapsed > TimeSpan.Zero ? (done - lastDone) / elapsed.TotalSeconds : null;
            (lastPhase, lastDone, lastTimestamp) = (phase, done, now);
            worker.Send(new ProgressMessage(phase, done, total, speed));
        }
    }
}

/// <summary>The worker's real devices: the Windows layer, which opens only USB and SD disks.</summary>
public sealed class WindowsDevices : IWorkerDevices
{
    /// <summary>A healthy drive answers in milliseconds; a dead one can hang indefinitely (design §4).</summary>
    public static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(10);

    public string SessionsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashProof", "sessions");

    public FullTestOptions TestOptions { get; } = new();

    public DevicesMessage Scan()
    {
        var system = SystemFacts.Read();
        var ejectedTests = DiskEvents.ReadEjectedTests(SessionsFolder);
        long start = Stopwatch.GetTimestamp();
        // Each on a thread of its own, so a hung drive holds up neither the others nor the thread pool.
        var inspections = DeviceScan.FindCandidates()
            .Select(c => (Candidate: c, Task: Task.Factory.StartNew(() => Read(c, system), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)))
            .ToList();
        var scanned = new List<ScannedDevice>();
        foreach (var (candidate, task) in inspections)
        {
            var left = InspectTimeout - Stopwatch.GetElapsedTime(start);
            scanned.Add(Scanned(candidate, task, left > TimeSpan.Zero ? left : TimeSpan.Zero, system, ejectedTests));
        }
        return new DevicesMessage(scanned, DeviceScan.FindProblemDevices());
    }

    /// <summary>The disk, and for a Ready one its test volume's identity, which the user confirms (design §5.2).</summary>
    static (DiskInfo Disk, VolumeIdentity? Identity) Read(DiskCandidate candidate, SystemFacts system)
    {
        var disk = DeviceScan.Inspect(candidate);
        if (Eligibility.Check(disk, system) is not null || Eligibility.TestVolume(disk) is not { } testVolume)
            return (disk, null);
        using var volume = WindowsTestVolume.Open(testVolume.GuidPath, candidate.InstanceId);
        return (disk, volume.ReadIdentity());
    }

    static ScannedDevice Scanned(DiskCandidate candidate, Task<(DiskInfo Disk, VolumeIdentity? Identity)> inspect, TimeSpan wait, SystemFacts system,
        IReadOnlyList<EjectedTest> ejectedTests)
    {
        try
        {
            if (!inspect.Wait(wait))
                return NotWorking(candidate, $"The drive didn't answer within {InspectTimeout.TotalSeconds:F0} s.");
        }
        catch (AggregateException ex)
        {
            // Whatever a failing drive makes the scan throw, the drive is what isn't working.
            var inner = ex.InnerException ?? ex;
            return NotWorking(candidate, inner is StorageIoException ? inner.Message : $"{inner.GetType().Name}: {inner.Message}");
        }
        var (disk, identity) = inspect.Result;
        var report = Inspection.Run(disk, system, ejectedTests);
        return new ScannedDevice(candidate, report.Status, report, identity, null);
    }

    static ScannedDevice NotWorking(DiskCandidate candidate, string why) =>
        new(candidate, new VerdictResult(Verdict.DetectedButNotWorking, CapacityResult.NotChecked, 0), null, null, why);

    public (ITestVolume Volume, IDisposable Lifetime) OpenForTest(StartSpec spec, Action onQueryRemove)
    {
        var disk = CandidateDisk(spec.DeviceInstanceId);
        if (Eligibility.Check(disk, SystemFacts.Read()) is { } verdict)
            throw new WorkerRefusal(WorkerErrors.NotReady, $"The drive can't be tested: {verdict.Verdict} {verdict.CantTestReason}".TrimEnd());
        if (!string.Equals(Eligibility.TestVolume(disk)?.GuidPath, spec.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
            throw new WorkerRefusal(WorkerErrors.NotTestVolume, "That volume isn't the drive's test volume.");

        var volume = WindowsTestVolume.Open(spec.VolumeGuidPath, spec.DeviceInstanceId);
        try
        {
            var watch = new RemovalWatch(volume.Handles, onQueryRemove, volume.Dispose);
            return (volume, new Lifetime(watch, volume));
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    public Task<FileReadResult> ReadFilesAsync(ReadFilesCommand command, Action<long, long> progress, CancellationToken ct)
    {
        var disk = CandidateDisk(command.DeviceInstanceId);
        var volume = disk.Volumes.FirstOrDefault(v => v.GuidPath.Equals(command.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkerRefusal(WorkerErrors.NotOnDevice, "That volume isn't on that drive.");
        long inUse = volume.Length - volume.FreeBytes;
        return Inspection.ReadAllFilesAsync(volume.GuidPath, done => progress(done, inUse), ct);
    }

    public IDisposable WatchPower(Action onSleepOrWake) => new PowerWatch(onSleepOrWake);

    public void KeepAwake(bool awake) =>
        Native.SetThreadExecutionState(awake ? Native.EsContinuous | Native.EsSystemRequired : Native.EsContinuous);

    public TimeSpan ReplugPollInterval { get; } = TimeSpan.FromSeconds(1);

    public bool LayoutChanged(VolumeIdentity identity)
    {
        // Only the tested drive is opened, on a thread of its own and with the scan's time limit: a failing drive can hang.
        var read = Task.Factory.StartNew(
            () => DeviceScan.FindCandidates().FirstOrDefault(c => c.InstanceId.Equals(identity.DeviceInstanceId, StringComparison.OrdinalIgnoreCase)) is { } c
                ? DeviceScan.Inspect(c) : null,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (!read.Wait(InspectTimeout) || read.Result is not { MediaPresent: true } disk)
                return false;
            if (disk.PartitionTableId != identity.PartitionTableId)
                return true;
            var volume = disk.Volumes.FirstOrDefault(v => v.GuidPath.Equals(identity.VolumeGuidPath, StringComparison.OrdinalIgnoreCase));
            return volume is { Error: 0 } && volume.Serial != identity.VolumeSerial;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    public IReadOnlyList<PluggedDisk> PluggedDisks() =>
        [.. DeviceScan.FindCandidates().Select(c => new PluggedDisk(c.InstanceId,
            Pnp.Locate(c.InstanceId) is { } node ? Pnp.FileTimeProperty(node, Native.DeviceLastArrivalDate) : null))];

    public (ITestVolume Volume, IDisposable Lifetime)? OpenAgain(string deviceInstanceId, Action onQueryRemove)
    {
        // Only the tested device, and only once it's back as a USB or SD disk. It isn't checked for writing (it may now
        // be full or read-only): nothing is written to it, and the runner checks it's the same drive before reading.
        VolumeInfo? testVolume;
        try
        {
            var disk = CandidateDisk(deviceInstanceId);
            testVolume = disk.MediaPresent ? Eligibility.TestVolume(disk) : null;
        }
        catch (Exception ex) when (ex is WorkerRefusal or StorageIoException)
        {
            return null; // not back, or still starting up
        }
        if (testVolume is not { FileSystem.Length: > 0, BitLocker: false, DiskExtents: 1 })
            return null; // Windows hasn't mounted it yet

        WindowsTestVolume volume;
        try
        {
            volume = WindowsTestVolume.Open(testVolume.GuidPath, deviceInstanceId);
        }
        catch (StorageIoException)
        {
            return null;
        }
        try
        {
            var watch = new RemovalWatch(volume.Handles, onQueryRemove, volume.Dispose);
            return (volume, new Lifetime(watch, volume));
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    public ReplugMessage Eject(string deviceInstanceId) => Ejection.Eject(deviceInstanceId);

    /// <summary>The USB or SD disk with this instance ID, read now. Nothing that isn't a candidate is ever opened.</summary>
    static DiskInfo CandidateDisk(string instanceId)
    {
        var candidate = DeviceScan.FindCandidates().FirstOrDefault(c => c.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkerRefusal(WorkerErrors.NotACandidate, "That device isn't a USB or SD drive that's plugged in.");
        return DeviceScan.Inspect(candidate);
    }

    /// <summary>Unregisters the removal watch first, then closes the volume.</summary>
    sealed class Lifetime(RemovalWatch watch, WindowsTestVolume volume) : IDisposable
    {
        public void Dispose()
        {
            watch.Dispose();
            volume.Dispose();
        }
    }
}
