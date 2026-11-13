using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>Inspect (design §6.3): the scan's report for one drive, and the optional read-back of every file.</summary>
public sealed class InspectViewModel : Screen
{
    readonly MainViewModel main;
    readonly ScannedDevice device;
    readonly InspectReport report;
    readonly List<(VolumeInfo Volume, FileReadResult Files)> readBacks = [];
    VolumeRow? reading;
    FileReadResult? files;
    string? readMessage, exportMessage;
    bool includeSerials;

    public InspectViewModel(MainViewModel main, ScannedDevice device)
    {
        this.main = main;
        this.device = device;
        report = device.Report ?? throw new ArgumentException("Inspect needs the drive's report.", nameof(device));
        Name = DeviceCard.DisplayName(device);
        (Status, StatusAdvice) = Text.DeviceStatus(device.Status);
        Volumes = [.. report.Disk.Volumes.Select(v => new VolumeRow(v, () => Read(v)))];
        Events = EventRows(report.Events ?? []);
        BackCommand = new Command(main.ShowHome);
        TestCommand = new Command(() => main.ShowConfirm(device, () => main.ShowInspect(device)));
        StopCommand = new Command(main.Coordinator.Stop);
        CopyCommand = new Command(async () => await CopyAsync());
        SaveCommand = new Command(async () => await SaveAsync());
    }

    public Command CopyCommand { get; }
    public Command SaveCommand { get; }

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

    public string ReportText() => Report.BuildInspect(Name, device, readBacks, includeSerials, main.Clock.GetUtcNow());

    async Task CopyAsync()
    {
        await main.Files.CopyAsync(ReportText());
        ExportMessage = Text.Copied;
    }

    async Task SaveAsync()
    {
        try
        {
            if (await main.Files.SaveAsync(Text.InspectReportFileName, ReportText()) is { } saved)
                ExportMessage = Text.Saved(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportMessage = Text.SaveFailed(ex.Message);
        }
    }

    public string Name { get; }
    public string Status { get; }
    public string StatusAdvice { get; }
    public bool MediaPresent => report.Disk.MediaPresent;
    public string Capacity => Text.BothUnits(report.Disk.Size);
    public string WriteProtection => Text.WriteProtection(report.Disk, report.Status);
    public IReadOnlyList<VolumeRow> Volumes { get; }
    public IReadOnlyList<EventRow> Events { get; }
    public bool EventsRead => report.Events is not null;
    public bool NoEvents => report.Events is { Count: 0 };

    public Command BackCommand { get; }
    public Command TestCommand { get; }
    public Command StopCommand { get; }

    /// <summary>"No problems found in these checks" (never a health pass), or what was found.</summary>
    public bool NoProblemsFound => NoProblems(report, ReadResults);

    public IReadOnlyList<string> Problems => ProblemLines(report, ReadResults);

    List<FileReadResult> ReadResults => [.. readBacks.Select(r => r.Files)];

    /// <param name="files">The read-back of each volume that was read.</param>
    public static bool NoProblems(InspectReport report, IReadOnlyList<FileReadResult> files) =>
        report.NoProblemsFound && !files.Any(f => f.Unreadable.Count > 0);

    /// <param name="files">The read-back of each volume that was read.</param>
    public static IReadOnlyList<string> ProblemLines(InspectReport report, IReadOnlyList<FileReadResult> files)
    {
        var problems = report.Problems.Select(p => p == InspectProblem.RecentDiskErrors && OnlyUncertainErrors(report.Events)
            ? Text.RecentDiskErrorsUncertain : Text.Problem(p)).ToList();
        if (files.Any(f => f.Unreadable.Count > 0) && !report.Problems.Contains(InspectProblem.UnreadableFiles))
            problems.Add(Text.Problem(InspectProblem.UnreadableFiles));
        if (!report.Disk.MediaPresent)
            problems.Add(Text.NoMedia);
        if (report.Events is null)
            problems.Add(Text.EventLogUnreadable);
        return problems;
    }

    /// <summary>Every counted disk error may have been another drive's: none was logged since this drive was plugged in.</summary>
    static bool OnlyUncertainErrors(IReadOnlyList<DiskEvent>? events) =>
        events?.Where(e => e.IsError).All(e => e.Match != DiskEventMatch.ThisPlugIn) == true;

    /// <summary>Design §2: a drive that holds files should be emptied first, or its capacity won't be checked.</summary>
    public bool ShowHoldsFilesAdvice => CanTest && Eligibility.TestVolume(report.Disk) is { } v
        && (double)new FullTestOptions().PlannedBytes(v.FreeBytes) / report.Disk.Size < VerdictEngine.CapacityCoverage;

    public bool CanTest => device.Status is null && device.Identity is not null && main.Coordinator.State == CoordinatorState.Idle;

    public bool IsReading => reading is not null;

    public bool CanRead => MediaPresent && main.Coordinator.State == CoordinatorState.Idle;

    public double ReadFraction => main.Coordinator.Progress is { Phase: WorkPhase.ReadFiles, Total: > 0 } p ? (double)p.Done / p.Total : 0;

    public string ReadProgress => main.Coordinator.Progress is { Phase: WorkPhase.ReadFiles } p ? Text.ReadProgress(p.Done, p.Total) : Text.Starting;

    public string? ReadMessage => readMessage;

    public IReadOnlyList<string> UnreadableFiles => files is null ? [] : [.. files.Unreadable.Select(Text.Unreadable)];

    void Read(VolumeInfo volume)
    {
        reading = Volumes.First(v => v.Volume == volume);
        (files, readMessage) = (null, null);
        main.Coordinator.ReadFiles(report.Disk.Device.InstanceId, volume.GuidPath);
    }

    public override void Update()
    {
        if (reading is not null && main.Coordinator.State == CoordinatorState.Idle)
        {
            files = main.Coordinator.Files;
            if (files is not null)
            {
                readBacks.RemoveAll(r => r.Volume == reading.Volume);
                readBacks.Add((reading.Volume, files));
            }
            readMessage = files is not null ? $"{reading.Title}: {Text.FilesResult(files)}"
                : main.Coordinator.Error is { Code: WorkerErrors.Cancelled } ? Text.ReadStopped
                : main.Coordinator.Error?.Text;
            reading = null;
        }
        foreach (var row in Volumes)
            row.CanRead = CanRead && row.HasFileSystem;
        base.Update();
    }

    public sealed class VolumeRow(VolumeInfo volume, Action read) : ViewModel
    {
        bool canRead = volume.FileSystem.Length > 0;

        public VolumeInfo Volume { get; } = volume;
        public string Title { get; } = Text.Volume(volume);
        public bool HasFileSystem => Volume.FileSystem.Length > 0;
        public Command ReadCommand { get; } = new(read);

        public bool CanRead
        {
            get => canRead;
            set => Set(ref canRead, value);
        }
    }

    /// <summary>One line per event, newest first; events that would read the same (minute, kind and caveat) are shown once, with a count.</summary>
    public static IReadOnlyList<EventRow> EventRows(IEnumerable<DiskEvent> events) =>
        [.. events.Select(e => new EventRow(Text.Event(e), Text.EventCaveat(e), e.IsError))
            .GroupBy(row => row)
            .Select(same => same.Key with { Title = Text.Repeated(same.Key.Title, same.Count()) })];

    public sealed record EventRow(string Title, string Caveat, bool IsError)
    {
        public bool HasCaveat => Caveat.Length > 0;
    }
}
