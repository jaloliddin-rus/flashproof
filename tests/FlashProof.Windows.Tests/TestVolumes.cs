using System.Runtime.InteropServices;

namespace FlashProof.Windows.Tests;

/// <summary>
/// The removable volumes the hardware tests may use, named explicitly in FLASHPROOF_TEST_VOLUME (drive letters such
/// as <c>E:\</c> or volume GUID paths, separated by ';'). They're looked up among the USB and SD disks the device
/// scan finds, so the computer's own disks are never opened or queried. Writing also needs FLASHPROOF_TEST_WRITES=1.
/// </summary>
public static class TestVolumes
{
    public const string VolumeVariable = "FLASHPROOF_TEST_VOLUME";
    public const string WritesVariable = "FLASHPROOF_TEST_WRITES";

    public static IReadOnlyList<string> Configured { get; } =
        (Environment.GetEnvironmentVariable(VolumeVariable) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool WritesAllowed => Environment.GetEnvironmentVariable(WritesVariable) == "1";

    /// <summary>The scanned disk and volume a configured name refers to; only removable USB, SD or MMC disks qualify.</summary>
    public static (DiskInfo Disk, VolumeInfo Volume) Find(string configured)
    {
        string letter = configured.TrimEnd('\\');
        foreach (var candidate in DeviceScan.FindCandidates())
        {
            var disk = DeviceScan.Inspect(candidate);
            var volume = disk.Volumes.FirstOrDefault(v => v.GuidPath.Equals(configured, StringComparison.OrdinalIgnoreCase)
                || v.Letters.Contains(letter, StringComparer.OrdinalIgnoreCase));
            if (volume is null)
                continue;
            if (!disk.Descriptor.RemovableMedia)
                throw new InvalidOperationException($"Refusing {configured}: the disk doesn't report removable media.");
            return (disk, volume);
        }
        throw new InvalidOperationException($"Refusing {configured}: it isn't a volume on a USB, SD or MMC disk.");
    }

    public static WindowsTestVolume Open(string configured)
    {
        var (disk, volume) = Find(configured);
        return WindowsTestVolume.Open(volume.GuidPath, disk.Device.InstanceId);
    }

    /// <summary>A pinned buffer aligned for unbuffered I/O.</summary>
    public static Memory<byte> AlignedBuffer(int length, int alignment = 4096)
    {
        var array = GC.AllocateArray<byte>(length + alignment, pinned: true);
        long address = Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
        return array.AsMemory((int)((alignment - address % alignment) % alignment), length);
    }
}

/// <summary>A read-only hardware test: skipped unless FLASHPROOF_TEST_VOLUME names a volume.</summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (TestVolumes.Configured.Count == 0)
            Skip = $"No test volume: set {TestVolumes.VolumeVariable}.";
    }
}

/// <summary>A hardware test that writes to the volume: also needs FLASHPROOF_TEST_WRITES=1.</summary>
public sealed class HardwareWriteFactAttribute : FactAttribute
{
    public HardwareWriteFactAttribute()
    {
        if (TestVolumes.Configured.Count == 0)
            Skip = $"No test volume: set {TestVolumes.VolumeVariable}.";
        else if (!TestVolumes.WritesAllowed)
            Skip = $"Writing is not allowed: set {TestVolumes.WritesVariable}=1.";
    }
}
