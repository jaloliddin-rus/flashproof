using System.Text.RegularExpressions;
using FlashProof.Coordination;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>The drive list (design §5.1): every USB, SD and MMC disk, and USB devices that failed, each with its status.</summary>
public sealed class HomeViewModel : Screen
{
    readonly MainViewModel main;

    public HomeViewModel(MainViewModel main)
    {
        this.main = main;
        RefreshCommand = new Command(main.ShowHome);
        Cards = BuildCards();
    }

    public Command RefreshCommand { get; }

    public IReadOnlyList<DeviceCard> Cards { get; private set; }

    public bool IsScanning => main.Coordinator.State == CoordinatorState.Scanning;

    public bool CanRefresh => main.Coordinator.State == CoordinatorState.Idle;

    public bool ShowNoDrives => !IsScanning && main.Coordinator.Devices is not null && Cards.Count == 0;

    public string? Error => main.Coordinator.Error is { } e && !IsScanning ? e.Text : null;

    public override void Update()
    {
        Cards = BuildCards();
        base.Update();
    }

    List<DeviceCard> BuildCards()
    {
        if (IsScanning || main.Coordinator.Devices is not { } found)
            return [];
        return
        [
            .. found.Devices.Select(d => new DeviceCard(d, () => main.ShowInspect(d), () => main.ShowConfirm(d, main.ShowHome))),
            .. found.ProblemDevices.Select(p => new DeviceCard(p)),
        ];
    }
}

/// <summary>One drive on the home screen.</summary>
public sealed partial class DeviceCard
{
    public DeviceCard(ScannedDevice device, Action inspect, Action test)
    {
        Name = DisplayName(device);
        (Status, Advice) = Text.DeviceStatus(device.Status);
        if (device.Failure is { } failure)
            Advice = $"{Advice} ({failure})";
        if (device.Report?.Disk is { MediaPresent: true } disk)
            Summary = Text.CardSummary(disk.Size, Eligibility.TestVolume(disk));
        IsReady = device.Status is null && device.Identity is not null;
        CanInspect = device.Report is not null;
        InspectCommand = new Command(inspect);
        TestCommand = new Command(test);
    }

    public DeviceCard(ProblemDevice device)
    {
        Name = Clean(device.Name);
        Status = Text.StatusNotWorking;
        Advice = $"{Text.Explanation(Core.Verdict.DetectedButNotWorking)} {Text.ProblemCode(device.ProblemCode)}";
    }

    public string Name { get; }
    public string Summary { get; } = "";
    public string Status { get; }
    public string Advice { get; }
    public bool IsReady { get; }
    public bool IsNotWorking => Status == Text.StatusNotWorking;
    public bool CanInspect { get; }
    public Command? InspectCommand { get; }
    public Command? TestCommand { get; }

    /// <summary>
    /// "SanDisk 3.2Gen1" rather than Windows' " USB  SanDisk 3.2Gen1 USB Device": the drive's own vendor and product,
    /// leaving out placeholder vendors; Windows' name, tidied, when the drive gave none.
    /// </summary>
    public static string DisplayName(ScannedDevice device)
    {
        var d = device.Report?.Disk.Descriptor;
        string product = d?.Product.Trim() ?? "";
        if (product.Length == 0)
            return Clean(device.Device.Name);
        string vendor = d!.Vendor.Trim();
        bool placeholder = vendor.Length == 0 || PlaceholderVendors.Contains(vendor) || product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase);
        return placeholder ? product : $"{vendor} {product}";
    }

    static readonly HashSet<string> PlaceholderVendors = new(["USB", "Generic", "Generic-", "Mass", "Mass Storage"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' friendly name without its extra spaces and the " USB Device" ending.</summary>
    static string Clean(string name)
    {
        string tidy = Spaces().Replace(name.Trim(), " ");
        return tidy.EndsWith(" USB Device", StringComparison.OrdinalIgnoreCase) ? tidy[..^" USB Device".Length] : tidy;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
