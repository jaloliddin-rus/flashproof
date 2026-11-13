using FlashProof.Core;

namespace FlashProof.Windows.Tests;

public class Win32ErrorsTests
{
    public static readonly TheoryData<int, IoErrorKind> Kinds = new()
    {
        { 112, IoErrorKind.DiskFull },
        { 39, IoErrorKind.DiskFull },
        { 19, IoErrorKind.WriteProtected },
        { 1392, IoErrorKind.FileSystemCorrupt },
        { 1393, IoErrorKind.FileSystemCorrupt },
        { 1167, IoErrorKind.DeviceRemoved },
        { 1617, IoErrorKind.DeviceRemoved },
        { 21, IoErrorKind.DeviceRemoved },
        { 55, IoErrorKind.DeviceRemoved },
        { 433, IoErrorKind.DeviceRemoved },
        { 1112, IoErrorKind.DeviceRemoved },
        { 1110, IoErrorKind.DeviceRemoved },
        { 1006, IoErrorKind.DeviceRemoved },
        { 34, IoErrorKind.DeviceRemoved },
        { 23, IoErrorKind.Other },  // CRC error: a read error, counted against the drive
        { 5, IoErrorKind.Other },   // access denied: a Windows policy is checked separately (design §5.1)
        { 31, IoErrorKind.Other },  // device not functioning
        { 995, IoErrorKind.Other }, // aborted without our cancellation
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Codes_map_to_kinds(int code, IoErrorKind kind)
    {
        var ex = Win32Errors.FromCode(code);

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(code, ex.NativeCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Win32_code_is_taken_from_HRESULT()
    {
        Assert.Equal(112, Win32Errors.CodeOf(unchecked((int)0x8007_0070)));
        Assert.Equal(0, Win32Errors.CodeOf(unchecked((int)0x8013_153B))); // COR_E_OPERATIONCANCELED isn't a Win32 code
    }

    [Fact]
    public void IO_exceptions_become_storage_errors()
    {
        var full = Win32Errors.Translate(new IOException("full", unchecked((int)0x8007_0070)), CancellationToken.None);
        var denied = Win32Errors.Translate(new UnauthorizedAccessException(), CancellationToken.None);

        Assert.Equal(IoErrorKind.DiskFull, full?.Kind);
        Assert.Equal(5, denied?.NativeCode);
    }

    [Fact]
    public void Requested_cancellation_is_passed_on_but_other_aborts_are_errors()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(Win32Errors.Translate(new OperationCanceledException(), cancelled.Token));
        Assert.Equal(995, Win32Errors.Translate(new OperationCanceledException(), CancellationToken.None)?.NativeCode);
    }

    [Fact]
    public void Programming_errors_are_not_translated()
    {
        Assert.Null(Win32Errors.Translate(new ArgumentException(), CancellationToken.None));
        Assert.Null(Win32Errors.Translate(new ObjectDisposedException("handle"), CancellationToken.None));
    }
}
