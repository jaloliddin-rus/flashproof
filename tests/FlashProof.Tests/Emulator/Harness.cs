using FlashProof.Core;

namespace FlashProof.Tests.Emulator;

/// <summary>A temporary folder on the host for a test's journal; deleted afterwards.</summary>
public sealed class HostFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashProofTests", Guid.NewGuid().ToString("N"));

    public string JournalPath => System.IO.Path.Combine(Path, "journal.json");

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

public static class Harness
{
    public const long MiB = 1 << 20;
    public const ulong Seed = 0x5EED_F1A5_C4EC_0001;

    /// <summary>
    /// The design's procedure at a small scale, so each emulated drive is 64 MiB: 8 MiB files (the early fake check
    /// runs after each one), 256 KiB I/Os, a 512 KiB reserve and 4 MiB speed slices.
    /// </summary>
    public static readonly FullTestOptions Small = new()
    {
        FileSize = 8 * MiB,
        ChunkSize = 256 << 10,
        ReserveBytes = 512 << 10,
        MinimumFreeSpace = 1 * MiB,
        SpeedSliceBytes = 4 * MiB,
    };

    /// <summary>The request after the user confirmed this device as it is now.</summary>
    public static FullTestRequest Request(SimulatedDevice device, HostFolder host, bool keepTestFiles = false) =>
        new("test", Seed, device.ReadIdentity(), host.JournalPath, keepTestFiles);

    public static Task<TestResult> RunAsync(SimulatedDevice device, HostFolder host, FullTestOptions? options = null,
        TimeProvider? clock = null, bool keepTestFiles = false, CancellationToken ct = default) =>
        FullTestRunner.RunAsync(device, Request(device, host, keepTestFiles), options ?? Small, clock, ct);
}
