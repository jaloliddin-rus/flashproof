using System.Buffers.Binary;
using System.Text;

namespace FlashProof.Windows;

/// <summary>
/// Reads Windows' device list (the PnP manager, as Device Manager shows it). Nothing here opens a device or sends it
/// anything, so it's safe for every device in the computer, the system disks included.
/// </summary>
internal static class Pnp
{
    const uint MaxDeviceIdLength = 200;
    const uint PropertyTypeString = 0x12, PropertyTypeFileTime = 0x10, PropertyTypeUInt32 = 0x7;

    /// <summary>The present interfaces of a class, optionally of one device only.</summary>
    public static IReadOnlyList<string> Interfaces(Guid interfaceClass, string? deviceId = null)
    {
        while (true)
        {
            if (Native.CM_Get_Device_Interface_List_Size(out uint length, interfaceClass, deviceId, 0) != Native.CrSuccess)
                return [];
            var buffer = new char[length];
            int result = Native.CM_Get_Device_Interface_List(interfaceClass, deviceId, buffer, length, 0);
            if (result == Native.CrSuccess)
                return SplitMultiString(buffer);
            if (result != Native.CrBufferSmall)
                return []; // the list changed between the two calls only if the result is "buffer too small"
        }
    }

    /// <summary>The instance IDs of the present devices of one enumerator, such as "USB".</summary>
    public static IReadOnlyList<string> DevicesOf(string enumerator)
    {
        const uint Flags = Native.CmGetIdListFilterEnumerator | Native.CmGetIdListFilterPresent;
        while (true)
        {
            if (Native.CM_Get_Device_ID_List_Size(out uint length, enumerator, Flags) != Native.CrSuccess)
                return [];
            var buffer = new char[length];
            int result = Native.CM_Get_Device_ID_List(enumerator, buffer, length, Flags);
            if (result == Native.CrSuccess)
                return SplitMultiString(buffer);
            if (result != Native.CrBufferSmall)
                return [];
        }
    }

    public static uint? Locate(string instanceId) => Native.CM_Locate_DevNode(out uint devInst, instanceId, 0) == Native.CrSuccess ? devInst : null;

    public static string? InstanceIdOf(uint devInst)
    {
        var buffer = new char[MaxDeviceIdLength + 1];
        return Native.CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) == Native.CrSuccess ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>The device and every device above it in the tree, nearest first.</summary>
    public static IEnumerable<string> SelfAndAncestors(uint devInst)
    {
        foreach (uint node in SelfAndAncestorNodes(devInst))
        {
            if (InstanceIdOf(node) is { } id)
                yield return id;
        }
    }

    public static IEnumerable<uint> SelfAndAncestorNodes(uint devInst)
    {
        for (uint? node = devInst; node is { } n; node = Native.CM_Get_Parent(out uint parent, n, 0) == Native.CrSuccess ? parent : null)
            yield return n;
    }

    public static uint? UInt32Property(uint devInst, in Native.DevPropKey key) =>
        Property(devInst, key, PropertyTypeUInt32) is { Length: 4 } bytes ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;

    public static bool HasChild(uint devInst) => Native.CM_Get_Child(out _, devInst, 0) == Native.CrSuccess;

    /// <summary>The device's problem code (as in Device Manager, e.g. 43), or 0 if it has none.</summary>
    public static int ProblemOf(uint devInst) =>
        Native.CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == Native.CrSuccess && (status & Native.DnHasProblem) != 0 ? (int)problem : 0;

    public static string? StringProperty(uint devInst, in Native.DevPropKey key) =>
        Property(devInst, key, PropertyTypeString) is { } bytes ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;

    public static DateTimeOffset? FileTimeProperty(uint devInst, in Native.DevPropKey key) =>
        Property(devInst, key, PropertyTypeFileTime) is { Length: 8 } bytes ? DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(bytes)).ToUniversalTime() : null;

    public static string? InterfaceStringProperty(string deviceInterface, in Native.DevPropKey key)
    {
        uint size = 0;
        Native.CM_Get_Device_Interface_Property(deviceInterface, key, out _, null, ref size, 0);
        var buffer = new byte[size];
        return size > 0 && Native.CM_Get_Device_Interface_Property(deviceInterface, key, out uint type, buffer, ref size, 0) == Native.CrSuccess && type == PropertyTypeString
            ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0')
            : null;
    }

    static byte[]? Property(uint devInst, in Native.DevPropKey key, uint expectedType)
    {
        uint size = 0;
        Native.CM_Get_DevNode_Property(devInst, key, out _, null, ref size, 0);
        var buffer = new byte[size];
        return size > 0 && Native.CM_Get_DevNode_Property(devInst, key, out uint type, buffer, ref size, 0) == Native.CrSuccess && type == expectedType
            ? buffer[..(int)size]
            : null;
    }

    internal static IReadOnlyList<string> SplitMultiString(ReadOnlySpan<char> buffer)
    {
        var items = new List<string>();
        foreach (var range in buffer.Split('\0'))
        {
            if (range.End.GetOffset(buffer.Length) > range.Start.GetOffset(buffer.Length))
                items.Add(new string(buffer[range]));
        }
        return items;
    }
}
