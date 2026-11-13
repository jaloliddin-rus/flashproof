namespace FlashProof.Windows;

/// <summary>
/// Windows' "Safely remove" for a tested drive (design §6.2 step 7), as the taskbar icon does it, without admin rights.
/// CM_Request_Device_Eject needs no privilege for a device that isn't a dock when it's called from the signed-in
/// user's own session; from a session not attached to the console (Remote Desktop) it needs one standard users lack
/// (Microsoft Learn). It only asks: a program using the drive can veto it.
/// </summary>
public static class Ejection
{
    /// <summary>Asks Windows to eject the USB or SD device the disk is on.</summary>
    public static ReplugMessage Eject(string diskInstanceId)
    {
        if (Pnp.Locate(diskInstanceId) is not { } disk)
            return new(ReplugStep.Ejected); // already gone from Windows' device list
        var chain = Pnp.SelfAndAncestorNodes(disk)
            .Select(n => (Node: n, Id: Pnp.InstanceIdOf(n) ?? "", Capabilities: Pnp.UInt32Property(n, Native.DeviceCapabilities) ?? 0))
            .ToList();
        if (Target([.. chain.Select(c => (c.Id, c.Capabilities))]) is not { } target)
            return new(ReplugStep.EjectNotAllowed);

        var vetoName = new char[Native.MaxPath];
        int result = Native.CM_Request_Device_Eject(chain[target].Node, out int vetoType, vetoName, (uint)vetoName.Length, 0);
        if (result == Native.CrSuccess && vetoType == 0)
            return new(ReplugStep.Ejected);
        if (result == Native.CrAccessDenied || vetoType == Native.PnpVetoInsufficientRights)
            return new(ReplugStep.EjectNotAllowed);
        return new(ReplugStep.EjectFailed, Blocker(vetoType, new string(vetoName).TrimEnd('\0')));
    }

    /// <summary>
    /// Which device to eject, from the disk and the devices above it (nearest first, with their CM_DEVCAP flags): the
    /// nearest removable one, as "Safely remove" does. For a USB stick that's the USB device above the disk (seen on the
    /// SanDisk: its disk isn't removable, its USB device is). Only a USB, USB storage or SD device is ever ejected, and
    /// never a dock, so a controller, hub or docking station never is. Null if there's no such device.
    /// </summary>
    internal static int? Target(IReadOnlyList<(string InstanceId, uint Capabilities)> selfAndAncestors)
    {
        for (int i = 0; i < selfAndAncestors.Count; i++)
        {
            var (id, capabilities) = selfAndAncestors[i];
            if ((capabilities & Native.CmDevcapRemovable) == 0)
                continue;
            bool usbOrSd = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(@"SD\", StringComparison.OrdinalIgnoreCase);
            return usbOrSd && (capabilities & Native.CmDevcapDockDevice) == 0 ? i : null;
        }
        return null;
    }

    /// <summary>The program or service Windows says is using the drive; null when it names a device or nothing.</summary>
    static string? Blocker(int vetoType, string name) => name.Length == 0 ? null : vetoType switch
    {
        Native.PnpVetoWindowsApp => Path.GetFileName(name),
        Native.PnpVetoWindowsService => name,
        _ => null,
    };
}
