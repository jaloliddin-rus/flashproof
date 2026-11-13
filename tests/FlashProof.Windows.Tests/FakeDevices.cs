using System.Collections.Concurrent;
using FlashProof.Core;
using FlashProof.Tests.Emulator;

namespace FlashProof.Windows.Tests;

/// <summary>The worker's devices without hardware: one simulated drive, and a scan result the test sets.</summary>
internal sealed class FakeDevices : IWorkerDevices, IDisposable
{
    public const string DriveId = @"USBSTOR\DISK&VEN_SIM&PROD_DRIVE\0001";
    public const string VolumeGuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
    public const string OtherDriveId = @"USBSTOR\DISK&VEN_SIM&PROD_OTHER\0002";
    const long MiB = 1 << 20;
    readonly ConcurrentQueue<bool> awake = new();
    readonly Lock sync = new();
    readonly List<PluggedDisk> others = [];
    SimulatedDevice drive = new(64 * MiB);
    bool present = true;
    DateTimeOffset arrival = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    public SimulatedDevice Drive
    {
        get
        {
            lock (sync)
                return drive;
        }
        set
        {
            lock (sync)
                drive = value;
        }
    }

    /// <summary>What Windows answers to the next eject.</summary>
    public ReplugMessage EjectAnswer { get; set; } = new(ReplugStep.Ejected);

    public int Ejects { get; private set; }
    public DevicesMessage ScanResult { get; set; } = new([], []);
    public int Opened { get; private set; }
    public Action? QueryRemove { get; private set; }
    public Action? SleepOrWake { get; private set; }
    public bool LifetimeDisposed { get; private set; }

    public string SessionsFolder { get; } = Path.Combine(Path.GetTempPath(), "FlashProofTests", Guid.NewGuid().ToString("N"));

    public FullTestOptions TestOptions => Harness.Small;

    /// <summary>A start of a full test on the simulated drive, as the user confirmed it now.</summary>
    public StartSpec Spec(string session = "session-1", long? maxTestBytes = null) =>
        new(session, DriveId, VolumeGuidPath, Drive.ReadIdentity(), MaxTestBytes: maxTestBytes);

    public DevicesMessage Scan() => ScanResult;

    public (ITestVolume Volume, IDisposable Lifetime) OpenForTest(StartSpec spec, Action onQueryRemove)
    {
        Opened++;
        if (spec.DeviceInstanceId != DriveId)
            throw new WorkerRefusal(WorkerErrors.NotACandidate, "Not a USB or SD drive.");
        QueryRemove = onQueryRemove;
        return (Drive, new Callback(() => LifetimeDisposed = true));
    }

    public Task<FileReadResult> ReadFilesAsync(ReadFilesCommand command, Action<long, long> progress, CancellationToken ct)
    {
        progress(300, 300);
        return Task.FromResult(new FileReadResult(3, 300, 0, []));
    }

    public IDisposable WatchPower(Action onSleepOrWake)
    {
        SleepOrWake = onSleepOrWake;
        return new Callback(() => { });
    }

    public void KeepAwake(bool on) => awake.Enqueue(on);

    public TimeSpan ReplugPollInterval => TimeSpan.FromMilliseconds(10);

    /// <summary>What <see cref="LayoutChanged"/> answers: whether the drive overwrote its own partition table or volume ID.</summary>
    public bool LayoutChangedAnswer { get; set; }

    public bool LayoutChanged(VolumeIdentity identity) => LayoutChangedAnswer;

    public IReadOnlyList<PluggedDisk> PluggedDisks()
    {
        lock (sync)
            return [.. present && drive.IsConnected ? [new PluggedDisk(DriveId, arrival)] : Array.Empty<PluggedDisk>(), .. others];
    }

    public (ITestVolume Volume, IDisposable Lifetime)? OpenAgain(string deviceInstanceId, Action onQueryRemove)
    {
        lock (sync)
        {
            if (!present || deviceInstanceId != DriveId)
                return null;
            QueryRemove = onQueryRemove;
            return (drive, new Callback(() => { }));
        }
    }

    public ReplugMessage Eject(string deviceInstanceId)
    {
        Ejects++;
        if (EjectAnswer.Step == ReplugStep.Ejected)
            Unplug();
        return EjectAnswer;
    }

    /// <summary>The drive leaves Windows' device list and loses power.</summary>
    public void Unplug()
    {
        lock (sync)
        {
            present = false;
            drive.Unplug();
        }
    }

    /// <summary>A drive arrives where the tested one was: the tested one again, or another card in the same reader.</summary>
    public void PlugIn(SimulatedDevice? another = null)
    {
        lock (sync)
        {
            drive = another ?? drive;
            drive.PlugIn();
            arrival = arrival.AddMinutes(1);
            present = true;
        }
    }

    /// <summary>A different USB drive is plugged in.</summary>
    public void PlugInAnother()
    {
        lock (sync)
            others.Add(new PluggedDisk(OtherDriveId, arrival));
    }

    public async Task WaitForAwakeAsync(bool on)
    {
        for (int i = 0; i < 100 && awake.LastOrDefault() != on; i++)
            await Task.Delay(50);
        Assert.Equal(on, awake.LastOrDefault());
    }

    public void Dispose()
    {
        if (Directory.Exists(SessionsFolder))
            Directory.Delete(SessionsFolder, recursive: true);
    }

    sealed class Callback(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
