using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashProof.Core;

public enum JournalStatus
{
    Running,
    Finished,
}

/// <summary>
/// The host-side record of a full test session (design §6.2 step 2): enough to verify, explain and clean up after
/// the test, even if the app crashes. It is stored on the system disk, never on the device under test.
/// </summary>
public sealed class Journal
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string SessionId { get; set; }
    public ulong Seed { get; set; }
    public int RecordFormatVersion { get; set; } = TestRecord.FormatVersion;
    public required VolumeIdentity Identity { get; set; }

    /// <summary>The drive's identity once it was plugged back in and accepted, for a test with a replug (design §6.2 step 7).</summary>
    public VolumeIdentity? IdentityAfterReplug { get; set; }

    public TestPlan? Plan { get; set; }

    /// <summary>Every test file, in test order. Cleanup deletes only these, never by wildcard (design §5.3).</summary>
    public List<TestFileEntry> Files { get; set; } = [];

    public List<Checkpoint> Checkpoints { get; set; } = [];
    public JournalStatus Status { get; set; } = JournalStatus.Running;
    public TestResult? Result { get; set; }
}

public sealed record TestPlan(long DiskSize, long FreeSpace, long PlannedBytes, long FileSize, int ChunkSize);

public sealed class TestFileEntry
{
    /// <summary>Path relative to the volume root.</summary>
    public required string Name { get; set; }

    public long FirstRecordIndex { get; set; }
    public long RecordCount { get; set; }
    public List<FileExtent> Extents { get; set; } = [];
}

public sealed record Checkpoint(TestPhase Phase, int Files, long Bytes, DateTimeOffset At);

/// <summary>The outcome of a full test, as stored in the journal and later exported.</summary>
public sealed class TestResult
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string SessionId { get; set; }
    public required VerdictResult Verdict { get; set; }
    public TestEvidence Evidence { get; set; } = new();
    public SpeedSummary Speed { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }

    /// <summary>True if the test files are still on the device: kept as evidence, at the user's request, or because deleting them failed.</summary>
    public bool TestFilesKept { get; set; }
}

/// <param name="Offset">Position in the test where the slice starts, in bytes.</param>
public sealed record SpeedSlice(long Offset, long Bytes, double Seconds);

/// <summary>Drive speeds in bytes per second. Null when no time was measured.</summary>
public sealed class SpeedSummary
{
    public double? AverageWrite { get; set; }

    /// <summary>The slowest full slice (design §6.4: worst sustained speed).</summary>
    public double? WorstWrite { get; set; }

    public double? AverageRead { get; set; }
    public double? WorstRead { get; set; }
    public List<SpeedSlice> WriteSlices { get; set; } = [];
    public List<SpeedSlice> ReadSlices { get; set; } = [];
}

/// <summary>Saves and loads journals so that a crash at any moment leaves the last saved version intact.</summary>
public static class JournalStore
{
    /// <summary>Writes to a temporary file, flushes it to disk, then renames it over the journal.</summary>
    public static void Save(string path, Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, CoreJson.Default.Journal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static Journal Load(string path)
    {
        using var stream = File.OpenRead(path);
        var journal = JsonSerializer.Deserialize(stream, CoreJson.Default.Journal)
            ?? throw new InvalidDataException("The journal is empty.");
        if (journal.SchemaVersion != Journal.CurrentSchemaVersion)
            throw new InvalidDataException($"Journal schema version {journal.SchemaVersion} isn't supported.");
        return journal;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Journal))]
internal sealed partial class CoreJson : JsonSerializerContext;
