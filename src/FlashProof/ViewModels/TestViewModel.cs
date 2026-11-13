using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>A running full test: progress, the time estimate (design §6.5), the speed chart, the watchdog and Stop (design §5.4).</summary>
public sealed class TestViewModel : Screen
{
    readonly MainViewModel main;
    readonly ScannedDevice device;
    readonly TimeEstimate estimate;
    readonly bool replug;
    ProgressMessage? lastProgress, bytesProgress;
    bool confirmingStop, closing;
    string? error;

    /// <param name="replug">The test unplugs and replugs the drive before verifying (design §6.2 step 7).</param>
    public TestViewModel(MainViewModel main, ScannedDevice device, bool replug = false)
    {
        this.main = main;
        this.device = device;
        this.replug = replug;
        estimate = new TimeEstimate(main.Clock);
        EjectCommand = new Command(main.Coordinator.Eject);
        Name = DeviceCard.DisplayName(device);
        StopCommand = new Command(() => AskToStop(close: false));
        StopYesCommand = new Command(() =>
        {
            ConfirmingStop = false;
            if (closing)
                main.Close();
            else
                main.Coordinator.Stop();
        });
        StopNoCommand = new Command(() => ConfirmingStop = false);
        StopNowCommand = new Command(main.Coordinator.Stop);
        KeepWaitingCommand = new Command(main.Coordinator.KeepWaiting);
        BackCommand = new Command(main.ShowHome);
    }

    Coordinator Coordinator => main.Coordinator;

    public string Name { get; }
    public string Title => Text.Testing(Name);
    public Command StopCommand { get; }
    public Command StopYesCommand { get; }
    public Command StopNoCommand { get; }
    public Command StopNowCommand { get; }
    public Command KeepWaitingCommand { get; }
    public Command BackCommand { get; }
    public Command EjectCommand { get; }

    bool Testing => Coordinator.State == CoordinatorState.Testing;

    public bool IsRunning => Coordinator.State is CoordinatorState.Testing or CoordinatorState.Stopping;

    public string PhaseText => IsReplugging ? Text.ReplugPhase : lastProgress is { } p ? Text.Phase(p.Phase, replug) : Text.Starting;

    /// <summary>Waiting for the user to unplug the drive and plug it back in.</summary>
    public bool IsReplugging => Testing && Coordinator.Replug is not null;

    public string ReplugText => Coordinator.Replug is { } r ? Text.Replug(r, Name) : "";

    public bool CanEject => IsReplugging && Coordinator.Replug?.Step is ReplugStep.ReadyToEject or ReplugStep.EjectFailed;

    public double Fraction => bytesProgress is { Total: > 0 } p ? (double)p.Done / p.Total : 0;

    public string DoneText => bytesProgress is { } p ? Text.Done(p.Done, p.Total) : "";

    public string SpeedText => (bytesProgress?.Phase == WorkPhase.Verify ? estimate.ReadSpeed : estimate.WriteSpeed) is { } speed ? Text.CurrentSpeed(speed) : "";

    public string TimeLeftText => Coordinator.Responsiveness != Responsiveness.Ok ? Text.Stalled
        : estimate.Left is not { } left ? Text.Estimating
        : left.Fastest == left.Slowest ? Text.TimeLeft(left.Fastest)
        : Text.TimeLeftRange(left.Fastest, left.Slowest);

    public bool IsSlow => Testing && Coordinator.Responsiveness == Responsiveness.Slow;

    public bool IsNotResponding => Testing && Coordinator.Responsiveness == Responsiveness.NotResponding;

    public bool IsStopping => Coordinator.State == CoordinatorState.Stopping && !Coordinator.Stuck;

    public bool IsStuck => Coordinator.Stuck;

    public string StuckText => Text.Stuck(Name);

    public bool CanStop => Testing && !confirmingStop && !IsNotResponding;

    /// <summary>Asks before stopping; <paramref name="close"/> when the user is closing the window, which also stops the test (design §5.4).</summary>
    public void AskToStop(bool close)
    {
        closing = close;
        ConfirmingStop = true;
    }

    public string StopQuestion => closing ? Text.CloseConfirm : Text.StopConfirm;

    public string StopYesText => closing ? Text.CloseYes : Text.StopYes;

    public bool ConfirmingStop
    {
        get => confirmingStop && Testing;
        private set
        {
            confirmingStop = value;
            Raise(string.Empty);
        }
    }

    /// <summary>Why the test couldn't start, if it didn't.</summary>
    public string? Error => error;

    public IReadOnlyList<SpeedSlice> WriteSlices => Slices(TestPhase.Write);

    public IReadOnlyList<SpeedSlice> ReadSlices => Slices(TestPhase.Verify);

    /// <summary>The chart's width in bytes: what the test plans to write.</summary>
    public long ChartBytes { get; private set; }

    List<SpeedSlice> Slices(TestPhase phase) =>
        [.. Coordinator.Samples.Where(s => s.Phase == phase).Select(s => new SpeedSlice(s.Offset, s.Bytes, s.Seconds))];

    public override void Update()
    {
        if (Coordinator.State == CoordinatorState.Idle && Coordinator.Outcome is { } outcome)
        {
            main.ShowResult(device, outcome);
            return;
        }
        if (Coordinator.Progress is { } p && !ReferenceEquals(p, lastProgress))
        {
            estimate.Add(p);
            lastProgress = p;
            if (p.Phase != WorkPhase.EarlyCheck) // the early check counts records, not bytes
                bytesProgress = p;
            if (p.Phase == WorkPhase.Write)
                ChartBytes = p.Total;
        }
        if (Coordinator.State == CoordinatorState.Idle && Coordinator.Error is { } e)
            error = Text.TestRefused(e.Text);
        base.Update();
    }
}
