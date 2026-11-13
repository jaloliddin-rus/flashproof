using System.ComponentModel;
using System.Runtime.InteropServices;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>Turns Win32 error codes into <see cref="StorageIoException"/>s with the right <see cref="IoErrorKind"/>.</summary>
internal static class Win32Errors
{
    /// <summary>
    /// The kind of a Win32 error. Each code's meaning was checked against the system's own message for it
    /// (FormatMessage, Windows 11 build 26100); which ones a real unplug produces is still to be seen on hardware.
    /// </summary>
    public static IoErrorKind KindOf(int code) => code switch
    {
        112 => IoErrorKind.DiskFull,           // ERROR_DISK_FULL: There is not enough space on the disk.
        39 => IoErrorKind.DiskFull,            // ERROR_HANDLE_DISK_FULL: The disk is full.
        19 => IoErrorKind.WriteProtected,      // ERROR_WRITE_PROTECT: The media is write protected.
        1392 => IoErrorKind.FileSystemCorrupt, // ERROR_FILE_CORRUPT: The file or directory is corrupted and unreadable.
        1393 => IoErrorKind.FileSystemCorrupt, // ERROR_DISK_CORRUPT: The disk structure is corrupted and unreadable.
        1167 => IoErrorKind.DeviceRemoved,     // ERROR_DEVICE_NOT_CONNECTED: The device is not connected.
        1617 => IoErrorKind.DeviceRemoved,     // ERROR_DEVICE_REMOVED: The device has been removed.
        21 => IoErrorKind.DeviceRemoved,       // ERROR_NOT_READY: The device is not ready.
        55 => IoErrorKind.DeviceRemoved,       // ERROR_DEV_NOT_EXIST: The specified network resource or device is no longer available.
        433 => IoErrorKind.DeviceRemoved,      // ERROR_NO_SUCH_DEVICE: A device which does not exist was specified.
        1112 => IoErrorKind.DeviceRemoved,     // ERROR_NO_MEDIA_IN_DRIVE: No media in drive.
        1110 => IoErrorKind.DeviceRemoved,     // ERROR_MEDIA_CHANGED: The media in the drive may have changed.
        1006 => IoErrorKind.DeviceRemoved,     // ERROR_FILE_INVALID: The volume for a file has been externally altered so that the opened file is no longer valid.
        34 => IoErrorKind.DeviceRemoved,       // ERROR_WRONG_DISK: The wrong diskette is in the drive. (The file system dropped the volume, as with 1006; seen on a FAT32 stick, 13 Nov 2026.)
        _ => IoErrorKind.Other,
    };

    public static StorageIoException FromCode(int code) => new(KindOf(code), code, new Win32Exception(code).Message);

    /// <summary>The error of the Win32 call that just failed.</summary>
    public static StorageIoException Last() => FromCode(Marshal.GetLastPInvokeError());

    /// <summary>
    /// The <see cref="StorageIoException"/> for an exception from a .NET I/O call such as <see cref="RandomAccess"/>, or
    /// null if it isn't an I/O failure: a cancellation the caller asked for, or a programming error.
    /// </summary>
    public static StorageIoException? Translate(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => null,
        OperationCanceledException => FromCode(Native.ErrorOperationAborted),
        IOException or UnauthorizedAccessException => FromCode(CodeOf(ex.HResult)),
        _ => null,
    };

    /// <summary>The Win32 code inside an HRESULT_FROM_WIN32 value (0x8007xxxx), or 0 for any other HRESULT.</summary>
    public static int CodeOf(int hresult) => (hresult & 0xFFFF_0000) == 0x8007_0000 ? hresult & 0xFFFF : 0;
}
