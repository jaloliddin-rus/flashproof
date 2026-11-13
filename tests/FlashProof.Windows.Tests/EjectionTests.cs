namespace FlashProof.Windows.Tests;

// Design §6.2 step 7: which device "Eject now" asks Windows to eject. Capabilities are CM_DEVCAP flags:
// 0x4 removable, 0x8 dock, 0x10 unique ID, 0x80 surprise removal OK.
public class EjectionTests
{
    [Fact]
    public void A_USB_stick_is_ejected_through_the_USB_device_above_its_disk()
    {
        // Shaped like a SanDisk stick's chain in Windows' device list; the instance IDs are made up.
        (string, uint)[] chain =
        [
            (@"USBSTOR\DISK&VEN__USB&PROD__SANDISK_3.2GEN1&REV_1.00\0000AB12", 0x10),
            (@"USB\VID_0781&PID_55A9\0000ab12cdef", 0x94),
            (@"USB\ROOT_HUB30\5&2c3d4e5f&0&0", 0x80),
            (@"PCI\VEN_1022&DEV_149C&SUBSYS_00000000&REV_00\4&3e4f5a6b&0&0341", 0x0),
        ];

        Assert.Equal(1, Ejection.Target(chain));
    }

    [Fact]
    public void A_UAS_drive_is_ejected_through_its_USB_device()
    {
        Assert.Equal(1, Ejection.Target([(@"SCSI\DISK&VEN_SAMSUNG&PROD_FLASH\7&1", 0x0), (@"USB\VID_04E8&PID_61F5\S1", 0x94)]));
    }

    [Fact]
    public void A_card_on_an_SD_host_is_ejected_itself()
    {
        Assert.Equal(0, Ejection.Target([(@"SD\VID_03&OID_5344&PID_SD32G\5&1", 0x4), (@"PCI\VEN_17A0&DEV_9755\4&2", 0x0)]));
    }

    public static TheoryData<string, (string, uint)[]> NeverEjected => new()
    {
        { "a removable PCI controller", [(@"SCSI\DISK&VEN_RTSUER\1", 0x0), (@"PCI\VEN_10EC&DEV_5260\4&3", 0x4)] },
        { "a dock", [(@"USBSTOR\DISK&VEN_X\1", 0x10), (@"USB\VID_17EF&PID_A396\1", 0x4 | 0x8)] },
        { "nothing removable", [(@"USBSTOR\DISK&VEN_X\1", 0x10), (@"USB\ROOT_HUB30\5&1", 0x80), (@"PCI\VEN_1022\4&1", 0x0)] },
    };

    [Theory]
    [MemberData(nameof(NeverEjected))]
    public void Nothing_but_a_USB_or_SD_device_is_ever_ejected(string what, (string, uint)[] chain)
    {
        Assert.True(Ejection.Target(chain) is null, what);
    }
}
