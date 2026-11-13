using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>The Windows disk events Inspect reports (design §6.3), by their System log source and ID.</summary>
public enum DiskEventKind
{
    /// <summary>disk 7: "The device ... has a bad block."</summary>
    BadBlock,

    /// <summary>disk 11: "The driver detected a controller error on ..."</summary>
    ControllerError,

    /// <summary>disk 153: an I/O was retried.</summary>
    RetriedIo,

    /// <summary>disk 157: logged on every unplug without ejecting first, so it's shown but never counted as an error.</summary>
    SurpriseRemoval,

    /// <summary>129 from the UAS driver: a request timed out and the drive was reset. Ordinary (non-UAS) flash drives never log it.</summary>
    UasReset,
}

/// <summary>How sure it is that an event is about the inspected drive.</summary>
public enum DiskEventMatch
{
    /// <summary>Logged under the drive's disk number since it was last plugged in: it is this drive.</summary>
    ThisPlugIn,

    /// <summary>Logged under the drive's disk number before it was last plugged in. Disk numbers are reused, so it may have been another drive.</summary>
    EarlierSameDiskNumber,

    /// <summary>Logged by the UAS driver, which names its adapter, not a disk: it may have been another UAS drive.</summary>
    UasDriver,
}

/// <param name="DiskNumber">The disk number the event names; null for a UAS reset.</param>
/// <param name="DuringEjectedTest">
/// One of the controller errors Windows logs when a drive is ejected while FlashProof is testing it (design §6.3):
/// shown, with that explanation, but not counted.
/// </param>
public sealed record DiskEvent(DiskEventKind Kind, DateTimeOffset Time, int? DiskNumber, DiskEventMatch Match, bool DuringEjectedTest = false)
{
    /// <summary>Every kind but a surprise removal counts as an error, except the controller errors of an ejected test (design §6.3).</summary>
    public bool IsError => Kind != DiskEventKind.SurpriseRemoval && !DuringEjectedTest;
}

/// <summary>A FlashProof test on this PC that ended because the drive was removed, with no I/O errors or bad data.</summary>
public sealed record EjectedTest(string DeviceInstanceId, DateTimeOffset Started, DateTimeOffset Finished);

/// <summary>An event as the System log holds it.</summary>
internal sealed record LoggedEvent(string Provider, int Id, DateTimeOffset Time, IReadOnlyList<string> Data);

/// <summary>
/// Recent disk errors from the System event log (design §6.3). Reading the log goes through Windows' event log
/// service and opens no disk. Events are matched to a drive by its disk number, which Windows reuses after a drive
/// is unplugged, so each event says how sure the match is.
/// </summary>
public static partial class DiskEvents
{
    /// <summary>How far back Inspect looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The most events read, newest first, so a drive in an error storm can't stall Inspect.</summary>
    const int MaxEvents = 1000;

    /// <summary>
    /// When the controller errors of an ejected test can be logged, around the test's end (design §6.3). On the SanDisk
    /// they came 0.6–5.1 s before it, in two runs; the margins allow for slower drives and PCs.
    /// </summary>
    static readonly TimeSpan EjectBefore = TimeSpan.FromSeconds(30);
    static readonly TimeSpan EjectAfter = TimeSpan.FromSeconds(5);

    const int NextTimeoutMs = 10_000;
    const string UasDriver = "UASPStor";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>
    /// Disk events 7, 11, 153 and 157, and 129 only from the UAS driver: other sources log unrelated 129s (Hyper-V does
    /// on the development PC). Source names are matched without regard to case.
    /// </summary>
    static string Query(TimeSpan window) =>
        $"*[System[((Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=153 or EventID=157)) or (Provider[@Name='{UasDriver}'] and EventID=129))" +
        $" and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";

    /// <summary>The events of the last 24 h about this disk, newest first; null if the log can't be read.</summary>
    /// <param name="ejectedTests">FlashProof's ejected tests (<see cref="ReadEjectedTests"/>), whose controller errors don't count.</param>
    public static IReadOnlyList<DiskEvent>? Read(DiskInfo disk, IReadOnlyList<EjectedTest> ejectedTests)
    {
        if (ReadLog(Window) is not { } logged)
            return null;
        bool onUas = IsOnUas(disk.Device.InstanceId);
        return logged.Select(e => About(e, disk.DiskNumber, disk.LastArrival, onUas)).OfType<DiskEvent>()
            .Select(e => Marked(e, disk.Device.InstanceId, ejectedTests)).ToList();
    }

    /// <summary>
    /// The event, marked <see cref="DiskEvent.DuringEjectedTest"/> if it's a controller error on this drive from
    /// 30 s before to 5 s after the end of one of its ejected tests (design §6.3).
    /// </summary>
    internal static DiskEvent Marked(DiskEvent e, string instanceId, IEnumerable<EjectedTest> ejectedTests) =>
        e.Kind == DiskEventKind.ControllerError && ejectedTests.Any(t =>
            t.DeviceInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)
            && e.Time >= t.Started && e.Time >= t.Finished - EjectBefore && e.Time <= t.Finished + EjectAfter)
            ? e with { DuringEjectedTest = true }
            : e;

    /// <summary>
    /// The tests whose journals in the sessions folder say they ended because the drive was removed, with no I/O
    /// errors or bad data, recently enough to matter for the last 24 h of events. A journal that can't be read is
    /// skipped, so its events simply count. Only FlashProof's own folder on the PC's disk is read.
    /// </summary>
    public static IReadOnlyList<EjectedTest> ReadEjectedTests(string sessionsFolder)
    {
        var tests = new List<EjectedTest>();
        if (!Directory.Exists(sessionsFolder))
            return tests;
        var since = DateTime.UtcNow - Window - EjectAfter; // a journal is saved last when its test ends
        foreach (string folder in Directory.EnumerateDirectories(sessionsFolder))
        {
            string path = Path.Combine(folder, "journal.json");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < since)
                continue;
            try
            {
                if (EjectedTestIn(JournalStore.Load(path)) is { } test)
                    tests.Add(test);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
            }
        }
        return tests;
    }

    /// <summary>The test a journal describes, if it ended because the drive was removed and found nothing wrong.</summary>
    internal static EjectedTest? EjectedTestIn(Journal journal) =>
        journal.Result is { Verdict.InterruptReason: InterruptReason.UserUnplugged, Evidence: { Errors.Count: 0, Counts.NotOk: 0, EarlyCheckBadSamples: 0 } } result
            ? new EjectedTest(journal.Identity.DeviceInstanceId, result.StartedAt, result.FinishedAt)
            : null;

    /// <summary>What an event says about one disk, or null if it isn't about it.</summary>
    /// <param name="arrival">When the disk was last plugged in; events before it may be about another drive.</param>
    /// <param name="onUas">The disk is on the UAS driver, the only one whose 129s count.</param>
    internal static DiskEvent? About(LoggedEvent e, int diskNumber, DateTimeOffset? arrival, bool onUas)
    {
        if (e.Provider.Equals(UasDriver, StringComparison.OrdinalIgnoreCase))
            return e.Id == 129 && onUas ? new DiskEvent(DiskEventKind.UasReset, e.Time, null, DiskEventMatch.UasDriver) : null;
        if (!e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase))
            return null;

        DiskEventKind? kind = e.Id switch
        {
            7 => DiskEventKind.BadBlock,
            11 => DiskEventKind.ControllerError,
            153 => DiskEventKind.RetriedIo,
            157 => DiskEventKind.SurpriseRemoval,
            _ => null,
        };
        if (kind is null || DiskNumberIn(e.Data) != diskNumber)
            return null;
        var match = arrival is { } a && e.Time >= a ? DiskEventMatch.ThisPlugIn : DiskEventMatch.EarlierSameDiskNumber;
        return new DiskEvent(kind.Value, e.Time, diskNumber, match);
    }

    /// <summary>An event rendered as XML by the event log, or null if it lacks the source, ID or time.</summary>
    internal static LoggedEvent? Parse(string xml)
    {
        var root = XElement.Parse(xml);
        var system = root.Element(Ns + "System");
        string? provider = system?.Element(Ns + "Provider")?.Attribute("Name")?.Value;
        int? id = (int?)system?.Element(Ns + "EventID");
        var time = (DateTimeOffset?)system?.Element(Ns + "TimeCreated")?.Attribute("SystemTime");
        if (provider is null || id is null || time is null)
            return null;
        var data = root.Element(Ns + "EventData")?.Elements(Ns + "Data").Select(d => d.Value).ToList() ?? [];
        return new LoggedEvent(provider, id.Value, time.Value, data);
    }

    /// <summary>
    /// The disk number in a disk event's first field, such as <c>\Device\Harddisk3\DR6</c>. The DR number changes with
    /// each plug-in; the Harddisk number is the disk number.
    /// </summary>
    static int? DiskNumberIn(IReadOnlyList<string> data) =>
        data.Count > 0 && HarddiskPath().Match(data[0]) is { Success: true } m && int.TryParse(m.Groups[1].ValueSpan, out int n) ? n : null;

    [GeneratedRegex(@"^\\Device\\Harddisk(\d+)\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HarddiskPath();

    static bool IsOnUas(string instanceId) =>
        Pnp.Locate(instanceId) is { } node
        && Pnp.SelfAndAncestors(node).Any(id => Pnp.Locate(id) is { } n
            && string.Equals(Pnp.StringProperty(n, Native.DeviceService), UasDriver, StringComparison.OrdinalIgnoreCase));

    /// <summary>The disk events and UAS resets logged within the window, newest first; null if the log can't be read.</summary>
    internal static IReadOnlyList<LoggedEvent>? ReadLog(TimeSpan window)
    {
        using var results = Native.EvtQuery(0, "System", Query(window), Native.EvtQueryChannelPath | Native.EvtQueryReverseDirection);
        if (results.IsInvalid)
            return null;
        var events = new List<LoggedEvent>();
        var handles = new nint[64];
        var buffer = new char[4096];
        while (events.Count < MaxEvents)
        {
            if (!Native.EvtNext(results, handles.Length, handles, NextTimeoutMs, 0, out int returned))
                return Marshal.GetLastPInvokeError() == Native.ErrorNoMoreItems ? events : null;
            try
            {
                for (int i = 0; i < returned; i++)
                {
                    if (Render(handles[i], ref buffer) is not { } xml)
                        return null;
                    if (Parse(xml) is { } e)
                        events.Add(e);
                }
            }
            finally
            {
                for (int i = 0; i < returned; i++)
                    Native.EvtClose(handles[i]);
            }
        }
        return events;
    }

    static string? Render(nint handle, ref char[] buffer)
    {
        while (true)
        {
            if (Native.EvtRender(0, handle, Native.EvtRenderEventXml, buffer.Length * sizeof(char), buffer, out int used, out _))
                return new string(buffer, 0, used / sizeof(char)).TrimEnd('\0');
            if (Marshal.GetLastPInvokeError() != Native.ErrorInsufficientBuffer)
                return null;
            buffer = new char[(used + 1) / sizeof(char)];
        }
    }
}
