using System.Globalization;
using System.Security.Cryptography;
using FlashProof.Coordination;
using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.ViewModels;

/// <summary>The confirmation screen (design §5.3) with the test's scope and expected time (design §6.2 step 1).</summary>
public sealed class ConfirmViewModel : Screen
{
    readonly MainViewModel main;
    readonly ScannedDevice device;
    readonly VolumeInfo volume;
    readonly VolumeIdentity identity;
    bool backedUp, replug;

    public ConfirmViewModel(MainViewModel main, ScannedDevice device, Action back)
    {
        this.main = main;
        this.device = device;
        var disk = device.Report?.Disk ?? throw new ArgumentException("The drive has no report.", nameof(device));
        volume = Eligibility.TestVolume(disk) ?? throw new ArgumentException("The drive has no test volume.", nameof(device));
        identity = device.Identity ?? throw new ArgumentException("The drive isn't Ready.", nameof(device));
        long planned = new FullTestOptions().PlannedBytes(volume.FreeBytes);
        DeviceLine = Text.DeviceLine(DeviceCard.DisplayName(device), disk.Size, volume);
        WhatIsWritten = Text.WhatIsWritten(volume);
        Scope = Text.Scope(planned, disk.Size);
        var (fastest, slowest) = TimeEstimate.Expected(planned);
        TimeRange = Text.TimeRange(fastest, slowest);
        BackCommand = new Command(back);
        StartCommand = new Command(Start);
    }

    public string DeviceLine { get; }
    public string WhatIsWritten { get; }
    public string Scope { get; }
    public string TimeRange { get; }
    public Command BackCommand { get; }
    public Command StartCommand { get; }

    public bool BackedUp
    {
        get => backedUp;
        set
        {
            Set(ref backedUp, value);
            Raise(nameof(CanStart));
        }
    }

    /// <summary>"Unplug and replug before verifying" (design §6.2 step 7): off by default.</summary>
    public bool Replug
    {
        get => replug;
        set => Set(ref replug, value);
    }

    /// <summary>The user must confirm their files are backed up (design §5.3).</summary>
    public bool CanStart => backedUp && main.Coordinator.State == CoordinatorState.Idle;

    void Start()
    {
        if (!CanStart)
            return;
        // After a pass the test files are always deleted (design §5.3): v1 has no re-check that would need them.
        var spec = new StartSpec(NewSessionId(main.Clock), device.Device.InstanceId, volume.GuidPath, identity, ReplugBeforeVerify: replug);
        main.StartTest(device, spec);
    }

    /// <summary>"20261112-140512-9f3a": when it started (UTC), and 4 random hex digits. Letters, digits and '-' only, as the worker requires.</summary>
    public static string NewSessionId(TimeProvider clock) =>
        $"{clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
}
