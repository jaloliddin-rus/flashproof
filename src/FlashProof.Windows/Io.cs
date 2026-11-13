using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>Opening handles and sending control codes, with failures as <see cref="FlashProof.Core.StorageIoException"/>s.</summary>
internal static class Io
{
    public static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = Native.CreateFile(path, access, share, 0, disposition, flags, 0);
        if (handle.IsInvalid)
            throw Win32Errors.Last();
        return handle;
    }

    public static byte[] Control(SafeFileHandle handle, uint code, byte[]? input, int outputSize) =>
        TryControl(handle, code, input, outputSize, allowUnsupported: false)!;

    /// <summary>The output of a control call, or null if the device doesn't support it.</summary>
    public static byte[]? TryControl(SafeFileHandle handle, uint code, byte[]? input, int outputSize, bool allowUnsupported = true)
    {
        var output = new byte[outputSize];
        if (Native.DeviceIoControl(handle, code, input, input?.Length ?? 0, output, outputSize, out _, 0))
            return output;
        int error = Marshal.GetLastPInvokeError();
        if (allowUnsupported && error is Native.ErrorInvalidFunction or Native.ErrorNotSupported)
            return null;
        throw Win32Errors.FromCode(error);
    }

    /// <summary>The Win32 error of a control call that returns no data, or 0 if it succeeded.</summary>
    public static int ControlError(SafeFileHandle handle, uint code, int outputSize = 0)
    {
        var output = outputSize > 0 ? new byte[outputSize] : null;
        return Native.DeviceIoControl(handle, code, null, 0, output, outputSize, out _, 0) ? 0 : Marshal.GetLastPInvokeError();
    }
}
