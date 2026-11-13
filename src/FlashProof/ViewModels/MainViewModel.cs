using FlashProof.Coordination;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>The window: which screen shows, and the moves between them. The coordinator's changes go to that screen.</summary>
public sealed class MainViewModel : ViewModel
{
    Screen current;
    bool closeConfirmed;

    public MainViewModel(Coordinator coordinator, TimeProvider clock, IUserFiles files)
    {
        Coordinator = coordinator;
        Clock = clock;
        Files = files;
        coordinator.Changed += () => Current.Update();
        current = new HomeViewModel(this);
        coordinator.Scan();
    }

    public Coordinator Coordinator { get; }
    public TimeProvider Clock { get; }
    public IUserFiles Files { get; }

    public Screen Current
    {
        get => current;
        private set => Set(ref current, value);
    }

    /// <summary>The drive list; scans again unless something is running.</summary>
    public void ShowHome()
    {
        Current = new HomeViewModel(this);
        if (Coordinator.State == CoordinatorState.Idle)
            Coordinator.Scan();
    }

    public void ShowInspect(ScannedDevice device) => Current = new InspectViewModel(this, device);

    /// <param name="back">Where Back leads: the screen the user came from.</param>
    public void ShowConfirm(ScannedDevice device, Action back) => Current = new ConfirmViewModel(this, device, back);

    public void StartTest(ScannedDevice device, StartSpec spec)
    {
        Current = new TestViewModel(this, device, spec.ReplugBeforeVerify);
        Coordinator.StartTest(spec);
    }

    public void ShowResult(ScannedDevice device, TestOutcome outcome) => Current = new ResultViewModel(this, device, outcome);

    /// <summary>Raised when the user confirms closing during a test; the window then closes.</summary>
    public event Action? CloseConfirmed;

    /// <summary>
    /// Whether the window may close now. During a test the test screen asks first, since closing stops the test with
    /// no result (design §5.4).
    /// </summary>
    public bool CanClose()
    {
        if (closeConfirmed || Coordinator.State != CoordinatorState.Testing || Current is not TestViewModel test)
            return true;
        test.AskToStop(close: true);
        return false;
    }

    /// <summary>The user confirmed closing during a test.</summary>
    public void Close()
    {
        closeConfirmed = true;
        CloseConfirmed?.Invoke();
    }
}
