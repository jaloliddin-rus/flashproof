#:project ..\src\FlashProof\FlashProof.csproj
#:package Avalonia.Headless@12.1.4
#:property TargetFramework=net10.0-windows
#:property TreatWarningsAsErrors=false
#:property Nullable=enable

// Draws the README's screenshots into docs\images: the app's own screens, rendered off-screen over a stand-in worker
// and sample drives, so nothing personal shows and no drive is touched. The pass uses the real numbers of a SanDisk
// stick's Full test (61.4 GB, write 16.0 MB/s, read 165.0 MB/s) with made-up IDs; the fake is the design's example, a
// "2 TB" stick holding about 32 GB. Run it again from the repo root after changing a screen:
// dotnet run -c Release tools/make-screenshots.cs
using System.Threading.Channels;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using FlashProof;
using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.ViewModels;
using FlashProof.Windows;

string outDir = Path.GetFullPath(Path.Combine("docs", "images"));
Directory.CreateDirectory(outDir);
const long MiB = 1 << 20, GiB = 1L << 30, Slice = 256 * MiB;
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

var now = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
var rng = new Random(7);

// A SanDisk stick, one exFAT volume.
var sdVol = new VolumeInfo(@"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\", @"\Device\HarddiskVolume7", ["E:"], 1, 1 << 20, 61_522_000_000, "exFAT", "USB DRIVE", 0x4E210000, false, 61_490_000_000, 0, false);
var sdCand = new DiskCandidate(@"USBSTOR\DISK&VEN_USB&PROD_SANDISK_3.2GEN1\0001", "x", " USB  SanDisk 3.2Gen1 USB Device");
var sdDisk = new DiskInfo(sdCand, 2, new DeviceDescriptor("USB", "SanDisk 3.2Gen1", "1.00", "0001", 7, true), now.AddMinutes(-5), true, 61_524_148_224, 512, false, "MBR:1A2B3C4D", [sdVol]);
var sdId = new VolumeIdentity(sdCand.InstanceId, "USB SanDisk 3.2Gen1", "0001", sdDisk.Size, 512, 512, now.AddMinutes(-5), 1, "MBR:1A2B3C4D", sdVol.GuidPath, sdVol.Serial);
var sandisk = new ScannedDevice(sdCand, null, new InspectReport(sdDisk, null, [], []), sdId, null);

// A fake "2 TB" stick: it claims 2.0 TB, holds about 32 GB.
var fkVol = new VolumeInfo(@"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000002}\", @"\Device\HarddiskVolume9", ["F:"], 1, 1 << 20, 2_000_396_000_000, "exFAT", "", 0x5A3C9E01, false, 2_000_390_000_000, 0, false);
var fkCand = new DiskCandidate(@"USBSTOR\DISK&VEN_GENERIC&PROD_FLASH_DISK\0002", "y", "Generic Flash Disk USB Device");
var fkDisk = new DiskInfo(fkCand, 3, new DeviceDescriptor("Generic", "Flash Disk", "8.07", "0002", 7, true), now.AddMinutes(-2), true, 2_000_398_934_016, 512, false, "MBR:5E6F7A8B", [fkVol]);
var fkId = new VolumeIdentity(fkCand.InstanceId, "Generic Flash Disk", "0002", fkDisk.Size, 512, 512, now.AddMinutes(-2), 1, "MBR:5E6F7A8B", fkVol.GuidPath, fkVol.Serial);
var fake = new ScannedDevice(fkCand, null, new InspectReport(fkDisk, null, [], []), fkId, null);

// A card reader with no card.
var rdCand = new DiskCandidate(@"USBSTOR\DISK&VEN_GENERIC-&PROD_SD_MMC\0003", "z", "Generic- SD/MMC USB Device");
var rdDisk = new DiskInfo(rdCand, 4, new DeviceDescriptor("Generic-", "SD/MMC", "1.00", "", 7, true), now, false, 0, 512, false, "", []);
var noCard = new VerdictResult(Verdict.NoCardInReader, CapacityResult.NotChecked, 0);
var reader = new ScannedDevice(rdCand, noCard, new InspectReport(rdDisk, noCard, [], []), null, null);

var clock = new StepClock(now);
var worker = new FakeWorker();
var coordinator = new Coordinator(() => worker, clock, a => Dispatcher.UIThread.Post(a));
var window = new MainWindow();
var main = new MainViewModel(coordinator, clock, window);
main.CloseConfirmed += window.Close;
window.DataContext = main;
window.Width = 900;
window.Show();

void Pump(Func<bool> until)
{
    for (int i = 0; i < 3000 && !until(); i++)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(1);
    }
    Dispatcher.UIThread.RunJobs();
}

// Renders the window at `height`, keeps its top `crop` pixels (the fake's chart spans the claimed 2 TB, so it's cut
// off), and draws a thin gray border (GitHub's #d0d7de) so the white screen stands out on a white page.
void Shot(string name, double height, int crop)
{
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    window.Height = height;
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame()!;
    int w = frame.PixelSize.Width;
    using var image = new RenderTargetBitmap(new PixelSize(w, crop));
    using (var ctx = image.CreateDrawingContext())
    {
        ctx.DrawImage(frame, new Rect(0, 0, w, crop), new Rect(0, 0, w, crop));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#d0d7de")), 1), new Rect(0.5, 0.5, w - 1, crop - 1));
    }
    image.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine($"Wrote {Path.Combine(outDir, name + ".png")} ({w} x {crop}).");
}

// Slices of 256 MiB at about `mbps` MB/s, with a little jitter.
List<SpeedSlice> Slices(long from, long to, Func<long, double> mbps)
{
    var list = new List<SpeedSlice>();
    for (long o = from; o < to; o += Slice)
    {
        long n = Math.Min(Slice, to - o);
        list.Add(new SpeedSlice(o, n, n / (mbps(o) * (1 + (rng.NextDouble() - 0.5) * 0.08) * 1e6)));
    }
    return list;
}

var devices = new DevicesMessage([sandisk, fake, reader], []);
void Home()
{
    main.ShowHome();
    Pump(() => coordinator.State != CoordinatorState.Idle);
    if (coordinator.State == CoordinatorState.Scanning) worker.Emit(devices);
    Pump(() => coordinator.State == CoordinatorState.Idle);
}
worker.Emit(devices);
Pump(() => coordinator.Devices is not null);
Shot("home", 620, 500);

main.ShowInspect(sandisk);
Shot("inspect", 820, 724);
Home();

// The SanDisk, a third of the way through writing.
long planned = 61_405_806_592;
double SdWrite(long o) => o < 40_000_000_000 ? 18.3 : 12.9;
var writeSlices = Slices(0, planned, SdWrite);
writeSlices[150] = writeSlices[150] with { Seconds = writeSlices[150].Bytes / 6.8e6 };
main.StartTest(sandisk, new StartSpec(ConfirmViewModel.NewSessionId(clock), sdCand.InstanceId, sdVol.GuidPath, sdId));
Pump(() => main.Current is TestViewModel);
long done = 0;
foreach (var s in writeSlices.Take(92))
{
    clock.Advance(TimeSpan.FromSeconds(s.Seconds));
    done += s.Bytes;
    worker.Emit(new SampleMessage(TestPhase.Write, s.Offset, s.Bytes, s.Seconds));
    var p = new ProgressMessage(WorkPhase.Write, done, planned, s.Bytes / s.Seconds);
    worker.Emit(p);
    Pump(() => coordinator.Progress == p);
}
worker.Emit(new HeartbeatMessage());
Pump(() => false);
Shot("test", 760, 508);

// It passes.
var readSlices = Slices(0, planned, _ => 165.0);
var pass = new TestEvidence
{
    DiskSize = sdDisk.Size, PlannedBytes = planned, WrittenBytes = planned, VerifiedBytes = planned, FillEnd = FillEnd.PlanReached,
    Counts = { Ok = planned / TestRecord.Size },
};
var passResult = new TestResult
{
    SessionId = "20261114-090000-1a2b", Verdict = VerdictEngine.Decide(pass), Evidence = pass,
    Speed = new SpeedSummary { AverageWrite = 16.0e6, WorstWrite = 6.8e6, AverageRead = 165.0e6, WorstRead = 164.4e6, WriteSlices = writeSlices, ReadSlices = readSlices },
    StartedAt = now, FinishedAt = now.AddMinutes(74),
};
worker.Emit(new ResultMessage(passResult, "journal.json"));
Pump(() => main.Current is ResultViewModel);
Shot("result-pass", 1000, 878);
Home();

// The fake: the early check fires after 33 GiB; everything written is read back; the test files are kept.
long written = 33 * GiB, real = 31_440_502_784;
var fakeEvidence = new TestEvidence
{
    DiskSize = fkDisk.Size, PlannedBytes = 1_999_900_000_000, WrittenBytes = written, VerifiedBytes = written, FillEnd = FillEnd.EarlyFakeCheck,
    Counts = { Ok = real / TestRecord.Size, Misplaced = (written - real) / TestRecord.Size }, EarlyCheckBadSamples = 9, WrapSize = real,
};
var fakeResult = new TestResult
{
    SessionId = "20261114-104000-3c4d", Verdict = VerdictEngine.Decide(fakeEvidence), Evidence = fakeEvidence,
    Speed = new SpeedSummary
    {
        AverageWrite = 10.4e6, WorstWrite = 8.9e6, AverageRead = 19.5e6, WorstRead = 19.1e6,
        WriteSlices = Slices(0, written, _ => 10.4), ReadSlices = Slices(0, written, _ => 19.5),
    },
    StartedAt = now.AddMinutes(100), FinishedAt = now.AddMinutes(187), TestFilesKept = true,
};
clock.Advance(TimeSpan.FromMinutes(120));
main.StartTest(fake, new StartSpec("20261114-104000-3c4d", fkCand.InstanceId, fkVol.GuidPath, fkId));
Pump(() => main.Current is TestViewModel);
worker.Emit(new ResultMessage(fakeResult, "journal.json"));
Pump(() => main.Current is ResultViewModel);
Shot("result-fake", 1100, 508);

sealed class StepClock(DateTimeOffset start) : TimeProvider
{
    long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => start.AddTicks(ticks);
    public void Advance(TimeSpan by) => ticks += by.Ticks;
}

sealed class FakeWorker : IWorkerConnection
{
    readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    public List<string> Sent { get; } = [];
    public void Emit(WorkerMessage m) => lines.Writer.TryWrite(WorkerProtocol.Serialize(m));
    public async Task<string?> ReadLineAsync() => await lines.Reader.WaitToReadAsync() && lines.Reader.TryRead(out var l) ? l : null;
    public void Send(string line) => Sent.Add(line);
    public void Kill() { }
    public void Dispose() { }
}
