using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>A full test's result: both verdicts with design §3's wording, and what they rest on (design §3, §6.4), and export (§9).</summary>
public sealed class ResultViewModel : Screen
{
    readonly MainViewModel main;
    readonly ScannedDevice device;
    TestOutcome outcome;
    bool includeSerials;
    string? exportMessage;

    public ResultViewModel(MainViewModel main, ScannedDevice device, TestOutcome outcome)
    {
        this.main = main;
        this.device = device;
        this.outcome = outcome;
        Name = DeviceCard.DisplayName(device);
        YesUnpluggedCommand = new Command(() => Answer(true));
        NoUnpluggedCommand = new Command(() => Answer(false));
        BackCommand = new Command(main.ShowHome);
        CopyCommand = new Command(async () => await CopyAsync());
        SaveCommand = new Command(async () => await SaveAsync());
    }

    public string Name { get; }
    public Command YesUnpluggedCommand { get; }
    public Command NoUnpluggedCommand { get; }
    public Command BackCommand { get; }
    public Command CopyCommand { get; }
    public Command SaveCommand { get; }

    VerdictResult Verdict => outcome.Verdict;
    TestEvidence Evidence => outcome.Result?.Evidence ?? new TestEvidence();
    SpeedSummary Speed => outcome.Result?.Speed ?? new SpeedSummary();

    /// <summary>"Did you unplug it?" comes before the verdict (design §3).</summary>
    public bool AskIfUnplugged => outcome.AskIfUnplugged;

    public bool ShowVerdict => !AskIfUnplugged;

    public string Title => Text.Title(Verdict.Verdict);

    /// <summary>The title with a symbol, so the result is never told by color alone.</summary>
    public string Heading => $"{(IsGood ? "✓" : IsBad ? "✗" : "!")} {Title}";

    public string Explanation => Text.Explanation(Verdict, Evidence, outcome.StayedPluggedIn);

    /// <summary>The two passes look good; a fake, lost data or a drive that doesn't store data looks bad; the rest is neither.</summary>
    public bool IsGood => Verdict.Verdict is Core.Verdict.PassedCapacityConfirmed or Core.Verdict.PassedCapacityNotChecked;

    public bool IsBad => Verdict.Verdict is Core.Verdict.FakeCapacity or Core.Verdict.NotStoringData or Core.Verdict.DataErrors;

    /// <summary>Only a test that ran has numbers to show.</summary>
    public bool HasDetails => ShowVerdict && outcome.Result is not null;

    public string Storage => Text.StorageResult(Verdict, Evidence);
    public string Capacity => Text.CapacityResult(Verdict, Evidence);
    public string Size => Text.BothUnits(outcome.Result?.Evidence.DiskSize ?? device.Identity?.DiskSize ?? 0);
    public string Tested => Text.Tested(Evidence, Verdict.Coverage);
    public string WriteSpeeds => Text.WriteSpeeds(Speed);
    public string ReadSpeeds => Text.ReadSpeeds(Speed);
    public string? Usb2Note => Text.Usb2Note(Speed);
    public string Took => outcome.Result is { } r ? Text.TookTime(r) : "";
    public string? Replugged => Evidence.Replugged ? Text.Replugged : null;
    public string? FilesKept => outcome.Result is { } r ? Text.FilesKeptOrNull(r) : null;
    public IReadOnlyList<SpeedSlice> WriteSlices => Speed.WriteSlices;
    public IReadOnlyList<SpeedSlice> ReadSlices => Speed.ReadSlices;
    public long ChartBytes => Evidence.PlannedBytes;
    public bool HasChart => WriteSlices.Count > 0 || ReadSlices.Count > 0;

    public bool IncludeSerials
    {
        get => includeSerials;
        set => Set(ref includeSerials, value);
    }

    public string? ExportMessage
    {
        get => exportMessage;
        private set => Set(ref exportMessage, value);
    }

    public string ReportText() => Report.Build(Name, device, outcome, includeSerials, main.Clock.GetUtcNow());

    void Answer(bool unplugged)
    {
        outcome = outcome.Answer(unplugged);
        Raise(string.Empty);
    }

    async Task CopyAsync()
    {
        await main.Files.CopyAsync(ReportText());
        ExportMessage = Text.Copied;
    }

    async Task SaveAsync()
    {
        try
        {
            if (await main.Files.SaveAsync(Text.ReportFileName, ReportText()) is { } saved)
                ExportMessage = Text.Saved(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportMessage = Text.SaveFailed(ex.Message);
        }
    }
}
