using FlashProof.Core;

namespace FlashProof.Windows.Tests;

// Design §5.1: which disks may be full-tested, and the verdict for those that can't.
public class EligibilityTests
{
    const long GB = 1_000_000_000;

    internal static readonly SystemFacts Pc = new(@"\Device\HarddiskVolume1", new HashSet<string>(["C:"], StringComparer.OrdinalIgnoreCase), WritePolicy.None);

    internal static VolumeInfo Ventoy(string fileSystem = "exFAT", bool readOnly = false, long free = 60 * GB, int extents = 1, long length = 61 * GB, bool bitLocker = false) =>
        new(@"\\?\Volume{0f0e0d0c-0000-4000-8000-000000000001}\", @"\Device\HarddiskVolume7", ["E:"], extents, 1 << 20, length,
            fileSystem, "Ventoy", 0x4E210000, readOnly, free, fileSystem == "" ? 1005 : 0, bitLocker);

    internal static readonly VolumeInfo Efi = new(@"\\?\Volume{1a2b3c4d-0000-0000-0000-20510e000000}\", @"\Device\HarddiskVolume8", [], 1, 61 * GB + (1 << 20), 32 << 20,
        "FAT", "VTOYEFI", 0x1B438741, false, 1 << 20, 0, false);

    internal static DiskInfo SanDisk(int bus = DeviceDescriptor.BusTypeUsb, bool removable = true, bool media = true, bool writeProtected = false, params VolumeInfo[] volumes) =>
        new(new DiskCandidate(@"USBSTOR\DISK&VEN_USB&PROD_SANDISK_3.2GEN1\0001", @"\\?\usbstor#disk#0001#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}", "USB SanDisk 3.2Gen1 USB Device"),
            2, new DeviceDescriptor("USB", "SanDisk 3.2Gen1", "1.00", "0001", bus, removable), null, media, 61_524_148_224, 512, writeProtected, "MBR:1A2B3C4D",
            volumes.Length > 0 ? volumes : [Ventoy(), Efi]);

    static void AssertCantTest(CantTestReason reason, VerdictResult? result)
    {
        Assert.Equal(Verdict.CantTest, result?.Verdict);
        Assert.Equal(reason, result?.CantTestReason);
    }

    [Fact]
    public void A_removable_USB_drive_with_an_exFAT_volume_is_ready()
    {
        Assert.Null(Eligibility.Check(SanDisk(), Pc));
        Assert.Equal("Ventoy", Eligibility.TestVolume(SanDisk())?.Label);
    }

    [Fact]
    public void The_test_volume_is_the_largest_with_a_supported_file_system()
    {
        var small = Ventoy(length: 1 * GB) with { Label = "small" };
        var raw = Ventoy("", length: 100 * GB) with { Label = "raw" };

        Assert.Equal("small", Eligibility.TestVolume(SanDisk(volumes: [Efi, raw, small]))?.Label);
    }

    [Theory]
    [InlineData(DeviceDescriptor.BusTypeUsb, CantTestReason.ExternalSsdOrHardDrive)]
    [InlineData(DeviceDescriptor.BusTypeSd, CantTestReason.EmbeddedStorage)]
    [InlineData(DeviceDescriptor.BusTypeMmc, CantTestReason.EmbeddedStorage)]
    public void Fixed_media_is_not_supported(int bus, CantTestReason reason)
    {
        AssertCantTest(reason, Eligibility.Check(SanDisk(bus, removable: false), Pc));
    }

    [Fact]
    public void Other_bus_types_are_not_supported()
    {
        AssertCantTest(CantTestReason.UnsupportedDevice, Eligibility.Check(SanDisk(bus: 17 /* NVMe */), Pc));
    }

    [Fact]
    public void A_disk_holding_the_system_partition_or_a_protected_letter_is_refused()
    {
        var system = Pc with { SystemPartitionDevice = Efi.DeviceName };
        var windows = Pc with { ProtectedLetters = new HashSet<string>(["C:", "E:"]) };

        AssertCantTest(CantTestReason.HoldsSystemFiles, Eligibility.Check(SanDisk(), system));
        AssertCantTest(CantTestReason.HoldsSystemFiles, Eligibility.Check(SanDisk(), windows));
    }

    [Fact]
    public void A_reader_without_a_card_says_so()
    {
        Assert.Equal(Verdict.NoCardInReader, Eligibility.Check(SanDisk(media: false, volumes: []) with { Volumes = [] }, Pc)?.Verdict);
    }

    [Fact]
    public void A_policy_is_blamed_before_the_device()
    {
        var denied = Pc with { Policy = WritePolicy.None with { RemovableDisksDenyWrite = true } };
        var protectedByRegistry = Pc with { Policy = WritePolicy.None with { StorageWriteProtect = true } };

        Assert.Equal(Verdict.WritingBlockedByWindows, Eligibility.Check(SanDisk(writeProtected: true), denied)?.Verdict);
        Assert.Equal(Verdict.WritingBlockedByWindows, Eligibility.Check(SanDisk(writeProtected: true), protectedByRegistry)?.Verdict);
    }

    [Fact]
    public void BitLocker_policy_blocks_only_the_volumes_it_made_read_only()
    {
        var bitLocker = Pc with { Policy = WritePolicy.None with { BitLockerDenyWrite = true } };

        Assert.Equal(Verdict.WritingBlockedByWindows, Eligibility.Check(SanDisk(volumes: [Ventoy(readOnly: true)]), bitLocker)?.Verdict);
        Assert.Null(Eligibility.Check(SanDisk(), bitLocker));
    }

    [Fact]
    public void A_device_or_volume_that_refuses_writes_is_write_protected()
    {
        Assert.Equal(Verdict.WriteProtected, Eligibility.Check(SanDisk(writeProtected: true), Pc)?.Verdict);
        Assert.Equal(Verdict.WriteProtected, Eligibility.Check(SanDisk(volumes: [Ventoy(readOnly: true)]), Pc)?.Verdict);
    }

    [Fact]
    public void Volume_must_be_one_range_inside_the_disk()
    {
        AssertCantTest(CantTestReason.VolumeSpansDisks, Eligibility.Check(SanDisk(volumes: [Ventoy(extents: 2)]), Pc));
        AssertCantTest(CantTestReason.VolumeLargerThanDisk, Eligibility.Check(SanDisk(volumes: [Ventoy(length: 128 * GB)]), Pc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FAT")]
    [InlineData("ReFS")]
    public void Unsupported_or_missing_file_systems_cant_be_tested(string fileSystem)
    {
        AssertCantTest(CantTestReason.NoFileSystem, Eligibility.Check(SanDisk(volumes: [Ventoy(fileSystem)]), Pc));
    }

    [Fact]
    public void A_disk_with_no_volume_has_no_file_system()
    {
        AssertCantTest(CantTestReason.NoFileSystem, Eligibility.Check(SanDisk() with { Volumes = [] }, Pc));
    }

    [Fact]
    public void A_locked_BitLocker_volume_says_so()
    {
        AssertCantTest(CantTestReason.BitLockerLocked, Eligibility.Check(SanDisk(volumes: [Ventoy("", bitLocker: true)]), Pc));
    }

    [Fact]
    public void Less_than_256_MiB_free_cant_be_tested()
    {
        AssertCantTest(CantTestReason.NotEnoughFreeSpace, Eligibility.Check(SanDisk(volumes: [Ventoy(free: (256L << 20) - 1)]), Pc));
        Assert.Null(Eligibility.Check(SanDisk(volumes: [Ventoy(free: 256L << 20)]), Pc));
    }
}

public class DeviceScanTests
{
    [Theory]
    [InlineData(true, @"USBSTOR\DISK&VEN_USB&PROD_SANDISK_3.2GEN1&REV_1.00\0401&0", @"USB\VID_0781&PID_5581\0401")]
    [InlineData(true, @"SD\VID_03&OID_5344&PID_SL64G&REV_8.0\5&1A2B&0&0", @"PCI\VEN_8086&DEV_9DC4")]
    [InlineData(true, @"SCSI\DISK&VEN_SAMSUNG&PROD_PORTABLE_SSD\8&1234&0&000000", @"USB\VID_04E8&PID_4001\S5X")]
    [InlineData(false, @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_980\5&2DEF&0&000000", @"PCI\VEN_144D&DEV_A809", @"ACPI\PNP0A08\0")]
    [InlineData(false, @"SCSI\DISK&VEN_MSFT&PROD_VIRTUAL_DISK\2&1F4ADFFE&0&000001", @"ROOT\VHDMP\0000")]
    [InlineData(false, @"SCSI\DISK&VEN_NVME&PROD_SSD\7&AB&0&000000", @"USB4\ROUTER\0", @"PCI\VEN_8086&DEV_9A1B")]
    public void Only_disks_on_USB_or_an_SD_host_are_candidates(bool candidate, params string[] selfAndAncestors)
    {
        Assert.Equal(candidate, DeviceScan.IsCandidate(selfAndAncestors));
    }

    [Fact]
    public void SD_must_be_the_disk_itself_not_an_ancestor()
    {
        Assert.False(DeviceScan.IsCandidate([@"SCSI\DISK&VEN_X\1", @"SD\SOMETHING\2"]));
    }

    [Fact]
    public void Drive_letters_are_taken_from_paths_and_page_files()
    {
        var letters = SystemFacts.LettersOf([@"C:", @"C:\Windows", @"\??\D:\pagefile.sys", @"\\?\e:\x", @"\\server\share\app", null]);

        Assert.Equal(["C:", "D:", "E:"], letters.Order());
    }

    [Fact]
    public void Multi_strings_are_split_at_nulls()
    {
        Assert.Equal(["a", "bc"], Pnp.SplitMultiString("a\0bc\0\0"));
        Assert.Empty(Pnp.SplitMultiString("\0"));
    }
}
