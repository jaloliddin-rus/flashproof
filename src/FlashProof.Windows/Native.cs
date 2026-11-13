using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>The Win32 calls the Windows layer makes. None of them needs admin rights on removable media.</summary>
internal static partial class Native
{
    // Access rights and sharing (CreateFileW).
    public const uint NoAccess = 0;
    public const uint FileReadAttributes = 0x80;
    public const uint GenericRead = 0x8000_0000;
    public const uint GenericWrite = 0x4000_0000;
    public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    public const uint ShareAll = ShareRead | ShareWrite | ShareDelete;

    // Creation dispositions.
    public const uint CreateNew = 1;
    public const uint OpenExisting = 3;

    // Flags.
    public const uint FlagNoBuffering = 0x2000_0000;
    public const uint FlagWriteThrough = 0x8000_0000;
    public const uint FlagOverlapped = 0x4000_0000;
    public const uint FlagBackupSemantics = 0x0200_0000;
    public const uint FlagOpenReparsePoint = 0x0020_0000;

    // Control codes.
    public const uint FsctlGetRetrievalPointers = 0x0009_0073;
    public const uint FsctlGetRetrievalPointerBase = 0x0009_0234;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x0056_0000;
    public const uint IoctlStorageQueryProperty = 0x002D_1400;
    public const uint IoctlStorageCheckVerify2 = 0x002D_0800;
    public const uint IoctlDiskGetDriveGeometryEx = 0x0007_00A0;
    public const uint IoctlDiskGetDriveLayoutEx = 0x0007_0050;
    public const uint IoctlDiskIsWritable = 0x0007_0024;
    public const uint IoctlStorageGetDeviceNumber = 0x002D_1080;
    public const uint IoctlMountdevQueryDeviceName = 0x004D_0008;
    public const uint IoctlMountmgrQueryPoints = 0x006D_0008;

    /// <summary>GUID_DEVINTERFACE_DISK.</summary>
    public static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>The volume is read-only (GetVolumeInformation flags).</summary>
    public const uint FileReadOnlyVolume = 0x0008_0000;

    // Win32 error codes this layer acts on.
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidFunction = 1;
    public const int ErrorHandleEof = 38;
    public const int ErrorNotSupported = 50;
    public const int ErrorMoreData = 234;
    public const int ErrorOperationAborted = 995;
    public const int ErrorWriteProtect = 19;
    public const int ErrorNotReady = 21;
    public const int ErrorNoMediaInDrive = 1112;
    public const int ErrorSharingViolation = 32;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorNoMoreItems = 259;

    // Event log (wevtapi) flags.
    public const uint EvtQueryChannelPath = 0x1;
    public const uint EvtQueryReverseDirection = 0x200;
    public const uint EvtRenderEventXml = 1;

    // Configuration Manager (cfgmgr32) results and flags.
    public const int CrSuccess = 0;
    public const int CrBufferSmall = 0x1A;
    public const int CrRemoveVetoed = 0x17;
    public const int CrAccessDenied = 0x33;

    // CM_DEVCAP_* flags in DEVPKEY_Device_Capabilities, and PNP_VETO_TYPE values (cfg.h).
    public const uint CmDevcapRemovable = 0x4;
    public const uint CmDevcapDockDevice = 0x8;
    public const int PnpVetoWindowsApp = 3;
    public const int PnpVetoWindowsService = 4;
    public const int PnpVetoInsufficientRights = 12;
    public const int MaxPath = 260;
    public const uint CmGetIdListFilterEnumerator = 0x1;
    public const uint CmGetIdListFilterPresent = 0x100;
    public const uint DnHasProblem = 0x400;

    /// <summary>DEVPROPKEY: a device property's ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        readonly Guid formatId = formatId;
        readonly uint propertyId = propertyId;
    }

    public static readonly DevPropKey DeviceInstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    public static readonly DevPropKey DeviceFriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly DevPropKey DeviceDescription = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    public static readonly DevPropKey DeviceService = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 6);
    public static readonly DevPropKey DeviceLastArrivalDate = new(new("83da6326-97a6-4088-9453-a1923f573b29"), 102);
    public static readonly DevPropKey DeviceCapabilities = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 17);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_Interface_List(in Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_ID(uint devInst, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static partial int CM_Get_DevNode_Property(uint devInst, in DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_Interface_Property(string deviceInterface, in DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Child(out uint child, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    /// <summary>With a veto buffer given, Windows shows no message of its own (Microsoft Learn).</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Request_Device_Eject(uint devInst, out int vetoType, [Out] char[] vetoName, uint nameLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string root, [Out] char[] name, uint nameSize, out uint serial, out uint maxComponentLength, out uint flags, [Out] char[] fileSystem, uint fileSystemSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    /// <summary>A second handle to an already open file, with other access and flags; the file isn't looked up by name again.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string folder, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformationByHandle(SafeFileHandle handle, nint nameBuffer, uint nameSize, out uint serial, out uint maxComponentLength, out uint flags, nint fileSystemBuffer, uint fileSystemSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateDirectory(string path, nint security);

    [LibraryImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveDirectory(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteFile(string path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeEvtHandle EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNext(SafeEvtHandle resultSet, int eventsSize, [Out] nint[] events, int timeoutMs, uint flags, out int returned);

    /// <param name="bufferSize">In bytes.</param>
    /// <param name="bufferUsed">In bytes, or the size needed if the buffer is too small.</param>
    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtRender(nint context, nint fragment, uint flags, int bufferSize, [Out] char[] buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtClose(nint handle);

    // Device notifications (CM_Register_Notification).
    public const int CmNotifyFilterTypeDeviceHandle = 1;
    public const int CmNotifyActionDeviceQueryRemove = 2;

    /// <summary>CM_NOTIFY_FILTER for a device handle; the union's largest member (an instance ID) makes it 416 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 416)]
    public struct CmNotifyFilter
    {
        public uint Size;
        public uint Flags;
        public int FilterType;
        public uint Reserved;
        public nint Handle;
    }

    [LibraryImport("cfgmgr32.dll")]
    public static unsafe partial int CM_Register_Notification(in CmNotifyFilter filter, nint context,
        delegate* unmanaged[Stdcall]<nint, nint, int, nint, uint, uint> callback, out nint notification);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Unregister_Notification(nint notification);

    // Power (design §5.4).
    public const uint EsContinuous = 0x8000_0000;
    public const uint EsSystemRequired = 0x1;
    public const uint DeviceNotifyCallback = 2;
    public const uint PbtApmSuspend = 4, PbtApmResumeSuspend = 7, PbtApmResumeAutomatic = 0x12;

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint flags);

    /// <summary>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DeviceNotifySubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    public static unsafe partial uint PowerRegisterSuspendResumeNotification(uint flags, DeviceNotifySubscribeParameters* recipient, out nint registration);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerUnregisterSuspendResumeNotification(nint registration);

    // Windows (one copy of the app).
    public const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    /// <summary>An event log handle (EVT_HANDLE).</summary>
    public sealed class SafeEvtHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => EvtClose(handle);
    }
}
