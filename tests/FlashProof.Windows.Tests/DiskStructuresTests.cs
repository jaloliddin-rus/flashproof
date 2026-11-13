using System.Buffers.Binary;
using System.Text;
using FlashProof.Core;

namespace FlashProof.Windows.Tests;

// The layouts are those of the Windows SDK headers (winioctl.h) on x64 and arm64.
public class DiskStructuresTests
{
    const long Cluster = 128 << 10;
    const long LcnZero = (1 << 20) + 3704 * 512L; // the SanDisk's exFAT volume: partition at 1 MiB, cluster heap at sector 3704

    static byte[] RetrievalPointers(long startingVcn, params (long NextVcn, long Lcn)[] runs)
    {
        var buffer = new byte[16 + 16 * runs.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, runs.Length);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), startingVcn);
        for (int i = 0; i < runs.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16 + 16 * i), runs[i].NextVcn);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24 + 16 * i), runs[i].Lcn);
        }
        return buffer;
    }

    [Fact]
    public void Retrieval_pointers_become_disk_extents()
    {
        var extents = new List<FileExtent>();

        long next = DiskStructures.AddRetrievalPointers(RetrievalPointers(0, (8, 100), (10, 500)), LcnZero, Cluster, extents);

        Assert.Equal(10, next);
        Assert.Equal([new FileExtent(0, LcnZero + 100 * Cluster, 8 * Cluster), new FileExtent(8 * Cluster, LcnZero + 500 * Cluster, 2 * Cluster)], extents);
    }

    [Fact]
    public void A_continued_call_starts_at_its_starting_VCN()
    {
        var extents = new List<FileExtent>();

        long next = DiskStructures.AddRetrievalPointers(RetrievalPointers(10, (12, 7)), LcnZero, Cluster, extents);

        Assert.Equal(12, next);
        Assert.Equal(new FileExtent(10 * Cluster, LcnZero + 7 * Cluster, 2 * Cluster), Assert.Single(extents));
    }

    [Fact]
    public void Runs_with_no_disk_position_are_skipped()
    {
        var extents = new List<FileExtent>();

        long next = DiskStructures.AddRetrievalPointers(RetrievalPointers(0, (4, -1), (6, 9)), LcnZero, Cluster, extents);

        Assert.Equal(6, next);
        Assert.Equal(new FileExtent(4 * Cluster, LcnZero + 9 * Cluster, 2 * Cluster), Assert.Single(extents));
    }

    [Fact]
    public void Runs_beyond_the_returned_bytes_are_ignored()
    {
        var buffer = RetrievalPointers(0, (8, 100), (10, 500));
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 5); // claims more runs than it holds
        var extents = new List<FileExtent>();

        long next = DiskStructures.AddRetrievalPointers(buffer, LcnZero, Cluster, extents);

        Assert.Equal(10, next);
        Assert.Equal(2, extents.Count);
    }

    [Fact]
    public void Disk_extent_gives_disk_number_offset_and_length()
    {
        var buffer = new byte[8 + 24];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), 1 << 20);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), 61_496_000_000);

        Assert.Equal((1, 2, 1L << 20, 61_496_000_000L), DiskStructures.FirstDiskExtent(buffer));
    }

    [Fact]
    public void Device_descriptor_gives_strings_bus_type_and_removable_flag()
    {
        var buffer = new byte[256];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 256);
        buffer[10] = 1; // RemovableMedia
        void Text(int field, int offset, string text)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(field), offset);
            Encoding.ASCII.GetBytes(text).CopyTo(buffer, offset);
        }
        Text(12, 40, "SanDisk ");
        Text(16, 60, "3.2Gen1         ");
        Text(20, 90, "1.00");
        Text(24, 100, "0401abcdef");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), DeviceDescriptor.BusTypeUsb);

        var d = DiskStructures.ParseDeviceDescriptor(buffer);

        Assert.Equal(new DeviceDescriptor("SanDisk", "3.2Gen1", "1.00", "0401abcdef", DeviceDescriptor.BusTypeUsb, true), d);
    }

    [Fact]
    public void Missing_descriptor_strings_are_empty()
    {
        var buffer = new byte[64]; // every string offset is 0: not reported

        var d = DiskStructures.ParseDeviceDescriptor(buffer);

        Assert.Equal(("", "", ""), (d.Vendor, d.Product, d.Serial));
        Assert.False(d.RemovableMedia);
    }

    [Fact]
    public void Partition_table_ID_is_MBR_signature_or_GPT_disk_GUID()
    {
        var mbr = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(8), 0x5EED_0001);
        var gpt = new byte[48];
        gpt[0] = 1;
        var guid = Guid.Parse("0f1e2d3c-0000-4000-8000-000000000001");
        guid.TryWriteBytes(gpt.AsSpan(8));
        var raw = new byte[48];
        raw[0] = 2;

        Assert.Equal("MBR:5EED0001", DiskStructures.PartitionTableId(mbr));
        Assert.Equal("GPT:{0f1e2d3c-0000-4000-8000-000000000001}", DiskStructures.PartitionTableId(gpt));
        Assert.Equal("RAW", DiskStructures.PartitionTableId(raw));
    }

    [Fact]
    public void Only_numbered_partitions_are_listed()
    {
        var layout = new byte[48 + 144 * 4];
        BinaryPrimitives.WriteInt32LittleEndian(layout.AsSpan(4), 4);
        void Entry(int i, long offset, long length, int number)
        {
            BinaryPrimitives.WriteInt64LittleEndian(layout.AsSpan(48 + 144 * i + 8), offset);
            BinaryPrimitives.WriteInt64LittleEndian(layout.AsSpan(48 + 144 * i + 16), length);
            BinaryPrimitives.WriteInt32LittleEndian(layout.AsSpan(48 + 144 * i + 24), number);
        }
        Entry(0, 1 << 20, 61_489_545_216, 1);
        Entry(1, 61_490_593_792, 32 << 20, 2);
        Entry(2, 0, 0, 0); // empty MBR slot

        Assert.Equal([(1, 1L << 20, 61_489_545_216L), (2, 61_490_593_792L, 32L << 20)], DiskStructures.Partitions(layout));
    }

    [Fact]
    public void Device_number_and_mount_device_name_are_read()
    {
        var number = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(number.AsSpan(4), 2);
        const string Device = @"\Device\HarddiskVolume7";
        var name = new byte[2 + 2 * Device.Length + 8]; // MOUNTDEV_NAME: byte length, then the name, then unused room
        BinaryPrimitives.WriteUInt16LittleEndian(name, (ushort)(2 * Device.Length));
        Encoding.Unicode.GetBytes(Device).CopyTo(name.AsSpan(2));

        Assert.Equal(2, DiskStructures.DeviceNumber(number));
        Assert.Equal(Device, DiskStructures.MountDeviceName(name));
    }

    [Fact]
    public void Mount_point_query_names_one_device_and_its_links_are_read_back()
    {
        const string Device = @"\Device\HarddiskVolume7";
        var query = DiskStructures.MountPointQuery(Device);
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(query.AsSpan(16)));
        Assert.Equal(Device, Encoding.Unicode.GetString(query, 24, BinaryPrimitives.ReadUInt16LittleEndian(query.AsSpan(20))));

        string[] links = [@"\??\Volume{0f0e0d0c-0000-4000-8000-000000000001}", @"\DosDevices\E:"];
        var output = new byte[8 + 24 * 2 + 200];
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 2);
        int at = 8 + 24 * 2;
        for (int i = 0; i < links.Length; i++)
        {
            int bytes = Encoding.Unicode.GetBytes(links[i], 0, links[i].Length, output, at);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8 + 24 * i), at);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12 + 24 * i), (ushort)bytes);
            at += bytes;
        }

        Assert.Equal(links, DiskStructures.MountPointLinks(output));
    }

    [Fact]
    public void Geometry_gives_disk_size_and_sector_size()
    {
        var buffer = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), 512);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), 61_530_439_680);

        Assert.Equal((61_530_439_680L, 512), DiskStructures.ParseGeometry(buffer));
    }

    [Theory]
    [InlineData(@"FlashProof-x\fill_0001.bin", true)]
    [InlineData("FlashProof-x", true)]
    [InlineData(@"..\x", false)]
    [InlineData(@"FlashProof-x\..\..\x", false)]
    [InlineData(@"\x", false)]
    [InlineData(@"C:\x", false)]
    [InlineData("file.bin:stream", false)]
    [InlineData(@"a\\b", false)]
    [InlineData("", false)]
    public void Paths_must_stay_inside_the_volume(string path, bool allowed)
    {
        const string Root = @"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\";

        if (allowed)
            Assert.Equal(Root + path, WindowsTestVolume.PathInside(Root, path));
        else
            Assert.Throws<ArgumentException>(() => WindowsTestVolume.PathInside(Root, path));
    }

    [Theory]
    [InlineData(@"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\", true)]
    [InlineData(@"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}", false)]
    [InlineData(@"E:\", false)]
    [InlineData(@"\\.\PhysicalDrive2", false)]
    [InlineData(@"\\?\Volume{not-a-guid-at-all-not-a-guid-at-all-xxx}\", false)]
    public void Only_volume_GUID_paths_are_accepted(string path, bool valid)
    {
        Assert.Equal(valid, WindowsTestVolume.IsVolumeGuidPath(path));
    }
}
