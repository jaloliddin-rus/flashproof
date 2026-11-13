using FlashProof.Core;

namespace FlashProof.Tests.Emulator;

public enum FlashKind
{
    /// <summary>Stores everything it's given.</summary>
    Genuine,

    /// <summary>Disk offset p is stored at p mod <see cref="SimulatedDevice.RealCapacity"/>, overwriting earlier data.</summary>
    Wraparound,

    /// <summary>Writes at or beyond <see cref="SimulatedDevice.RealCapacity"/> are dropped; reads return what the flash held.</summary>
    Limbo,

    /// <summary>Like <see cref="Limbo"/>, but the most recent <see cref="SimulatedDevice.CacheBytes"/> beyond capacity are held in RAM.</summary>
    RamCache,
}

/// <summary>
/// An in-memory USB drive or card for the emulator suite (design §8): a disk with a simple FAT-like volume on it,
/// whose flash is genuine or one of the common fakes, with optional faults. Only written flash takes memory.
/// </summary>
public sealed class SimulatedDevice : IBlockDevice, ITestVolume
{
    const int Sector = 512;

    readonly int clusterSize;
    readonly long dataStart;
    readonly bool[] clusterUsed;
    readonly Dictionary<string, SimFile> files = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<(long Sector, byte[] Data)> cacheOrder = new();
    readonly Dictionary<long, LinkedListNode<(long Sector, byte[] Data)>> cache = new();
    readonly TaskCompletionSource hangReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource hangRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    SparseStore store;
    VolumeIdentity identity;
    int clustersUsed, firstFreeCluster;
    bool connected = true, swapped, hangReleased;

    /// <param name="volumeSize">Defaults to the rest of the disk; set it larger to make the volume run past the disk's end.</param>
    /// <param name="erasedByte">What never-written flash reads as.</param>
    public SimulatedDevice(long diskSize, long? volumeSize = null, int sectorSize = Sector, long volumeOffset = 256 << 10,
        long metadataBytes = 256 << 10, int clusterSize = 32 << 10, byte erasedByte = 0x00)
    {
        DiskSize = diskSize;
        SectorSize = sectorSize;
        VolumeOffset = volumeOffset;
        VolumeSize = volumeSize ?? diskSize - volumeOffset;
        this.clusterSize = clusterSize;
        dataStart = volumeOffset + metadataBytes;
        clusterUsed = new bool[(VolumeSize - metadataBytes) / clusterSize];
        store = new SparseStore(erasedByte);
        identity = new VolumeIdentity(@"USBSTOR\DISK&VEN_SIM&PROD_FLASH\0001", "SIM Flash", "0001", diskSize, sectorSize, sectorSize,
            new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), 1, "MBR:5EED5EED", @"\\?\Volume{5eed0000-0000-0000-0000-000000000001}\", 0x5EED0001);
    }

    // ---- What the flash really is

    public FlashKind Flash { get; init; }

    /// <summary>Wrap size, or where a limbo or RAM-cache fake stops storing data.</summary>
    public long RealCapacity { get; init; }

    public long CacheBytes { get; init; }

    // ---- Faults

    /// <summary>Disk byte offsets whose lowest bit flips when written.</summary>
    public HashSet<long> BitFlips { get; } = [];

    /// <summary>Disk ranges that fail to read with a CRC error.</summary>
    public List<DiskRange> UnreadableRanges { get; } = [];

    /// <summary>Advanced by the time each I/O takes on a slow drive.</summary>
    public ManualClock? Clock { get; init; }

    public double? WriteBytesPerSecond { get; init; }
    public double? ReadBytesPerSecond { get; init; }

    /// <summary>The first I/O touching this disk offset never completes on its own.</summary>
    public long? HangAt { get; init; }

    /// <summary>Whether a hung I/O ends when it's cancelled. If not, it ends only on <see cref="ReleaseHang"/>.</summary>
    public bool HangHonoursCancellation { get; init; }

    public long? DisconnectAfterBytesWritten { get; init; }

    /// <summary>
    /// After this much is written, writes fail as when Windows' file system drops the volume (error 1006), while the
    /// drive stays connected; seen on a FAT32 stick after a retried I/O (13 Nov 2026).
    /// </summary>
    public long? VolumeLostAfterBytesWritten { get; init; }

    public long? SwapCardAfterBytesWritten { get; init; }
    public bool IsWriteProtected { get; init; }

    /// <summary>The real sector size, for a drive that reports a smaller one: I/O not aligned to it fails.</summary>
    public int RequiredAlignment { get; init; }

    /// <summary>A write touching this disk offset transfers only half its bytes.</summary>
    public long? ShortTransferAt { get; init; }

    /// <summary>Free space the file system reports beyond what really fits.</summary>
    public long ExtraReportedFreeSpace { get; init; }

    // ---- Observations

    public long BytesWritten { get; private set; }

    /// <summary>False once unplugged or disconnected, until plugged back in.</summary>
    public bool IsConnected => connected;

    /// <summary>I/O attempted after the device disconnected.</summary>
    public int IoAfterDisconnect { get; private set; }

    public Task HangReached => hangReached.Task;

    public IReadOnlyCollection<string> FileNames => files.Keys;

    public IReadOnlyCollection<string> FolderNames => folders;

    // ---- Setup and inspection

    /// <summary>Creates a file with random content before the test, as the user's own file. Returns the content.</summary>
    public byte[] AddExistingFile(string path, long size, int seed = 1)
    {
        var file = new SimFile { Size = size };
        files[path] = file;
        Allocate(file, size);
        var content = new byte[size];
        new Random(seed).NextBytes(content);
        long done = 0;
        foreach (var (disk, length) in Pieces(file, 0, size))
        {
            FlashWrite(disk, content.AsSpan((int)done, (int)length));
            done += length;
        }
        return content;
    }

    /// <summary>Reads a file's content straight from the disk, through <see cref="IBlockDevice"/>.</summary>
    public async Task<byte[]> ReadFileFromDiskAsync(string path)
    {
        var file = files[path];
        var content = new byte[file.Size];
        long done = 0;
        foreach (var (disk, length) in Pieces(file, 0, file.Size))
        {
            await ReadAsync(disk, content.AsMemory((int)done, (int)length), CancellationToken.None);
            done += length;
        }
        return content;
    }

    /// <summary>Puts records of an older session on the flash, as a previous test would have left them.</summary>
    public void FillWithStaleRecords(ulong oldSeed, long offset, long length)
    {
        var data = new byte[length];
        TestRecord.Fill(data, oldSeed, TestRecord.SessionTag(oldSeed), offset / Sector);
        store.Write(offset, data);
    }

    /// <summary>Replaces the card in the reader with a blank one.</summary>
    public void SwapCard()
    {
        swapped = true;
        store = new SparseStore(store.Erased);
        identity = identity with { MediaChangeCount = identity.MediaChangeCount + 1, VolumeSerial = identity.VolumeSerial + 1 };
    }

    /// <summary>Unplugs the drive: I/O fails until it's plugged back in, and a RAM cache loses what it held.</summary>
    public void Unplug()
    {
        connected = false;
        cache.Clear();
        cacheOrder.Clear();
    }

    /// <summary>Plugs it back in: Windows sees a new arrival, and the reader counts a media change.</summary>
    public void PlugIn()
    {
        connected = true;
        identity = identity with { LastArrival = identity.LastArrival.AddMinutes(1), MediaChangeCount = identity.MediaChangeCount + 1 };
    }

    /// <summary>Lets a hang that ignores cancellation end, as if the drive were then unplugged.</summary>
    public void ReleaseHang()
    {
        hangReleased = true;
        hangRelease.TrySetResult();
    }

    // ---- ITestVolume

    public long DiskSize { get; }
    public long VolumeOffset { get; }
    public long VolumeSize { get; }
    public int SectorSize { get; }
    public long FreeSpace => (long)(clusterUsed.Length - clustersUsed) * clusterSize + ExtraReportedFreeSpace;

    public VolumeIdentity ReadIdentity()
    {
        BeginIo();
        return identity;
    }

    public void CreateFolder(string path)
    {
        BeginIo();
        if (IsWriteProtected)
            throw WriteProtectedError();
        folders.Add(path);
    }

    public ITestFile CreateFile(string path)
    {
        BeginIo();
        if (IsWriteProtected)
            throw WriteProtectedError();
        string folder = Path.GetDirectoryName(path) ?? "";
        if (folder.Length > 0 && !folders.Contains(folder))
            throw new StorageIoException(IoErrorKind.Other, 3, "The system cannot find the path specified.");
        var file = new SimFile();
        files[path] = file;
        return new Handle(this, file, writable: true);
    }

    public ITestFile OpenFile(string path)
    {
        BeginIo();
        if (!files.TryGetValue(path, out var file))
            throw new StorageIoException(IoErrorKind.Other, 2, "The system cannot find the file specified.");
        return new Handle(this, file, writable: false);
    }

    public void DeleteFile(string path)
    {
        BeginIo();
        if (!files.Remove(path, out var file))
            throw new StorageIoException(IoErrorKind.Other, 2, "The system cannot find the file specified.");
        foreach (var extent in file.Extents)
        {
            for (long c = (extent.DiskOffset - dataStart) / clusterSize; c < (extent.DiskOffset - dataStart + extent.Length) / clusterSize; c++)
            {
                clusterUsed[c] = false;
                clustersUsed--;
                firstFreeCluster = (int)Math.Min(firstFreeCluster, c);
            }
        }
    }

    public void DeleteFolder(string path)
    {
        BeginIo();
        if (files.Keys.Any(k => Path.GetDirectoryName(k) == path))
            throw new StorageIoException(IoErrorKind.Other, 145, "The directory is not empty.");
        folders.Remove(path);
    }

    // ---- IBlockDevice

    long IBlockDevice.Size => DiskSize;

    public async ValueTask<int> WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        BeginIo();
        CheckAlignment(offset, data.Length);
        if (DisconnectAfterBytesWritten is { } limit && BytesWritten + data.Length > limit)
        {
            connected = false;
            throw Removed();
        }
        if (VolumeLostAfterBytesWritten is { } lost && BytesWritten + data.Length > lost)
            throw new StorageIoException(IoErrorKind.DeviceRemoved, 1006, "The volume for a file has been externally altered so that the opened file is no longer valid.");
        if (SwapCardAfterBytesWritten is { } swapAt && !swapped && BytesWritten >= swapAt)
            SwapCard();
        await HangIfAt(offset, data.Length, ct);
        return WriteNow(offset, data.Span);
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        BeginIo();
        CheckAlignment(offset, buffer.Length);
        await HangIfAt(offset, buffer.Length, ct);
        if (UnreadableRanges.Any(r => Overlaps(offset, buffer.Length, r.Offset, r.Length)))
            throw new StorageIoException(IoErrorKind.Other, 23, "Data error (cyclic redundancy check).");
        FlashRead(offset, buffer.Span);
        Elapse(buffer.Length, ReadBytesPerSecond);
        return buffer.Length;
    }

    // ---- Internals

    int WriteNow(long offset, ReadOnlySpan<byte> data)
    {
        if (ShortTransferAt is { } p && Overlaps(offset, data.Length, p, 1))
            data = data[..(data.Length / 2)];
        long end = offset + data.Length;
        if (BitFlips.Any(b => b >= offset && b < end))
        {
            var copy = data.ToArray();
            foreach (long b in BitFlips.Where(b => b >= offset && b < end))
                copy[b - offset] ^= 1;
            data = copy;
        }
        FlashWrite(offset, data);
        BytesWritten += data.Length;
        Elapse(data.Length, WriteBytesPerSecond);
        return data.Length;
    }

    void FlashWrite(long offset, ReadOnlySpan<byte> data)
    {
        if (Flash == FlashKind.Genuine)
        {
            store.Write(offset, data);
        }
        else if (Flash == FlashKind.Wraparound)
        {
            while (data.Length > 0)
            {
                long slot = offset % RealCapacity;
                int n = (int)Math.Min(data.Length, RealCapacity - slot);
                store.Write(slot, data[..n]);
                data = data[n..];
                offset += n;
            }
        }
        else
        {
            int stored = (int)Math.Clamp(RealCapacity - offset, 0, data.Length);
            store.Write(offset, data[..stored]);
            if (Flash == FlashKind.RamCache)
            {
                for (int i = stored; i < data.Length; i += Sector)
                    Cache((offset + i) / Sector, data.Slice(i, Sector));
            }
        }
    }

    void FlashRead(long offset, Span<byte> dest)
    {
        if (Flash == FlashKind.Wraparound)
        {
            while (dest.Length > 0)
            {
                long slot = offset % RealCapacity;
                int n = (int)Math.Min(dest.Length, RealCapacity - slot);
                store.Read(slot, dest[..n]);
                dest = dest[n..];
                offset += n;
            }
            return;
        }
        store.Read(offset, dest);
        if (Flash == FlashKind.RamCache)
        {
            for (int i = 0; i < dest.Length; i += Sector)
            {
                if (cache.TryGetValue((offset + i) / Sector, out var node))
                    node.Value.Data.CopyTo(dest.Slice(i, Sector));
            }
        }
    }

    void Cache(long sector, ReadOnlySpan<byte> data)
    {
        if (cache.Remove(sector, out var old))
            cacheOrder.Remove(old);
        cache[sector] = cacheOrder.AddLast((sector, data.ToArray()));
        while (cache.Count > CacheBytes / Sector)
        {
            cache.Remove(cacheOrder.First!.Value.Sector);
            cacheOrder.RemoveFirst();
        }
    }

    async ValueTask HangIfAt(long offset, int length, CancellationToken ct)
    {
        if (HangAt is not { } at || hangReleased || !Overlaps(offset, length, at, 1))
            return;
        hangReached.TrySetResult();
        if (HangHonoursCancellation)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        await hangRelease.Task;
        connected = false;
        throw Removed();
    }

    async ValueTask<int> FileWriteAsync(SimFile file, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        BeginIo();
        CheckAlignment(offset, data.Length);
        if (IsWriteProtected)
            throw WriteProtectedError();
        Allocate(file, offset + data.Length);
        int done = 0;
        foreach (var (disk, length) in Pieces(file, offset, data.Length))
        {
            int n = await WriteAsync(disk, data.Slice(done, (int)length), ct);
            done += n;
            if (n < length)
                break;
        }
        file.Size = Math.Max(file.Size, offset + done);
        return done;
    }

    async ValueTask<int> FileReadAsync(SimFile file, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        BeginIo();
        CheckAlignment(offset, buffer.Length);
        if (offset + buffer.Length > file.Size)
            throw new StorageIoException(IoErrorKind.Other, 38, "Reached the end of the file.");
        int done = 0;
        foreach (var (disk, length) in Pieces(file, offset, buffer.Length))
        {
            done += await ReadAsync(disk, buffer.Slice(done, (int)length), ct);
        }
        return done;
    }

    /// <summary>Grows a file to at least <paramref name="length"/>, taking the lowest free clusters first.</summary>
    void Allocate(SimFile file, long length)
    {
        long needed = (length - file.Allocated + clusterSize - 1) / clusterSize;
        if (needed <= 0)
            return;
        if (needed > clusterUsed.Length - clustersUsed)
            throw new StorageIoException(IoErrorKind.DiskFull, 112, "There is not enough space on the disk.");
        for (int c = firstFreeCluster; needed > 0; c++)
        {
            if (clusterUsed[c])
                continue;
            clusterUsed[c] = true;
            clustersUsed++;
            needed--;
            firstFreeCluster = c + 1;
            long disk = dataStart + (long)c * clusterSize;
            if (file.Extents.Count > 0 && file.Extents[^1].DiskOffset + file.Extents[^1].Length == disk)
                file.Extents[^1] = file.Extents[^1] with { Length = file.Extents[^1].Length + clusterSize };
            else
                file.Extents.Add(new FileExtent(file.Allocated, disk, clusterSize));
            file.Allocated += clusterSize;
        }
    }

    /// <summary>The disk ranges holding bytes offset … offset + length − 1 of a file.</summary>
    static IEnumerable<(long Disk, long Length)> Pieces(SimFile file, long offset, long length)
    {
        long end = offset + length;
        foreach (var e in file.Extents)
        {
            long from = Math.Max(offset, e.FileOffset), to = Math.Min(end, e.FileOffset + e.Length);
            if (from < to)
                yield return (e.DiskOffset + (from - e.FileOffset), to - from);
        }
    }

    void CheckAlignment(long offset, int length)
    {
        int unit = Math.Max(SectorSize, RequiredAlignment);
        if (offset % unit != 0 || length % unit != 0)
            throw new StorageIoException(IoErrorKind.Other, 87, "The parameter is incorrect.");
    }

    void BeginIo()
    {
        if (connected)
            return;
        IoAfterDisconnect++;
        throw Removed();
    }

    void Elapse(long bytes, double? bytesPerSecond)
    {
        if (bytesPerSecond is { } speed)
            Clock!.Advance(TimeSpan.FromSeconds(bytes / speed));
    }

    static bool Overlaps(long offset, long length, long otherOffset, long otherLength) =>
        otherOffset < offset + length && offset < otherOffset + otherLength;

    static StorageIoException Removed() => new(IoErrorKind.DeviceRemoved, 1167, "The device is not connected.");

    static StorageIoException WriteProtectedError() => new(IoErrorKind.WriteProtected, 19, "The media is write protected.");

    sealed class SimFile
    {
        public List<FileExtent> Extents { get; } = [];
        public long Allocated { get; set; }
        public long Size { get; set; }
    }

    sealed class Handle(SimulatedDevice device, SimFile file, bool writable) : ITestFile
    {
        public ValueTask<int> WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) =>
            writable ? device.FileWriteAsync(file, offset, data, ct) : throw new InvalidOperationException("The file is open read-only.");

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => device.FileReadAsync(file, offset, buffer, ct);

        public void Flush() => device.BeginIo();

        public IReadOnlyList<FileExtent> GetExtents()
        {
            device.BeginIo();
            return [.. file.Extents];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Flash contents, in 64 KiB pages allocated on first write.</summary>
    sealed class SparseStore(byte erased)
    {
        const int PageSize = 64 << 10;
        readonly Dictionary<long, byte[]> pages = new();

        public byte Erased => erased;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int within = (int)(offset % PageSize), n = Math.Min(PageSize - within, data.Length);
                if (!pages.TryGetValue(offset / PageSize, out var page))
                {
                    page = new byte[PageSize];
                    page.AsSpan().Fill(erased);
                    pages[offset / PageSize] = page;
                }
                data[..n].CopyTo(page.AsSpan(within));
                data = data[n..];
                offset += n;
            }
        }

        public void Read(long offset, Span<byte> dest)
        {
            while (dest.Length > 0)
            {
                int within = (int)(offset % PageSize), n = Math.Min(PageSize - within, dest.Length);
                if (pages.TryGetValue(offset / PageSize, out var page))
                    page.AsSpan(within, n).CopyTo(dest);
                else
                    dest[..n].Fill(erased);
                dest = dest[n..];
                offset += n;
            }
        }
    }
}
