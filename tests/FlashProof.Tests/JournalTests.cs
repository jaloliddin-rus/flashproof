using FlashProof.Core;
using FlashProof.Tests.Emulator;

namespace FlashProof.Tests;

// Design §6.2 step 2 (the host-side journal), the versioned result schema, and §5.2 identity checks.
public class JournalTests
{
    static readonly VolumeIdentity Identity = new(@"USBSTOR\DISK&VEN_SANDISK\4C53", "SanDisk 3.2Gen1", "4C530001", 61_530_439_680, 512, 512,
        new DateTimeOffset(2026, 11, 12, 8, 30, 0, TimeSpan.Zero), 3, "GPT:{0f1e2d3c-0000-4000-8000-000000000001}",
        @"\\?\Volume{1b2c3d4e-0000-0000-0000-000000000002}\", 0xDEADBEEF);

    static Journal Sample()
    {
        var evidence = new TestEvidence
        {
            DiskSize = 64 << 20,
            PlannedBytes = 63 << 20,
            WrittenBytes = 24 << 20,
            VerifiedBytes = 24 << 20,
            FillEnd = FillEnd.EarlyFakeCheck,
            EarlyCheckBadSamples = 3,
            WrapSize = 16 << 20,
            Boundary = new Boundary(20 << 20, 19 << 20),
        };
        evidence.Counts.Add(RecordClass.Ok, 32768);
        evidence.Counts.Add(RecordClass.Misplaced, 16384);
        evidence.MisplacedDeltas[16 << 20] = 16384;
        evidence.BadRuns.Add(new RecordRun(0, 16384, RecordClass.Misplaced));
        evidence.Errors.Add(new IoError(TestPhase.Verify, 1 << 20, IoErrorKind.Other, 23, "Data error (cyclic redundancy check)."));

        return new Journal
        {
            SessionId = "20261112-0930",
            Seed = 0xFEDC_BA98_7654_3210,
            Identity = Identity,
            Plan = new TestPlan(64 << 20, 63 << 20, 63 << 20, 8 << 20, 256 << 10),
            Files = [new TestFileEntry { Name = Path.Combine("FlashProof-x", "fill_0001.bin"), RecordCount = 16384, Extents = [new FileExtent(0, 1 << 19, 8 << 20)] }],
            Checkpoints = [new Checkpoint(TestPhase.Write, 1, 8 << 20, new DateTimeOffset(2026, 11, 12, 9, 31, 0, TimeSpan.Zero))],
            Status = JournalStatus.Finished,
            Result = new TestResult
            {
                SessionId = "20261112-0930",
                Verdict = VerdictEngine.Decide(evidence),
                Evidence = evidence,
                Speed = new SpeedSummary { AverageWrite = 4e6, WorstWrite = 3.5e6, WriteSlices = [new SpeedSlice(0, 4 << 20, 1.05)] },
                TestFilesKept = true,
            },
        };
    }

    [Fact]
    public void Journal_round_trips_every_field()
    {
        using var host = new HostFolder();
        string first = Path.Combine(host.Path, "a.json"), second = Path.Combine(host.Path, "b.json");

        JournalStore.Save(first, Sample());
        var loaded = JournalStore.Load(first);
        JournalStore.Save(second, loaded);

        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
        Assert.Equal(Identity, loaded.Identity);
        Assert.Equal(0xFEDC_BA98_7654_3210, loaded.Seed);
        Assert.Equal(16384, loaded.Result!.Evidence.MisplacedDeltas[16 << 20]);
        Assert.Equal(Verdict.FakeCapacity, loaded.Result.Verdict.Verdict);
    }

    [Fact]
    public void Journal_is_versioned_JSON_with_readable_names()
    {
        using var host = new HostFolder();
        JournalStore.Save(host.JournalPath, Sample());
        string json = File.ReadAllText(host.JournalPath);

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"recordFormatVersion\": 1", json);
        Assert.Contains("\"verdict\": \"FakeCapacity\"", json);
    }

    [Fact]
    public void A_crash_while_saving_leaves_the_last_saved_journal()
    {
        using var host = new HostFolder();
        var journal = Sample();
        journal.Status = JournalStatus.Running;
        JournalStore.Save(host.JournalPath, journal);

        // The next save was cut off halfway through writing its temporary file.
        File.WriteAllText(host.JournalPath + ".tmp", "{ \"schemaVersion\": 1, \"sessio");

        Assert.Equal(JournalStatus.Running, JournalStore.Load(host.JournalPath).Status);

        journal.Status = JournalStatus.Finished;
        JournalStore.Save(host.JournalPath, journal);
        Assert.Equal(JournalStatus.Finished, JournalStore.Load(host.JournalPath).Status);
        Assert.False(File.Exists(host.JournalPath + ".tmp"));
    }

    [Fact]
    public void A_journal_from_a_newer_version_is_refused()
    {
        using var host = new HostFolder();
        var journal = Sample();
        journal.SchemaVersion = 2;
        JournalStore.Save(host.JournalPath, journal);

        Assert.Throws<InvalidDataException>(() => JournalStore.Load(host.JournalPath));
    }

    [Fact]
    public void An_unfinished_session_is_interrupted_and_never_resumed()
    {
        var journal = Sample();
        journal.Status = JournalStatus.Running;
        journal.Result = null;

        var result = FullTestRunner.ResultOf(journal);

        Assert.Equal(Verdict.Interrupted, result.Verdict.Verdict);
        Assert.Equal(InterruptReason.Unfinished, result.Verdict.InterruptReason);
        Assert.True(result.TestFilesKept); // kept as evidence
    }

    public static TheoryData<VolumeIdentity> ChangedIdentities() => new()
    {
        Identity with { DeviceInstanceId = @"USBSTOR\DISK&VEN_SANDISK\4C54" },
        Identity with { VendorProduct = "SanDisk Ultra" },
        Identity with { Serial = "4C530002" },
        Identity with { DiskSize = Identity.DiskSize - 512 },
        Identity with { LogicalSectorSize = 4096 },
        Identity with { PhysicalSectorSize = 4096 },
        Identity with { LastArrival = Identity.LastArrival.AddSeconds(1) },
        Identity with { MediaChangeCount = 4 },
        Identity with { PartitionTableId = "MBR:12345678" },
        Identity with { VolumeGuidPath = @"\\?\Volume{1b2c3d4e-0000-0000-0000-000000000003}\" },
        Identity with { VolumeSerial = 0xDEADBEEE },
    };

    [Theory]
    [MemberData(nameof(ChangedIdentities))]
    public void Any_changed_identity_field_means_a_different_device(VolumeIdentity changed) => Assert.NotEqual(Identity, changed);

    [Fact]
    public void The_same_identity_read_again_is_the_same_device() => Assert.Equal(Identity, Identity with { });
}
