using System.Security.Cryptography;
using FlashProof.Core;
using Xunit.Abstractions;

namespace FlashProof.Windows.Tests;

// Tests on a real removable drive, named in FLASHPROOF_TEST_VOLUME; skipped otherwise, so CI passes without one.
// Only the full-test case writes, and only with FLASHPROOF_TEST_WRITES=1.
public class HardwareTests(ITestOutputHelper output)
{
    /// <summary>Every file, hidden and system ones included: a freshly formatted drive may hold nothing else.</summary>
    static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };

    /// <summary>The scan finds the drive among the USB and SD disks, reads its volumes, and finds it ready to test.</summary>
    [HardwareFact]
    public void Scan_finds_the_drive_ready()
    {
        var system = SystemFacts.Read();
        output.WriteLine($"Protected letters: {string.Join(", ", system.ProtectedLetters.Order())}; system partition {system.SystemPartitionDevice}; {system.Policy}");
        foreach (var candidate in DeviceScan.FindCandidates())
            output.WriteLine($"Candidate: {candidate.Name}");

        foreach (string configured in TestVolumes.Configured)
        {
            var (disk, volume) = TestVolumes.Find(configured);
            var verdict = Eligibility.Check(disk, system);
            output.WriteLine($"{configured}: {disk.Descriptor.VendorProduct}, bus {disk.Descriptor.BusType}, removable {disk.Descriptor.RemovableMedia}, " +
                $"{disk.Size:N0} B, {disk.PartitionTableId}, write protected {disk.WriteProtected}, eligibility {verdict?.Verdict.ToString() ?? "Ready"} {verdict?.CantTestReason}");
            foreach (var v in disk.Volumes)
                output.WriteLine($"  {string.Join(",", v.Letters)} {v.Label} {v.FileSystem} at {v.Offset:N0} B, {v.Length:N0} B, {v.FreeBytes:N0} B free, extents {v.DiskExtents}, read-only {v.ReadOnly}");

            Assert.True(disk.MediaPresent);
            Assert.Contains(volume, disk.Volumes);
            Assert.NotNull(disk.LastArrival);
            Assert.Null(verdict);
        }
        output.WriteLine($"USB devices with problems: {DeviceScan.FindProblemDevices().Count}");
    }

    /// <summary>Inspect reads the drive's state and its recent disk events, and writes nothing.</summary>
    [HardwareFact]
    public void Inspect_reports_on_the_drive()
    {
        var system = SystemFacts.Read();
        foreach (string configured in TestVolumes.Configured)
        {
            var (disk, _) = TestVolumes.Find(configured);
            var report = Inspection.Run(disk, system, ejectedTests: []);
            output.WriteLine($"{configured}: disk {disk.DiskNumber}, plugged in {disk.LastArrival:u}, {report.Events?.Count} events in 24 h, " +
                $"problems [{string.Join(", ", report.Problems)}], no problems found: {report.NoProblemsFound}");
            foreach (var e in report.Events ?? [])
                output.WriteLine($"  {e.Time:u} {e.Kind} disk {e.DiskNumber} ({e.Match})");

            Assert.NotNull(report.Events);
            Assert.True(report.Disk.MediaPresent);
            Assert.DoesNotContain(InspectProblem.WriteProtected, report.Problems);
            Assert.DoesNotContain(InspectProblem.VolumeLargerThanDisk, report.Problems);
        }
    }

    /// <summary>The optional read-back reads every file to the end, unbuffered, and finds them all readable.</summary>
    [HardwareFact]
    public async Task Every_existing_file_reads_back()
    {
        foreach (string configured in TestVolumes.Configured)
        {
            var (_, volume) = TestVolumes.Find(configured);
            var sizes = Directory.EnumerateFiles(volume.GuidPath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(f => new FileInfo(f).Length).ToList();
            long reported = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var result = await Inspection.ReadAllFilesAsync(volume.GuidPath, n => reported = n, CancellationToken.None);

            output.WriteLine($"{configured}: {result.FilesRead} files, {result.BytesRead:N0} B in {clock.Elapsed.TotalSeconds:F1} s " +
                $"({result.BytesRead / Math.Max(clock.Elapsed.TotalSeconds, 0.001) / 1e6:F0} MB/s), {result.Skipped} skipped, {result.Unreadable.Count} unreadable");
            Assert.Empty(result.Unreadable);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(sizes.Count, result.FilesRead);
            Assert.Equal(sizes.Sum(), result.BytesRead);
            Assert.Equal(result.BytesRead, reported);
        }
    }

    [HardwareFact]
    public void Volume_lies_within_its_disk()
    {
        foreach (string configured in TestVolumes.Configured)
        {
            using var volume = TestVolumes.Open(configured);

            Assert.InRange(volume.VolumeOffset, 1, volume.DiskSize - 1);
            Assert.InRange(volume.VolumeOffset + volume.VolumeSize, volume.VolumeOffset + 1, volume.DiskSize);
            Assert.Contains(volume.SectorSize, new[] { 512, 4096 });
            Assert.InRange(volume.FreeSpace, 0, volume.VolumeSize);
        }
    }

    [HardwareFact]
    public void Identity_is_stable_and_describes_the_device()
    {
        foreach (string configured in TestVolumes.Configured)
        {
            using var volume = TestVolumes.Open(configured);

            var first = volume.ReadIdentity();
            var second = volume.ReadIdentity();

            Assert.Equal(first, second);
            Assert.NotEmpty(first.VendorProduct);
            Assert.Equal(volume.DiskSize, first.DiskSize);
            Assert.Equal(volume.SectorSize, first.LogicalSectorSize);
            Assert.Matches("^(MBR:[0-9A-F]{8}|GPT:\\{.+\\})$", first.PartitionTableId);
            Assert.True(WindowsTestVolume.IsVolumeGuidPath(first.VolumeGuidPath));
            Assert.NotEqual(0u, first.VolumeSerial);
            Assert.NotEmpty(first.DeviceInstanceId);
            Assert.NotEqual(default, first.LastArrival);
            output.WriteLine($"{configured}: {first.VendorProduct}, disk {first.DiskSize:N0} B, sectors {first.LogicalSectorSize}/{first.PhysicalSectorSize}, " +
                $"volume at {volume.VolumeOffset:N0} B, {first.PartitionTableId}, media changes {first.MediaChangeCount}, volume serial {first.VolumeSerial:X8}");
        }
    }

    /// <summary>The extents of existing files, read through the raw volume, give the same bytes as the files themselves.</summary>
    [HardwareFact]
    public async Task Extents_point_at_the_files_data()
    {
        const int Block = 64 << 10;
        foreach (string configured in TestVolumes.Configured)
        {
            using var volume = TestVolumes.Open(configured);
            string root = volume.ReadIdentity().VolumeGuidPath;
            using var raw = Native.CreateFile(root[..^1], Native.GenericRead, Native.ShareAll, 0, Native.OpenExisting, Native.FlagNoBuffering, 0);
            Assert.False(raw.IsInvalid);
            var files = Directory.EnumerateFiles(root, "*", AllFiles)
                .Where(f => new FileInfo(f).Length > 0).Take(64).ToList();
            Assert.NotEmpty(files);
            var fromFile = TestVolumes.AlignedBuffer(Block);
            var fromDisk = TestVolumes.AlignedBuffer(Block);
            int compared = 0, fragmented = 0;

            foreach (string path in files)
            {
                long length = new FileInfo(path).Length;
                using var file = volume.OpenFile(path[root.Length..]);
                var extents = file.GetExtents();

                Assert.NotEmpty(extents);
                Assert.True(extents.Sum(e => e.Length) >= length, $"{path}: extents cover less than the file");
                fragmented += extents.Count > 1 ? 1 : 0;
                foreach (var extent in extents.Where(e => e.FileOffset < length).Take(16))
                {
                    Assert.InRange(extent.DiskOffset, volume.VolumeOffset, volume.VolumeOffset + volume.VolumeSize - extent.Length);
                    int block = (int)Math.Min(Block, extent.Length);
                    int n = await file.ReadAsync(extent.FileOffset, fromFile[..block], CancellationToken.None);
                    int m = RandomAccess.Read(raw, fromDisk.Span[..block], extent.DiskOffset - volume.VolumeOffset);

                    Assert.Equal((int)Math.Min(block, length - extent.FileOffset), n);
                    Assert.Equal(block, m);
                    Assert.True(fromFile.Span[..n].SequenceEqual(fromDisk.Span[..n]), $"{path}: bytes at file offset {extent.FileOffset} differ on the disk");
                    compared++;
                }
            }
            output.WriteLine($"{configured}: {files.Count} files ({fragmented} in more than one extent), {compared} extents matched the raw volume");
        }
    }

    /// <summary>Files with more runs than one call returns are read in several calls (ERROR_MORE_DATA).</summary>
    [HardwareFact]
    public void Extents_are_the_same_one_run_per_call()
    {
        foreach (string configured in TestVolumes.Configured)
        {
            using var volume = TestVolumes.Open(configured);
            string root = volume.ReadIdentity().VolumeGuidPath;
            int files = 0, fragmented = 0;

            foreach (string path in Directory.EnumerateFiles(root, "*", AllFiles).Take(32))
            {
                using var handle = Native.CreateFile(path, Native.FileReadAttributes, Native.ShareAll, 0, Native.OpenExisting, 0, 0);
                Assert.False(handle.IsInvalid);
                var extents = volume.ExtentsOf(handle);

                Assert.Equal(extents, volume.ExtentsOf(handle, runsPerCall: 1));
                files++;
                fragmented += extents.Count > 1 ? 1 : 0;
            }
            output.WriteLine($"{configured}: {files} files, {fragmented} in more than one extent");
        }
    }

    /// <summary>
    /// The real thing at a small scale: 256 MiB in four 64 MiB files, with the early fake check after each, then
    /// verification, the verdict and cleanup. Existing files must be unchanged afterwards.
    /// </summary>
    [HardwareWriteFact]
    public async Task A_small_full_test_passes_and_cleans_up()
    {
        const long Tested = 256L << 20;
        foreach (string configured in TestVolumes.Configured)
        {
            using var volume = TestVolumes.Open(configured);
            var identity = volume.ReadIdentity();
            var before = Fingerprint(identity.VolumeGuidPath);
            string session = $"hwtest-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            string journalFolder = Path.Combine(Path.GetTempPath(), "FlashProofTests", session);
            var options = new FullTestOptions { FileSize = 64L << 20, ReserveBytes = volume.FreeSpace - Tested };
            var request = new FullTestRequest(session, (ulong)Random.Shared.NextInt64(), identity, Path.Combine(journalFolder, "journal.json"));

            try
            {
                var result = await FullTestRunner.RunAsync(volume, request, options);
                output.WriteLine($"{configured}: {result.Verdict.Verdict}, write {result.Speed.AverageWrite / 1e6:F1} MB/s, read {result.Speed.AverageRead / 1e6:F1} MB/s");

                Assert.Empty(result.Evidence.Errors);
                Assert.Equal(Verdict.PassedCapacityNotChecked, result.Verdict.Verdict);
                Assert.Equal(Tested, result.Evidence.VerifiedBytes);
                Assert.Equal(Tested / TestRecord.Size, result.Evidence.Counts.Ok);
                Assert.False(result.TestFilesKept);
                Assert.False(Directory.Exists(identity.VolumeGuidPath + "FlashProof-" + session));
                Assert.Equal(before, Fingerprint(identity.VolumeGuidPath));
            }
            finally
            {
                if (Directory.Exists(journalFolder))
                    Directory.Delete(journalFolder, recursive: true);
            }
        }
    }

    /// <summary>Every file's name, size and a hash of its first MiB.</summary>
    static Dictionary<string, (long, string)> Fingerprint(string root)
    {
        var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*", AllFiles))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[Math.Min(stream.Length, 1 << 20)];
            stream.ReadExactly(head);
            result[path] = (stream.Length, Convert.ToHexString(SHA256.HashData(head)));
        }
        return result;
    }
}
