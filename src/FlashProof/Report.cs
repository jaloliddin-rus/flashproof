using System.Text;
using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.ViewModels;
using FlashProof.Windows;

namespace FlashProof;

/// <summary>
/// The exported test report (design §9): plain text, to save or paste into a message. Serial numbers, and the device ID
/// and volume serial that contain or reveal them, are left out unless the user asks for them.
/// </summary>
public static class Report
{
    const int LabelWidth = 11;

    public static string Build(string name, ScannedDevice device, TestOutcome outcome, bool includeSerials, DateTimeOffset now)
    {
        var text = new StringBuilder();
        void Line(string label, string value) => text.Append((label.Length > 0 ? label + ":" : "").PadRight(LabelWidth)).AppendLine(value);

        text.AppendLine($"{Text.ReportTitle}, {Text.When(now)}").AppendLine();
        var identity = device.Identity;
        Line(Text.ReportDrive, includeSerials || identity is null ? name : $"{name} ({Text.SerialHidden})");
        if (includeSerials && identity is not null)
        {
            Line(Text.ReportSerial, identity.Serial);
            Line(Text.ReportDeviceId, identity.DeviceInstanceId);
        }
        var result = outcome.Result;
        var evidence = result?.Evidence ?? new TestEvidence();
        Line(Text.ReportSize, Text.BothUnits(identity?.DiskSize ?? evidence.DiskSize));
        if (device.Report?.Disk is { } disk && Eligibility.TestVolume(disk) is { } volume)
            Line(Text.ReportTestedOn, Text.Volume(volume));
        text.AppendLine();

        var v = outcome.Verdict;
        Line(Text.ReportResult, Text.Title(v.Verdict));
        Line("", Text.Explanation(v, evidence, outcome.StayedPluggedIn));
        if (v.InterruptReason == InterruptReason.UserUnplugged && evidence.DeviceRemoved)
            Line("", Text.ReportUserUnplugged);
        if (result is null)
            return text.ToString();
        Line(Text.StorageLabel, Text.StorageResult(v, evidence));
        Line(Text.CapacityLabel, Text.CapacityResult(v, evidence));
        text.AppendLine();

        Line(Text.ReportTested, Text.Tested(evidence, v.Coverage));
        if (evidence.Replugged)
            Line("", Text.Replugged);
        Line(Text.SpeedLabel, Text.WriteSpeeds(result.Speed));
        Line("", Text.ReadSpeeds(result.Speed));
        if (Text.Usb2Note(result.Speed) is { } note)
            Line("", note);
        Line(Text.ReportTime, Text.TookTime(result));
        Line(Text.ReportRecords, Text.Records(evidence.Counts));
        Line(Text.ReportErrors, evidence.Errors.Count == 0 ? Text.ReportNone : Text.IoErrorLine(evidence.Errors[0]));
        foreach (var error in evidence.Errors.Skip(1))
            Line("", Text.IoErrorLine(error));
        if (Text.FilesKeptOrNull(result) is { } kept)
            Line(Text.ReportFiles, kept);
        Line(Text.ReportSession, result.SessionId);
        Line(Text.ReportApp, AppVersion);
        return text.ToString();
    }

    /// <summary>The Inspect report (design §6.3): what the Inspect screen shows, as plain text. Never a health pass.</summary>
    /// <param name="readBacks">Each volume whose files were read back, and what that found.</param>
    public static string BuildInspect(string name, ScannedDevice device, IReadOnlyList<(VolumeInfo Volume, FileReadResult Files)> readBacks,
        bool includeSerials, DateTimeOffset now)
    {
        var report = device.Report ?? throw new ArgumentException("Inspect needs the drive's report.", nameof(device));
        var disk = report.Disk;
        var text = new StringBuilder();
        void Line(string label, string value) => text.Append((label.Length > 0 ? label + ":" : "").PadRight(LabelWidth)).AppendLine(value);
        void Lines(string label, IEnumerable<string> values)
        {
            foreach (string value in values)
            {
                Line(label, value);
                label = "";
            }
        }

        text.AppendLine($"{Text.InspectReportTitle}, {Text.When(now)}").AppendLine();
        string serial = device.Identity?.Serial ?? disk.Descriptor.Serial.Trim();
        Line(Text.ReportDrive, includeSerials || serial.Length == 0 ? name : $"{name} ({Text.SerialHidden})");
        if (includeSerials)
        {
            if (serial.Length > 0)
                Line(Text.ReportSerial, serial);
            Line(Text.ReportDeviceId, device.Device.InstanceId);
        }
        if (disk.MediaPresent)
            Line(Text.ReportSize, Text.BothUnits(disk.Size));
        var (status, advice) = Text.DeviceStatus(report.Status);
        Line(Text.ReportStatus, status);
        if (advice.Length > 0)
            Line("", advice);
        text.AppendLine();

        var files = readBacks.Select(r => r.Files).ToList();
        if (InspectViewModel.NoProblems(report, files))
            Lines(Text.ReportFound, [Text.NoProblemsFound, Text.NotAHealthPass]);
        else
            Lines(Text.ReportFound, InspectViewModel.ProblemLines(report, files));
        if (disk.MediaPresent)
        {
            text.AppendLine();
            Lines(Text.ReportPartition, disk.Volumes.Select(Text.Volume));
            Line(Text.ReportWrites, Text.WriteProtection(disk, report.Status));
            if (report.Events is not { Count: > 0 } events)
                Line(Text.ReportErrors, report.Events is null ? Text.EventLogUnreadable : Text.ReportNoEvents);
            else
                Lines(Text.ReportErrors, InspectViewModel.EventRows(events).SelectMany(e => e.HasCaveat ? [e.Title, "  " + e.Caveat] : new[] { e.Title }));
            if (readBacks.Count == 0)
                Line(Text.ReportReadBack, Text.ReportNotRun);
            foreach (var (volume, read) in readBacks)
                Lines(Text.ReportReadBack, [$"{Text.Volume(volume)}: {Text.FilesResult(read)}", .. read.Unreadable.Select(f => "• " + Text.Unreadable(f))]);
        }
        Line(Text.ReportApp, AppVersion);
        return text.ToString();
    }

    static string AppVersion => $"{Text.AppName} {typeof(Report).Assembly.GetName().Version}";
}
