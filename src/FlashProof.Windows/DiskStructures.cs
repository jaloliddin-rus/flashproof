using System.Buffers.Binary;
using System.Text;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>What STORAGE_DEVICE_DESCRIPTOR says about a disk (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceProperty).</summary>
public sealed record DeviceDescriptor(string Vendor, string Product, string Revision, string Serial, int BusType, bool RemovableMedia)
{
    public const int BusTypeUsb = 7, BusTypeSd = 12, BusTypeMmc = 13;

    public string VendorProduct => $"{Vendor} {Product}".Trim();
}

/// <summary>Reads the fixed-layout structures that Windows storage control codes return. All little-endian, x64 and arm64 layouts.</summary>
internal static class DiskStructures
{
    /// <summary>
    /// Adds the runs of a RETRIEVAL_POINTERS_BUFFER (FSCTL_GET_RETRIEVAL_POINTERS) as disk extents and returns the VCN
    /// after the last run, where the next call starts when Windows said ERROR_MORE_DATA. Runs with no disk position
    /// (LCN −1, as in sparse files) are skipped.
    /// </summary>
    /// <param name="lcnZero">Disk offset of cluster 0 of the file area: partition offset + retrieval pointer base.</param>
    public static long AddRetrievalPointers(ReadOnlySpan<byte> buffer, long lcnZero, long clusterSize, List<FileExtent> extents)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        long vcn = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]);
        for (int i = 0; i < count && 32 + 16 * i <= buffer.Length; i++)
        {
            long next = BinaryPrimitives.ReadInt64LittleEndian(buffer[(16 + 16 * i)..]);
            long lcn = BinaryPrimitives.ReadInt64LittleEndian(buffer[(24 + 16 * i)..]);
            if (lcn >= 0)
                extents.Add(new FileExtent(vcn * clusterSize, lcnZero + lcn * clusterSize, (next - vcn) * clusterSize));
            vcn = next;
        }
        return vcn;
    }

    /// <summary>The number of disk extents in a VOLUME_DISK_EXTENTS, and the first one.</summary>
    public static (int Count, int DiskNumber, long Offset, long Length) FirstDiskExtent(ReadOnlySpan<byte> buffer) =>
        (BinaryPrimitives.ReadInt32LittleEndian(buffer),
         BinaryPrimitives.ReadInt32LittleEndian(buffer[8..]),
         BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]),
         BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]));

    public static DeviceDescriptor ParseDeviceDescriptor(ReadOnlySpan<byte> buffer) => new(
        AsciiAt(buffer, BinaryPrimitives.ReadInt32LittleEndian(buffer[12..])),
        AsciiAt(buffer, BinaryPrimitives.ReadInt32LittleEndian(buffer[16..])),
        AsciiAt(buffer, BinaryPrimitives.ReadInt32LittleEndian(buffer[20..])),
        AsciiAt(buffer, BinaryPrimitives.ReadInt32LittleEndian(buffer[24..])),
        BinaryPrimitives.ReadInt32LittleEndian(buffer[28..]),
        buffer[10] != 0);

    /// <summary>Disk size and logical sector size from a DISK_GEOMETRY_EX.</summary>
    public static (long DiskSize, int BytesPerSector) ParseGeometry(ReadOnlySpan<byte> buffer) =>
        (BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]), BinaryPrimitives.ReadInt32LittleEndian(buffer[20..]));

    /// <summary>Logical and physical sector sizes from a STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR.</summary>
    public static (int Logical, int Physical) ParseAccessAlignment(ReadOnlySpan<byte> buffer) =>
        (BinaryPrimitives.ReadInt32LittleEndian(buffer[16..]), BinaryPrimitives.ReadInt32LittleEndian(buffer[20..]));

    /// <summary>
    /// The partition table's ID from a DRIVE_LAYOUT_INFORMATION_EX: "MBR:" and the disk signature, "GPT:" and the disk
    /// GUID, or "RAW" for a disk with no partition table. A card swapped in the same reader almost always changes it.
    /// </summary>
    public static string PartitionTableId(ReadOnlySpan<byte> buffer) => BinaryPrimitives.ReadInt32LittleEndian(buffer) switch
    {
        0 => $"MBR:{BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]):X8}",
        1 => $"GPT:{new Guid(buffer.Slice(8, 16)):B}",
        _ => "RAW",
    };

    /// <summary>
    /// The partitions in a DRIVE_LAYOUT_INFORMATION_EX that Windows numbered (empty MBR slots and extended-partition
    /// containers have number 0). Each numbered partition is <c>\Device\HarddiskN\PartitionM</c>.
    /// </summary>
    public static IReadOnlyList<(int Number, long Offset, long Length)> Partitions(ReadOnlySpan<byte> buffer)
    {
        const int Header = 48, Entry = 144;
        var partitions = new List<(int, long, long)>();
        int count = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        for (int i = 0; i < count && Header + Entry * (i + 1) <= buffer.Length; i++)
        {
            var entry = buffer.Slice(Header + Entry * i, Entry);
            int number = BinaryPrimitives.ReadInt32LittleEndian(entry[24..]);
            long length = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (number > 0 && length > 0)
                partitions.Add((number, BinaryPrimitives.ReadInt64LittleEndian(entry[8..]), length));
        }
        return partitions;
    }

    /// <summary>The disk number in a STORAGE_DEVICE_NUMBER: N in <c>\\.\PhysicalDriveN</c>.</summary>
    public static int DeviceNumber(ReadOnlySpan<byte> buffer) => BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);

    /// <summary>The device name in a MOUNTDEV_NAME, such as <c>\Device\HarddiskVolume7</c>.</summary>
    public static string MountDeviceName(ReadOnlySpan<byte> buffer) =>
        Encoding.Unicode.GetString(buffer.Slice(2, BinaryPrimitives.ReadUInt16LittleEndian(buffer)));

    /// <summary>The input of IOCTL_MOUNTMGR_QUERY_POINTS asking for one device's mount points only.</summary>
    public static byte[] MountPointQuery(string deviceName)
    {
        const int Header = 24;
        var input = new byte[Header + deviceName.Length * 2];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(16), Header);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(20), (ushort)(deviceName.Length * 2));
        Encoding.Unicode.GetBytes(deviceName).CopyTo(input, Header);
        return input;
    }

    /// <summary>The symbolic links in a MOUNTMGR_MOUNT_POINTS, such as <c>\??\Volume{GUID}</c> and <c>\DosDevices\E:</c>.</summary>
    public static IReadOnlyList<string> MountPointLinks(ReadOnlySpan<byte> buffer)
    {
        var links = new List<string>();
        int count = BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]);
        for (int i = 0; i < count && 8 + 24 * (i + 1) <= buffer.Length; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(buffer[(8 + 24 * i)..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(12 + 24 * i)..]);
            if (length > 0 && offset + length <= buffer.Length)
                links.Add(Encoding.Unicode.GetString(buffer.Slice(offset, length)));
        }
        return links;
    }

    /// <summary>The input of IOCTL_STORAGE_QUERY_PROPERTY: a standard query for one property.</summary>
    public static byte[] PropertyQuery(int propertyId)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        return query;
    }

    static string AsciiAt(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
            return "";
        var text = buffer[offset..];
        int end = text.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
    }
}
