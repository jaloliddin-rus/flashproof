namespace FlashProof.Core;

/// <summary>
/// Raw sector access to a whole disk. The emulator implements it in M1; the Windows layer in M2.
/// Implementations throw <see cref="StorageIoException"/> for I/O failures.
/// </summary>
public interface IBlockDevice
{
    /// <summary>The size the disk reports, in bytes.</summary>
    long Size { get; }

    /// <summary>The logical sector size the disk reports, in bytes.</summary>
    int SectorSize { get; }

    /// <summary>Reads at a disk offset and returns the number of bytes actually read.</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);

    /// <summary>Writes at a disk offset and returns the number of bytes actually written.</summary>
    ValueTask<int> WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>
/// The mounted volume a full test writes its files to (design §6.2). Paths are relative to the volume root.
/// Implementations throw <see cref="StorageIoException"/> for I/O failures.
/// </summary>
public interface ITestVolume
{
    /// <summary>Size of the whole disk the volume is on, in bytes. Test coverage is measured against this.</summary>
    long DiskSize { get; }

    /// <summary>Where the volume starts on the disk, in bytes.</summary>
    long VolumeOffset { get; }

    /// <summary>Size of the volume, in bytes.</summary>
    long VolumeSize { get; }

    /// <summary>The logical sector size the disk reports, in bytes.</summary>
    int SectorSize { get; }

    /// <summary>Free space the file system reports, in bytes.</summary>
    long FreeSpace { get; }

    /// <summary>Reads the device and card identity again, through the already open handles (design §5.2).</summary>
    VolumeIdentity ReadIdentity();

    void CreateFolder(string path);

    /// <summary>Creates a new file for unbuffered, write-through reading and writing.</summary>
    ITestFile CreateFile(string path);

    /// <summary>Opens an existing file for unbuffered reading.</summary>
    ITestFile OpenFile(string path);

    void DeleteFile(string path);

    /// <summary>Deletes an empty folder.</summary>
    void DeleteFolder(string path);
}

/// <summary>An open test file. Offsets and lengths are whole sectors.</summary>
public interface ITestFile : IDisposable
{
    ValueTask<int> WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);

    void Flush();

    /// <summary>Where the file lies on the disk, as disk offsets (design §6.1). Empty if the platform can't tell; then there is no wrap analysis.</summary>
    IReadOnlyList<FileExtent> GetExtents();
}

/// <summary>A contiguous part of a file: <see cref="Length"/> bytes at file offset <see cref="FileOffset"/> lie at disk offset <see cref="DiskOffset"/>.</summary>
public readonly record struct FileExtent(long FileOffset, long DiskOffset, long Length);

public enum IoErrorKind
{
    Other,
    DiskFull,
    WriteProtected,
    FileSystemCorrupt,
    DeviceRemoved,
}

/// <summary>An I/O failure, with the platform's own error code (a Win32 code on Windows).</summary>
public sealed class StorageIoException(IoErrorKind kind, int nativeCode, string message) : IOException(message)
{
    public IoErrorKind Kind { get; } = kind;

    public int NativeCode { get; } = nativeCode;
}

/// <summary>
/// Everything that identifies the device and the card in it (design §5.2). Disk numbers and drive letters are
/// never part of it. Any difference from the confirmed identity means the drive or card changed.
/// </summary>
public sealed record VolumeIdentity(
    string DeviceInstanceId,
    string VendorProduct,
    string Serial,
    long DiskSize,
    int LogicalSectorSize,
    int PhysicalSectorSize,
    DateTimeOffset LastArrival,
    long MediaChangeCount,
    string PartitionTableId,
    string VolumeGuidPath,
    uint VolumeSerial)
{
    /// <summary>
    /// The same drive or card after being unplugged and plugged back in (design §5.2): vendor/product, serial, size,
    /// partition table ID and volume serial match. The plug-in time and media-change count change on a replug, and
    /// the instance ID and volume path may.
    /// </summary>
    public bool SameDriveAs(VolumeIdentity other) =>
        VendorProduct == other.VendorProduct && Serial == other.Serial && DiskSize == other.DiskSize
        && PartitionTableId == other.PartitionTableId && VolumeSerial == other.VolumeSerial;
}
