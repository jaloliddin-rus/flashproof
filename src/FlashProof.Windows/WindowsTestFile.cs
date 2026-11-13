using FlashProof.Core;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>
/// A test file opened unbuffered and overlapped (design §6.2). I/O goes through <see cref="RandomAccess"/>, whose
/// cancellation issues CancelIoEx for the pending request. Offsets, lengths and buffers must be sector-aligned.
/// </summary>
internal sealed class WindowsTestFile(WindowsTestVolume volume, SafeFileHandle handle) : ITestFile
{
    /// <summary>
    /// Writes the buffer and returns its length. In .NET 10, <see cref="RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory{byte}, long, CancellationToken)"/>
    /// on an overlapped handle neither returns nor checks the byte count Windows reports (seen in its IL), so a short
    /// write isn't caught here. Verification's read-back still finds the missing records, so it can't cause a pass.
    /// </summary>
    public async ValueTask<int> WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            await RandomAccess.WriteAsync(handle, data, offset, ct);
            return data.Length;
        }
        catch (Exception ex) when (Win32Errors.Translate(ex, ct) is { } error)
        {
            throw error;
        }
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            return await RandomAccess.ReadAsync(handle, buffer, offset, ct);
        }
        catch (Exception ex) when (Win32Errors.Translate(ex, ct) is { } error)
        {
            throw error;
        }
    }

    /// <summary>FlushFileBuffers. This doesn't prove the data reached the flash (design §6.2 step 6).</summary>
    public void Flush()
    {
        if (!Native.FlushFileBuffers(handle))
            throw Win32Errors.Last();
    }

    public IReadOnlyList<FileExtent> GetExtents() => volume.ExtentsOf(handle);

    public void Dispose() => handle.Dispose();
}
