using System.Runtime.InteropServices;
using FlashProof.Core;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>A USB, SD or MMC disk in Windows' device list, found without opening anything.</summary>
public sealed record DiskCandidate(string InstanceId, string InterfacePath, string Name);

/// <summary>A volume on a candidate disk.</summary>
/// <param name="DeviceName">Such as <c>\Device\HarddiskVolume7</c>.</param>
/// <param name="Letters">Drive letters, such as "E:".</param>
/// <param name="DiskExtents">How many disk ranges the volume spans; 1 for a normal partition.</param>
/// <param name="FileSystem">As Windows names it ("FAT32", "exFAT", "NTFS", "FAT" for FAT12/16), or "" if none is mounted.</param>
/// <param name="Error">The Win32 error when the file system couldn't be read, or 0.</param>
/// <param name="BitLocker">The volume starts with BitLocker's signature: it's encrypted and locked.</param>
public sealed record VolumeInfo(string GuidPath, string DeviceName, IReadOnlyList<string> Letters, int DiskExtents, long Offset, long Length,
    string FileSystem, string Label, uint Serial, bool ReadOnly, long FreeBytes, int Error, bool BitLocker);

/// <summary>What a candidate disk says about itself, and its volumes. Size and volumes are known only with media present.</summary>
public sealed record DiskInfo(DiskCandidate Device, int DiskNumber, DeviceDescriptor Descriptor, DateTimeOffset? LastArrival, bool MediaPresent,
    long Size, int SectorSize, bool WriteProtected, string PartitionTableId, IReadOnlyList<VolumeInfo> Volumes);

/// <summary>A USB device that isn't working: a problem code as in Device Manager (e.g. 43), or 0 for USB storage that never produced a disk.</summary>
public sealed record ProblemDevice(string InstanceId, string Name, int ProblemCode);

/// <summary>
/// Finds the disks FlashProof may show (design §5.1). Candidates come from Windows' device list alone: a disk is one
/// only if it's on USB (USB storage, or any device below a USB device) or on an SD host. Nothing else, the computer's
/// own disks included, is ever opened or sent anything. Only <see cref="Inspect"/> opens a disk, and only a candidate.
/// </summary>
public static class DeviceScan
{
    internal const int LayoutSize = 48 + 144 * 256;
    const int StorageDeviceProperty = 0;

    public static IReadOnlyList<DiskCandidate> FindCandidates()
    {
        var candidates = new List<DiskCandidate>();
        foreach (string path in Pnp.Interfaces(Native.DiskInterface))
        {
            if (Pnp.InterfaceStringProperty(path, Native.DeviceInstanceId) is not { } id || Pnp.Locate(id) is not { } node)
                continue;
            if (!IsCandidate(Pnp.SelfAndAncestors(node)))
                continue;
            string name = Pnp.StringProperty(node, Native.DeviceFriendlyName) ?? Pnp.StringProperty(node, Native.DeviceDescription) ?? id;
            candidates.Add(new DiskCandidate(id, path, name));
        }
        return candidates;
    }

    /// <summary>
    /// True for a disk on USB or an SD host, from its instance ID and those of the devices above it (nearest first).
    /// Built-in PCIe card readers don't qualify yet: what they report is spike 3 (design §8).
    /// </summary>
    internal static bool IsCandidate(IEnumerable<string> selfAndAncestors)
    {
        bool self = true;
        foreach (string id in selfAndAncestors)
        {
            if (self && (id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"SD\", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                return true;
            self = false;
        }
        return false;
    }

    /// <summary>
    /// Opens a candidate disk (with no read or write access) and its partitions, and reads what eligibility needs.
    /// May block on a failing device; the worker runs it with a timeout (design §4).
    /// </summary>
    /// <exception cref="StorageIoException">The disk can't be opened or doesn't answer.</exception>
    public static DiskInfo Inspect(DiskCandidate candidate)
    {
        using var disk = Io.Open(candidate.InterfacePath, Native.NoAccess, Native.ShareAll, Native.OpenExisting, 0);
        int number = DiskStructures.DeviceNumber(Io.Control(disk, Native.IoctlStorageGetDeviceNumber, null, 12));
        var descriptor = ReadDescriptor(disk);
        var arrival = Pnp.Locate(candidate.InstanceId) is { } node ? Pnp.FileTimeProperty(node, Native.DeviceLastArrivalDate) : null;

        int media = Io.ControlError(disk, Native.IoctlStorageCheckVerify2, 4);
        if (media is Native.ErrorNotReady or Native.ErrorNoMediaInDrive)
            return new DiskInfo(candidate, number, descriptor, arrival, false, 0, 0, false, "", []);
        if (media != 0)
            throw Win32Errors.FromCode(media);

        var (size, sectorSize) = DiskStructures.ParseGeometry(Io.Control(disk, Native.IoctlDiskGetDriveGeometryEx, null, 256));
        bool writeProtected = Io.ControlError(disk, Native.IoctlDiskIsWritable) == Native.ErrorWriteProtect;
        var layout = Io.Control(disk, Native.IoctlDiskGetDriveLayoutEx, null, LayoutSize);
        using var mountManager = Io.Open(@"\\.\MountPointManager", Native.NoAccess, Native.ShareAll, Native.OpenExisting, 0);
        var volumes = new List<VolumeInfo>();
        foreach (var (partition, _, _) in DiskStructures.Partitions(layout))
        {
            if (ReadVolume(mountManager, number, partition) is { } volume)
                volumes.Add(volume);
        }
        return new DiskInfo(candidate, number, descriptor, arrival, true, size, sectorSize, writeProtected, DiskStructures.PartitionTableId(layout), volumes);
    }

    /// <summary>USB devices with a problem code, and USB storage devices that never produced a disk. From the device list only.</summary>
    public static IReadOnlyList<ProblemDevice> FindProblemDevices()
    {
        var problems = new List<ProblemDevice>();
        foreach (string enumerator in new[] { "USB", "USBSTOR" })
        {
            foreach (string id in Pnp.DevicesOf(enumerator))
            {
                if (Pnp.Locate(id) is not { } node)
                    continue;
                int problem = Pnp.ProblemOf(node);
                bool storage = Pnp.StringProperty(node, Native.DeviceService) is { } service
                    && (service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) || service.Equals("UASPStor", StringComparison.OrdinalIgnoreCase));
                if (problem != 0 || (storage && !Pnp.HasChild(node)))
                    problems.Add(new ProblemDevice(id, Pnp.StringProperty(node, Native.DeviceFriendlyName) ?? Pnp.StringProperty(node, Native.DeviceDescription) ?? id, problem));
            }
        }
        return problems;
    }

    internal static DeviceDescriptor ReadDescriptor(SafeFileHandle disk) =>
        DiskStructures.ParseDeviceDescriptor(Io.Control(disk, Native.IoctlStorageQueryProperty, DiskStructures.PropertyQuery(StorageDeviceProperty), 4096));

    /// <summary>
    /// The volume on partition M of disk N, or null if there is none (as for a Microsoft reserved partition). Asks the
    /// mount manager about this one volume only.
    /// </summary>
    static VolumeInfo? ReadVolume(SafeFileHandle mountManager, int diskNumber, int partition)
    {
        string deviceName;
        using (var handle = Native.CreateFile($@"\\?\GLOBALROOT\Device\Harddisk{diskNumber}\Partition{partition}", Native.NoAccess, Native.ShareAll, 0, Native.OpenExisting, 0, 0))
        {
            if (handle.IsInvalid || Io.TryControl(handle, Native.IoctlMountdevQueryDeviceName, null, 1024) is not { } name)
                return null;
            deviceName = DiskStructures.MountDeviceName(name);
        }
        var links = DiskStructures.MountPointLinks(Io.Control(mountManager, Native.IoctlMountmgrQueryPoints, DiskStructures.MountPointQuery(deviceName), 8192));
        if (links.FirstOrDefault(l => l.StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase)) is not { } guidLink)
            return null;
        string guidPath = @"\\?\" + guidLink[4..] + @"\";
        var letters = links.Where(l => l.Length == 14 && l.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase) && l[13] == ':').Select(l => l[12..].ToUpperInvariant()).ToList();

        int extents;
        long offset, length;
        using (var volume = Io.Open(guidPath[..^1], Native.NoAccess, Native.ShareAll, Native.OpenExisting, 0))
            (extents, _, offset, length) = DiskStructures.FirstDiskExtent(Io.Control(volume, Native.IoctlVolumeGetVolumeDiskExtents, null, 8 + 24 * 4));

        var label = new char[261];
        var fileSystem = new char[261];
        if (!Native.GetVolumeInformation(guidPath, label, (uint)label.Length, out uint serial, out _, out uint flags, fileSystem, (uint)fileSystem.Length))
            return new VolumeInfo(guidPath, deviceName, letters, extents, offset, length, "", "", 0, false, 0, Marshal.GetLastPInvokeError(), HasBitLockerSignature(guidPath[..^1]));
        long free = Native.GetDiskFreeSpaceEx(guidPath, out ulong available, out _, out _) ? (long)available : 0;
        return new VolumeInfo(guidPath, deviceName, letters, extents, offset, length, Text(fileSystem), Text(label), serial,
            (flags & Native.FileReadOnlyVolume) != 0, free, 0, false);
    }

    /// <summary>
    /// A locked BitLocker volume begins with "-FVE-FS-" where a file system's name would be. Reads its first 4 KiB,
    /// which Windows allows without admin rights on removable media. Untested: no BitLocker drive was at hand.
    /// </summary>
    static unsafe bool HasBitLockerSignature(string device)
    {
        const int Size = 4096;
        using var handle = Native.CreateFile(device, Native.GenericRead, Native.ShareAll, 0, Native.OpenExisting, Native.FlagNoBuffering, 0);
        if (handle.IsInvalid)
            return false;
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(Size, Size);
        try
        {
            var span = new Span<byte>(buffer, Size);
            return RandomAccess.Read(handle, span, 0) >= 11 && span.Slice(3, 8).SequenceEqual("-FVE-FS-"u8);
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    static string Text(char[] buffer) => new string(buffer).TrimEnd('\0');
}
