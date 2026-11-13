using System.Runtime.InteropServices;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>What Inspect found wrong with a drive (design §6.3).</summary>
public enum InspectProblem
{
    /// <summary>The drive refuses writes and no Windows policy explains it.</summary>
    WriteProtected,

    /// <summary>A volume runs past the end of the disk: a sign of a fake or a bad format (design §5.1).</summary>
    VolumeLargerThanDisk,

    /// <summary>Windows logged disk errors under the drive's disk number, or UAS resets, in the last 24 h.</summary>
    RecentDiskErrors,

    /// <summary>The optional read-back found files or folders that couldn't be read.</summary>
    UnreadableFiles,
}

/// <summary>A file or folder the read-back couldn't read.</summary>
/// <param name="Path">Relative to the volume root.</param>
/// <param name="Error">The Win32 error.</param>
public sealed record UnreadableFile(string Path, bool IsFolder, int Error);

/// <summary>The result of reading back every existing file.</summary>
/// <param name="Skipped">Files and folders Windows wouldn't open for this user: access denied, or in use. Not a fault in the drive.</param>
public sealed record FileReadResult(int FilesRead, long BytesRead, int Skipped, IReadOnlyList<UnreadableFile> Unreadable);

/// <summary>The result of Inspect for one drive (design §6.3). Inspect only reads.</summary>
/// <param name="Status">Whether the drive can be full-tested: null for Ready, otherwise the verdict it gets without a test (<see cref="Eligibility.Check"/>).</param>
/// <param name="Events">Disk events of the last 24 h about this drive, newest first; null if the event log couldn't be read.</param>
/// <param name="Files">The optional read-back of every existing file, if it was run.</param>
public sealed record InspectReport(DiskInfo Disk, VerdictResult? Status, IReadOnlyList<DiskEvent>? Events, IReadOnlyList<InspectProblem> Problems, FileReadResult? Files = null)
{
    /// <summary>
    /// "No problems found in these checks". Never a health pass (design §2): a fake or failing drive that hasn't
    /// logged errors yet passes these checks. False when there is no card, or the event log couldn't be read.
    /// </summary>
    public bool NoProblemsFound => Disk.MediaPresent && Events is not null && Problems.Count == 0;
}

/// <summary>Inspect mode (design §6.3) for a disk the scan has read. It writes nothing.</summary>
public static class Inspection
{
    const int ReadChunk = 4 << 20;
    const int ReadAlignment = 4096;

    /// <param name="ejectedTests">FlashProof's tests that ended because a drive was removed (<see cref="DiskEvents.ReadEjectedTests"/>).</param>
    public static InspectReport Run(DiskInfo disk, SystemFacts system, IReadOnlyList<EjectedTest> ejectedTests) =>
        Report(disk, system, DiskEvents.Read(disk, ejectedTests));

    /// <summary>
    /// The rules, from what was read. Events count whether or not the match is certain (design §6.3: "matched to the
    /// disk, with a caveat that disk numbers change"), so a drive replugged after errors isn't reported clean;
    /// <see cref="DiskEvent.Match"/> carries the caveat. The controller errors of an ejected test don't count.
    /// </summary>
    internal static InspectReport Report(DiskInfo disk, SystemFacts system, IReadOnlyList<DiskEvent>? events, FileReadResult? files = null)
    {
        var status = Eligibility.Check(disk, system);
        var problems = new List<InspectProblem>();
        // The disk's own answer counts even when another check decided the status; a policy blocking all writes hides it.
        if (status?.Verdict == Verdict.WriteProtected || (disk.WriteProtected && !system.Policy.BlocksAllRemovableWrites))
            problems.Add(InspectProblem.WriteProtected);
        if (disk.Volumes.Any(v => v.Offset + v.Length > disk.Size))
            problems.Add(InspectProblem.VolumeLargerThanDisk);
        if (events?.Any(e => e.IsError) == true)
            problems.Add(InspectProblem.RecentDiskErrors);
        if (files?.Unreadable.Count > 0)
            problems.Add(InspectProblem.UnreadableFiles);
        return new InspectReport(disk, status, events, problems, files);
    }

    /// <summary>The same report with the file read-back added.</summary>
    public static InspectReport WithFiles(InspectReport report, SystemFacts system, FileReadResult files) =>
        Report(report.Disk, system, report.Events, files);

    /// <summary>
    /// Reads every existing file on the volume to the end, unbuffered so the data comes from the drive and not from
    /// Windows' cache (design §6.3), and lists what can't be read. A read proves a file is readable, not that its
    /// contents are right. Links and junctions are skipped, so nothing outside the volume is ever read.
    /// </summary>
    /// <param name="volumeGuidPath">A volume of the inspected disk, <c>\\?\Volume{GUID}\</c>.</param>
    /// <param name="progress">Called with the bytes read so far.</param>
    public static async Task<FileReadResult> ReadAllFilesAsync(string volumeGuidPath, Action<long>? progress, CancellationToken ct)
    {
        if (!WindowsTestVolume.IsVolumeGuidPath(volumeGuidPath))
            throw new ArgumentException(@"Expected a volume GUID path: \\?\Volume{GUID}\", nameof(volumeGuidPath));
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var unreadable = new List<UnreadableFile>();
        var folders = new Stack<string>([volumeGuidPath]);
        using var buffer = new AlignedBuffer(ReadChunk, ReadAlignment);
        int files = 0, skipped = 0;
        long bytes = 0;

        while (folders.TryPop(out string? folder))
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Count(folder, isFolder: true, Win32Errors.CodeOf(ex.HResult));
                continue;
            }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo)
                {
                    folders.Push(entry.FullName + @"\");
                    continue;
                }
                var (read, error) = await ReadFileAsync(entry.FullName, buffer, n => progress?.Invoke(bytes + n), ct);
                bytes += read;
                if (error == 0)
                    files++;
                else
                    Count(entry.FullName, isFolder: false, error);
            }
        }
        return new FileReadResult(files, bytes, skipped, unreadable);

        void Count(string path, bool isFolder, int error)
        {
            if (error is Native.ErrorAccessDenied or Native.ErrorSharingViolation)
                skipped++;
            else
                unreadable.Add(new UnreadableFile(path[volumeGuidPath.Length..].TrimEnd('\\'), isFolder, error));
        }
    }

    /// <summary>Reads a file to the end; returns the bytes read and the Win32 error that stopped it, or 0.</summary>
    static async Task<(long Bytes, int Error)> ReadFileAsync(string path, AlignedBuffer buffer, Action<long> progress, CancellationToken ct)
    {
        using var handle = Native.CreateFile(path, Native.GenericRead, Native.ShareAll, 0, Native.OpenExisting,
            Native.FlagNoBuffering | Native.FlagOverlapped | Native.FlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
            return (0, Marshal.GetLastPInvokeError());
        long offset = 0;
        try
        {
            while (true)
            {
                int n = await RandomAccess.ReadAsync(handle, buffer.Memory, offset, ct);
                offset += n;
                progress(offset);
                if (n < ReadChunk)
                    return (offset, 0);
            }
        }
        catch (Exception ex) when (Win32Errors.Translate(ex, ct) is { } error)
        {
            return (offset, error.NativeCode);
        }
    }
}
