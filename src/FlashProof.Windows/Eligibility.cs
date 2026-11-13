using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>
/// Whether a disk may be full-tested (design §5.1): "Ready", or the verdict it gets without a test. There is no
/// override. The rules are pure, so they're tested without hardware.
/// </summary>
public static class Eligibility
{
    /// <summary>Below this much free space the drive can't be tested (design §6.2); the same as the runner's default.</summary>
    public const long MinimumFreeSpace = 256L << 20;

    static readonly string[] SupportedFileSystems = ["FAT32", "exFAT", "NTFS"];

    /// <summary>The volume a full test uses: the largest with a supported file system, else the largest.</summary>
    public static VolumeInfo? TestVolume(DiskInfo disk) =>
        disk.Volumes.OrderByDescending(v => IsSupported(v.FileSystem)).ThenByDescending(v => v.Length).FirstOrDefault();

    /// <summary>Null if the disk's test volume may be full-tested; otherwise the verdict, with no test run.</summary>
    public static VerdictResult? Check(DiskInfo disk, SystemFacts system)
    {
        var d = disk.Descriptor;
        if (d.BusType is not (DeviceDescriptor.BusTypeUsb or DeviceDescriptor.BusTypeSd or DeviceDescriptor.BusTypeMmc))
            return CantTest(CantTestReason.UnsupportedDevice);
        if (!d.RemovableMedia)
            return CantTest(d.BusType == DeviceDescriptor.BusTypeUsb ? CantTestReason.ExternalSsdOrHardDrive : CantTestReason.EmbeddedStorage);
        if (disk.Volumes.Any(v => HoldsSystemFiles(v, system)))
            return CantTest(CantTestReason.HoldsSystemFiles);
        if (!disk.MediaPresent)
            return Result(Verdict.NoCardInReader);
        if (TestVolume(disk) is not { } volume)
            return CantTest(CantTestReason.NoFileSystem);
        if (system.Policy.BlocksAllRemovableWrites || (system.Policy.BitLockerDenyWrite && volume.ReadOnly))
            return Result(Verdict.WritingBlockedByWindows);
        if (disk.WriteProtected || volume.ReadOnly)
            return Result(Verdict.WriteProtected);
        if (volume.DiskExtents != 1)
            return CantTest(CantTestReason.VolumeSpansDisks);
        if (volume.Offset + volume.Length > disk.Size)
            return CantTest(CantTestReason.VolumeLargerThanDisk);
        if (volume.BitLocker)
            return CantTest(CantTestReason.BitLockerLocked);
        if (!IsSupported(volume.FileSystem))
            return CantTest(CantTestReason.NoFileSystem);
        if (volume.FreeBytes < MinimumFreeSpace)
            return CantTest(CantTestReason.NotEnoughFreeSpace);
        return null;
    }

    static bool HoldsSystemFiles(VolumeInfo volume, SystemFacts system) =>
        string.Equals(volume.DeviceName, system.SystemPartitionDevice, StringComparison.OrdinalIgnoreCase)
        || volume.Letters.Any(system.ProtectedLetters.Contains);

    static bool IsSupported(string fileSystem) => SupportedFileSystems.Contains(fileSystem, StringComparer.OrdinalIgnoreCase);

    static VerdictResult Result(Verdict verdict) => new(verdict, CapacityResult.NotChecked, 0);

    static VerdictResult CantTest(CantTestReason reason) => new(Verdict.CantTest, CapacityResult.NotChecked, 0, CantTestReason: reason);
}
