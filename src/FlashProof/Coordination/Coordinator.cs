using System.ComponentModel;
using System.Text.Json;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.Coordination;

/// <summary>What the app has the worker doing.</summary>
public enum CoordinatorState
{
    Idle,
    Scanning,
    ReadingFiles,
    Testing,

    /// <summary>The user pressed Stop: the worker was asked to cancel, and is killed if it hasn't ended in 10 s (design §5.4).</summary>
    Stopping,
}

/// <summary>The watchdog's view of the running operation (design §5.4).</summary>
public enum Responsiveness
{
    Ok,

    /// <summary>No progress for 15 s: a "Slow" notice.</summary>
    Slow,

    /// <summary>No progress for 60 s, or no heartbeat for 10 s: offer Keep waiting and Stop.</summary>
    NotResponding,
}

/// <summary>
/// The app's side of the worker (design §4, §5.4). It starts the worker, gives it one operation at a time, keeps what
/// the operation reports, and runs the watchdog and Stop. The UI never touches a device; it calls this and reads it.
/// Not thread-safe: call it, and handle <see cref="Changed"/>, on one thread (the UI's). The worker's messages and the
/// watchdog's ticks reach that thread through the dispatch action.
/// </summary>
public sealed class Coordinator : IDisposable
{
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan NotRespondingAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The <see cref="Error"/> code when the worker ended in the middle of an operation.</summary>
    public const string WorkerLost = "workerLost";

    /// <summary>The <see cref="Error"/> code when the worker couldn't be started.</summary>
    public const string WorkerDidNotStart = "workerDidNotStart";

    readonly Func<IWorkerConnection> startWorker;
    readonly TimeProvider clock;
    readonly Action<Action> dispatch;
    readonly ITimer timer;
    readonly List<SampleMessage> samples = [];
    IWorkerConnection? worker;
    CoordinatorState stopping; // the operation being stopped
    long lastProgress, lastHeartbeat, stopAsked;
    (WorkPhase, long)? lastProgressMark;
    bool killed;

    /// <param name="startWorker">Starts a worker; called again after one ends.</param>
    /// <param name="dispatch">Runs an action on the thread that owns the coordinator.</param>
    public Coordinator(Func<IWorkerConnection> startWorker, TimeProvider clock, Action<Action> dispatch)
    {
        this.startWorker = startWorker;
        this.clock = clock;
        this.dispatch = dispatch;
        timer = clock.CreateTimer(_ => dispatch(Tick), null, TickInterval, TickInterval);
    }

    public CoordinatorState State { get; private set; }

    public Responsiveness Responsiveness { get; private set; }

    /// <summary>Stop didn't end the worker within 30 s: the drive is stuck, and the user should unplug it (design §5.4).</summary>
    public bool Stuck { get; private set; }

    /// <summary>The last scan's devices.</summary>
    public DevicesMessage? Devices { get; private set; }

    /// <summary>The running test's or read-back's latest progress.</summary>
    public ProgressMessage? Progress { get; private set; }

    /// <summary>The current or last test's speed slices, for the chart (design §6.4).</summary>
    public IReadOnlyList<SampleMessage> Samples => samples;

    /// <summary>The last read-back's result.</summary>
    public FileReadResult? Files { get; private set; }

    /// <summary>
    /// While the running test waits for "Unplug and replug before verifying" (design §6.2 step 7), where that stands;
    /// null otherwise. The wait is the user's, so it isn't a stall: only the heartbeat is watched meanwhile.
    /// </summary>
    public ReplugMessage? Replug { get; private set; }

    /// <summary>The last test's outcome.</summary>
    public TestOutcome? Outcome { get; private set; }

    /// <summary>Why the last operation failed or was refused; null if it didn't.</summary>
    public ErrorMessage? Error { get; private set; }

    /// <summary>Anything above changed.</summary>
    public event Action? Changed;

    public void Scan() => Begin(CoordinatorState.Scanning, new ScanCommand());

    /// <summary>Inspect's optional read-back of every file on one volume (design §6.3).</summary>
    public void ReadFiles(string deviceInstanceId, string volumeGuidPath) =>
        Begin(CoordinatorState.ReadingFiles, new ReadFilesCommand(deviceInstanceId, volumeGuidPath));

    /// <summary>Starts a full test the user confirmed (design §5.3).</summary>
    public void StartTest(StartSpec spec) => Begin(CoordinatorState.Testing, new StartCommand(spec));

    /// <summary>Stops the running operation: cancel, kill after 10 s, "stuck" after 30 s (design §5.4).</summary>
    public void Stop()
    {
        if (State is CoordinatorState.Idle or CoordinatorState.Stopping)
            return;
        (stopping, State, Responsiveness, stopAsked, killed) = (State, CoordinatorState.Stopping, Responsiveness.Ok, clock.GetTimestamp(), false);
        worker!.Send(WorkerProtocol.Serialize(new CancelCommand()));
        Raise();
    }

    /// <summary>"Eject now": Windows' "Safely remove" for the drive a test waits to have replugged.</summary>
    public void Eject()
    {
        if (State == CoordinatorState.Testing && Replug is not null)
            worker!.Send(WorkerProtocol.Serialize(new EjectCommand()));
    }

    /// <summary>The user chose to keep waiting for an operation that isn't responding.</summary>
    public void KeepWaiting()
    {
        if (Responsiveness != Responsiveness.NotResponding)
            return;
        lastProgress = lastHeartbeat = clock.GetTimestamp();
        Responsiveness = Responsiveness.Ok;
        Raise();
    }

    /// <summary>Applies the watchdog and the Stop deadlines; runs every half second.</summary>
    public void Tick()
    {
        var before = (Responsiveness, Stuck);
        long now = clock.GetTimestamp();
        if (State == CoordinatorState.Stopping)
        {
            var sinceStop = clock.GetElapsedTime(stopAsked, now);
            if (!killed && sinceStop >= KillAfter)
            {
                killed = true;
                worker!.Kill();
            }
            Stuck = sinceStop >= StuckAfter;
        }
        else if (State != CoordinatorState.Idle)
        {
            var noProgress = Replug is null ? clock.GetElapsedTime(lastProgress, now) : TimeSpan.Zero;
            Responsiveness = noProgress >= NotRespondingAfter || clock.GetElapsedTime(lastHeartbeat, now) >= HeartbeatTimeout ? Responsiveness.NotResponding
                : noProgress >= SlowAfter ? Responsiveness.Slow
                : Responsiveness.Ok;
        }
        if ((Responsiveness, Stuck) != before)
            Raise();
    }

    /// <summary>Closes the worker's stdin: it stops any running test as "app closed" and exits (design §5.4).</summary>
    public void Dispose()
    {
        timer.Dispose();
        worker?.Dispose();
        worker = null;
    }

    void Begin(CoordinatorState operation, WorkerCommand command)
    {
        if (State != CoordinatorState.Idle)
            throw new InvalidOperationException($"Another operation is running ({State}).");
        if (worker is null)
        {
            try
            {
                worker = startWorker();
            }
            catch (Exception ex) when (ex is Win32Exception or IOException)
            {
                Error = new ErrorMessage(WorkerDidNotStart, ex.Message);
                Raise();
                return;
            }
            _ = PumpAsync(worker);
        }
        if (operation == CoordinatorState.Testing)
        {
            samples.Clear();
            Outcome = null;
        }
        if (operation == CoordinatorState.ReadingFiles)
            Files = null;
        (State, Responsiveness, Stuck, Error, Progress, lastProgressMark) = (operation, Responsiveness.Ok, false, null, null, null);
        lastProgress = lastHeartbeat = clock.GetTimestamp();
        worker.Send(WorkerProtocol.Serialize(command));
        Raise();
    }

    /// <summary>Reads the worker's messages off the UI thread and hands each to the owning thread.</summary>
    async Task PumpAsync(IWorkerConnection connection)
    {
        try
        {
            while (await connection.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                WorkerMessage message;
                try
                {
                    message = WorkerProtocol.ParseMessage(line);
                }
                catch (JsonException)
                {
                    continue; // not a message; the worker writes nothing else to stdout
                }
                dispatch(() => Receive(connection, message));
            }
        }
        catch (IOException)
        {
            // the pipe broke: the worker is gone
        }
        dispatch(() => Exited(connection));
    }

    void Receive(IWorkerConnection from, WorkerMessage message)
    {
        if (from != worker)
            return; // a worker that was given up on
        long now = clock.GetTimestamp();
        lastHeartbeat = now; // any message shows the worker is alive
        switch (message)
        {
            case ProgressMessage progress:
                if (lastProgressMark != (progress.Phase, progress.Done) || Replug is not null)
                    (lastProgressMark, lastProgress) = ((progress.Phase, progress.Done), now);
                Progress = progress;
                Replug = null; // the drive was accepted, and verifying has started
                break;
            case ReplugMessage replug:
                Replug = replug;
                lastProgress = now;
                break;
            case SampleMessage sample:
                samples.Add(sample);
                lastProgress = now;
                break;
            case DevicesMessage devices:
                Devices = devices;
                End();
                break;
            case FilesMessage files:
                Files = files.Result;
                End();
                break;
            case ResultMessage result:
                Outcome = TestOutcome.From(result);
                End();
                break;
            case ErrorMessage { Code: WorkerErrors.NothingToCancel or WorkerErrors.NothingToEject or WorkerErrors.Busy or WorkerErrors.BadCommand }:
                return; // answers to a command, not the end of the operation
            case ErrorMessage error:
                Error = error;
                if (Running(CoordinatorState.Testing) && error.Code == WorkerErrors.Cancelled)
                    Outcome = TestOutcome.WithoutResult(InterruptReason.Cancelled);
                End();
                break;
            default:
                return; // hello, heartbeat, warning
        }
        Raise();
    }

    void Exited(IWorkerConnection from)
    {
        from.Dispose();
        if (from != worker)
            return;
        worker = null; // the next operation starts a new one
        if (State == CoordinatorState.Idle)
            return;
        if (State == CoordinatorState.Stopping)
        {
            if (stopping == CoordinatorState.Testing)
                Outcome = TestOutcome.WithoutResult(InterruptReason.Cancelled);
        }
        else
        {
            Error = new ErrorMessage(WorkerLost, "The worker stopped unexpectedly.");
            if (State == CoordinatorState.Testing)
                Outcome = TestOutcome.WithoutResult(InterruptReason.Unfinished);
        }
        End();
        Raise();
    }

    /// <summary>The operation is, or was until Stop, this one.</summary>
    bool Running(CoordinatorState operation) => State == operation || (State == CoordinatorState.Stopping && stopping == operation);

    void End() => (State, Responsiveness, Stuck, Replug) = (CoordinatorState.Idle, Responsiveness.Ok, false, null);

    void Raise() => Changed?.Invoke();
}
