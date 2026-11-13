using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FlashProof.Core;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>
/// A mounted volume on a removable disk, as the full test's <see cref="ITestVolume"/> (design §6.2). Needs no admin
/// rights. Every path goes through the volume GUID path, never a drive letter, so a letter reassigned to another
/// device can't redirect writes (design §5.2). The identity is re-read through the handles opened here.
/// </summary>
public sealed class WindowsTestVolume : ITestVolume, IDisposable
{
    const int StorageAccessAlignmentProperty = 6;
    const int RunsPerCall = 256;

    readonly string root, instanceId;
    readonly SafeFileHandle rootFolder, disk;

    /// <summary>Disk offset of the file system's cluster 0, or −1 if Windows won't tell (then files have no known extents).</summary>
    readonly long lcnZero;
    readonly long clusterSize;

    WindowsTestVolume(string root, string instanceId, SafeFileHandle rootFolder, SafeFileHandle disk, long volumeOffset, long volumeSize, long lcnZero, long clusterSize)
    {
        this.root = root;
        this.instanceId = instanceId;
        this.rootFolder = rootFolder;
        this.disk = disk;
        VolumeOffset = volumeOffset;
        VolumeSize = volumeSize;
        this.lcnZero = lcnZero;
        this.clusterSize = clusterSize;
        (DiskSize, SectorSize) = DiskStructures.ParseGeometry(Io.Control(disk, Native.IoctlDiskGetDriveGeometryEx, null, 256));
    }

    /// <summary>Opens a volume for testing, checking that it lies on the given device.</summary>
    /// <param name="volumeGuidPath">The volume's GUID path, <c>\\?\Volume{GUID}\</c>.</param>
    /// <param name="deviceInstanceId">The disk's PnP instance ID, as <see cref="DeviceScan"/> found it.</param>
    /// <exception cref="StorageIoException">The volume or disk can't be opened, the volume spans more than one disk, or it isn't on that device.</exception>
    public static WindowsTestVolume Open(string volumeGuidPath, string deviceInstanceId)
    {
        if (!IsVolumeGuidPath(volumeGuidPath))
            throw new ArgumentException(@"Expected a volume GUID path: \\?\Volume{GUID}\", nameof(volumeGuidPath));
        string device = volumeGuidPath[..^1];

        int diskNumber;
        long volumeOffset, volumeSize;
        using (var volume = Io.Open(device, Native.NoAccess, Native.ShareAll, Native.OpenExisting, 0))
        {
            var (count, number, offset, length) = DiskStructures.FirstDiskExtent(Io.Control(volume, Native.IoctlVolumeGetVolumeDiskExtents, null, 8 + 24 * 4));
            if (count != 1)
                throw new StorageIoException(IoErrorKind.Other, 0, "The volume spans more than one disk.");
            (diskNumber, volumeOffset, volumeSize) = (number, offset, length);
        }

        if (!Native.GetDiskFreeSpace(volumeGuidPath, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            throw Win32Errors.Last();
        long lcnZero = FileAreaStart(device, volumeOffset, bytesPerSector);

        var rootFolder = Io.Open(volumeGuidPath, Native.FileReadAttributes, Native.ShareAll, Native.OpenExisting, Native.FlagBackupSemantics);
        SafeFileHandle? disk = null;
        try
        {
            // Through the device's own interface, not \\.\PhysicalDriveN: disk numbers can be reused by another device.
            string diskInterface = Pnp.Interfaces(Native.DiskInterface, deviceInstanceId).FirstOrDefault()
                ?? throw new StorageIoException(IoErrorKind.DeviceRemoved, 0, "The device is no longer in Windows' device list.");
            disk = Io.Open(diskInterface, Native.NoAccess, Native.ShareAll, Native.OpenExisting, 0);
            if (DiskStructures.DeviceNumber(Io.Control(disk, Native.IoctlStorageGetDeviceNumber, null, 12)) != diskNumber)
                throw new StorageIoException(IoErrorKind.Other, 0, "The volume isn't on that device.");
            return new WindowsTestVolume(volumeGuidPath, deviceInstanceId, rootFolder, disk, volumeOffset, volumeSize, lcnZero, (long)sectorsPerCluster * bytesPerSector);
        }
        catch
        {
            rootFolder.Dispose();
            disk?.Dispose();
            throw;
        }
    }

    public long DiskSize { get; }
    public long VolumeOffset { get; }
    public long VolumeSize { get; }
    public int SectorSize { get; }

    public long FreeSpace => Native.GetDiskFreeSpaceEx(root, out ulong free, out _, out _) ? (long)free : throw Win32Errors.Last();

    /// <summary>
    /// Reads what identifies the device and the card (design §5.2): through the open handles, plus the arrival time
    /// from Windows' device list, which changes when the device is plugged in again.
    /// </summary>
    public VolumeIdentity ReadIdentity()
    {
        var device = DeviceScan.ReadDescriptor(disk);
        var (diskSize, logicalSector) = DiskStructures.ParseGeometry(Io.Control(disk, Native.IoctlDiskGetDriveGeometryEx, null, 256));
        int physicalSector = Io.TryControl(disk, Native.IoctlStorageQueryProperty, DiskStructures.PropertyQuery(StorageAccessAlignmentProperty), 64) is { } alignment
            ? DiskStructures.ParseAccessAlignment(alignment).Physical
            : logicalSector;
        long mediaChanges = BinaryPrimitives.ReadUInt32LittleEndian(Io.Control(disk, Native.IoctlStorageCheckVerify2, null, 4));
        string partitionTable = DiskStructures.PartitionTableId(Io.Control(disk, Native.IoctlDiskGetDriveLayoutEx, null, DeviceScan.LayoutSize));
        if (!Native.GetVolumeInformationByHandle(rootFolder, 0, 0, out uint volumeSerial, out _, out _, 0, 0))
            throw Win32Errors.Last();
        var node = Pnp.Locate(instanceId) ?? throw new StorageIoException(IoErrorKind.DeviceRemoved, 0, "The device is no longer in Windows' device list.");

        return new VolumeIdentity(
            DeviceInstanceId: instanceId,
            VendorProduct: device.VendorProduct,
            Serial: device.Serial,
            DiskSize: diskSize,
            LogicalSectorSize: logicalSector,
            PhysicalSectorSize: physicalSector,
            LastArrival: Pnp.FileTimeProperty(node, Native.DeviceLastArrivalDate) ?? default,
            MediaChangeCount: mediaChanges,
            PartitionTableId: partitionTable,
            VolumeGuidPath: root,
            VolumeSerial: volumeSerial);
    }

    public void CreateFolder(string path)
    {
        if (!Native.CreateDirectory(FullPath(path), 0))
            throw Win32Errors.Last();
    }

    /// <summary>Creates a new file, never replacing one, for unbuffered, write-through, overlapped I/O (design §6.2).</summary>
    public ITestFile CreateFile(string path) => new WindowsTestFile(this, Io.Open(FullPath(path),
        Native.GenericRead | Native.GenericWrite, Native.ShareRead, Native.CreateNew, Native.FlagNoBuffering | Native.FlagWriteThrough | Native.FlagOverlapped));

    public ITestFile OpenFile(string path) => new WindowsTestFile(this, Io.Open(FullPath(path),
        Native.GenericRead, Native.ShareRead, Native.OpenExisting, Native.FlagNoBuffering | Native.FlagOverlapped));

    public void DeleteFile(string path)
    {
        if (!Native.DeleteFile(FullPath(path)))
            throw Win32Errors.Last();
    }

    public void DeleteFolder(string path)
    {
        if (!Native.RemoveDirectory(FullPath(path)))
            throw Win32Errors.Last();
    }

    public void Dispose()
    {
        rootFolder.Dispose();
        disk.Dispose();
    }

    /// <summary>The handles this volume keeps open for the whole test: the volume's root folder and the disk.</summary>
    internal IEnumerable<SafeFileHandle> Handles => [rootFolder, disk];

    /// <summary>
    /// Where a file lies on the disk (design §6.1), from FSCTL_GET_RETRIEVAL_POINTERS: disk offset = partition offset +
    /// retrieval pointer base + LCN × cluster size, as proved on exFAT and FAT by spike 1. Empty when Windows won't tell.
    /// </summary>
    internal IReadOnlyList<FileExtent> ExtentsOf(SafeFileHandle file, int runsPerCall = RunsPerCall)
    {
        var extents = new List<FileExtent>();
        if (lcnZero < 0)
            return extents;
        // A second, synchronous handle: control calls on the overlapped one would need an OVERLAPPED of their own.
        using var query = Native.ReOpenFile(file, Native.FileReadAttributes, Native.ShareAll, 0);
        if (query.IsInvalid)
            throw Win32Errors.Last();

        var input = new byte[8];
        var output = new byte[16 + 16 * runsPerCall];
        while (true)
        {
            bool ok = Native.DeviceIoControl(query, Native.FsctlGetRetrievalPointers, input, input.Length, output, output.Length, out int returned, 0);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (error == Native.ErrorHandleEof)
                return extents; // the file has no clusters yet
            if (error == Native.ErrorAccessDenied)
                return []; // what NTFS answered on a fixed disk in spike 1; removable NTFS is untested
            if (error != 0 && error != Native.ErrorMoreData)
                throw Win32Errors.FromCode(error);

            long start = BinaryPrimitives.ReadInt64LittleEndian(input);
            long next = DiskStructures.AddRetrievalPointers(output.AsSpan(0, returned), lcnZero, clusterSize, extents);
            if (ok)
                return extents;
            if (next <= start)
                throw Win32Errors.FromCode(error); // no progress: don't loop forever
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
        }
    }

    string FullPath(string path) => PathInside(root, path);

    /// <summary>A path inside the volume, refusing anything that could lead outside the folder the runner names (design §5.3).</summary>
    internal static string PathInside(string root, string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':') || path.Split('\\', '/').Any(s => s is "" or "." or ".."))
            throw new ArgumentException($"Not a plain path inside the volume: {path}", nameof(path));
        return root + path;
    }

    internal static bool IsVolumeGuidPath(string path) =>
        path.Length == 49 && path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.EndsWith(@"}\", StringComparison.Ordinal)
        && Guid.TryParse(path.AsSpan(10, 38), out _);

    /// <summary>
    /// Disk offset of cluster 0, from FSCTL_GET_RETRIEVAL_POINTER_BASE. That works only on a volume handle opened for
    /// reading, which Windows allows without admin rights on removable media (spike 1). −1 if refused.
    /// </summary>
    static long FileAreaStart(string device, long volumeOffset, uint bytesPerSector)
    {
        using var volume = Native.CreateFile(device, Native.GenericRead, Native.ShareAll, 0, Native.OpenExisting, 0, 0);
        int error = volume.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        var output = new byte[8];
        if (error == 0 && !Native.DeviceIoControl(volume, Native.FsctlGetRetrievalPointerBase, null, 0, output, output.Length, out _, 0))
            error = Marshal.GetLastPInvokeError();
        return error switch
        {
            0 => volumeOffset + BinaryPrimitives.ReadInt64LittleEndian(output) * bytesPerSector,
            Native.ErrorAccessDenied or Native.ErrorInvalidFunction or Native.ErrorNotSupported => -1,
            _ => throw Win32Errors.FromCode(error),
        };
    }
}
