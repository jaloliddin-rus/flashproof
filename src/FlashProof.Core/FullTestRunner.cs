namespace FlashProof.Core;

/// <summary>Sizes and limits of the full test. The defaults are the design's; tests shrink them.</summary>
public sealed record FullTestOptions
{
    /// <summary>Size of each test file; 1 GiB fits FAT32's 4 GiB limit. The early fake check runs after each file.</summary>
    public long FileSize { get; init; } = 1L << 30;

    /// <summary>Bytes per I/O; one I/O in flight (design §5.4).</summary>
    public int ChunkSize { get; init; } = 4 << 20;

    /// <summary>Free space left untested for the file system.</summary>
    public long ReserveBytes { get; init; } = 64L << 20;

    /// <summary>Below this much free space the drive can't be tested (design §3).</summary>
    public long MinimumFreeSpace { get; init; } = 256L << 20;

    /// <summary>Records read back by each early fake check.</summary>
    public int EarlyCheckSamples { get; init; } = 64;

    /// <summary>Speed is recorded per slice of this size (design §6.4).</summary>
    public long SpeedSliceBytes { get; init; } = 256L << 20;

    /// <summary>How often the device identity is re-checked during the test (design §5.2).</summary>
    public TimeSpan IdentityCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How much a test writes into this much free space: all but the reserve, in whole I/Os.</summary>
    public long PlannedBytes(long freeSpace) => (freeSpace - ReserveBytes) / ChunkSize * ChunkSize;
}

/// <param name="ConfirmedIdentity">The identity of the device the user confirmed. Nothing is written to any other.</param>
/// <param name="JournalPath">Where to keep the journal, on the host's system disk.</param>
/// <param name="KeepTestFiles">Keep the test files after a pass, for a later re-check.</param>
public sealed record FullTestRequest(string SessionId, ulong Seed, VolumeIdentity ConfirmedIdentity, string JournalPath, bool KeepTestFiles = false);

/// <summary>Follows a running full test, for the worker's progress messages (design §7). Called from the test itself, so it must return quickly.</summary>
public interface IFullTestObserver
{
    /// <summary>How far the phase has got: bytes for writing and verifying, records for the early fake check.</summary>
    void Progress(TestPhase phase, long done, long total);

    /// <summary>A speed slice of writing or verifying closed (design §6.4).</summary>
    void Slice(TestPhase phase, SpeedSlice slice);

    /// <summary>
    /// Why the caller stopped the test, asked when it ends; null if it didn't, or for a plain cancel. A reason makes the
    /// result Interrupted, whatever errors came with it.
    /// </summary>
    InterruptReason? StopReason { get; }
}

/// <summary>
/// "Unplug and replug before verifying" (design §6.2 step 7): the platform's part. The runner asks for the drive back
/// once writing and flushing have ended and it holds no file open, and decides itself whether what came back is the
/// tested drive (design §5.2). Nothing is written after that; verification only reads.
/// </summary>
public interface IReplug
{
    /// <summary>
    /// Lets go of the drive so it can be unplugged, waits for the next drive plugged in that may be the tested one, and
    /// returns its test volume. Called again after <see cref="Refuse"/>. Cancelling ends the wait, and the test is Interrupted.
    /// </summary>
    Task<ITestVolume> NextAsync(CancellationToken ct);

    /// <summary>The volume <see cref="NextAsync"/> returned isn't the tested drive: let go of it, tell the user, and keep waiting.</summary>
    void Refuse(ReplugRefusal reason);
}

public enum ReplugRefusal
{
    /// <summary>The stable identity fields differ: another drive or card (design §5.2).</summary>
    DifferentDrive,

    /// <summary>The first test file is missing, unreadable, or doesn't carry this session's tag.</summary>
    TestFilesNotFound,
}

/// <summary>
/// The full test of design §6.2 over any <see cref="ITestVolume"/>: fill the free space with records, check early
/// for fakes, verify everything, decide both results, and clean up. Platform-independent.
/// </summary>
public sealed class FullTestRunner
{
    const int MaxBadRuns = 10_000;
    const int MaxDistinctDeltas = 4_096;

    /// <summary>Smallest read of the early fake check: whole sectors even on a drive that under-reports its sector size.</summary>
    const int MinSampleBlock = 4096;

    readonly FullTestRequest request;
    readonly FullTestOptions options;
    readonly TimeProvider clock;
    readonly ulong tag;
    readonly string folder;
    readonly Journal journal;
    readonly TestEvidence evidence = new();
    readonly SpeedMeter writeSpeed, readSpeed;
    readonly IFullTestObserver? observer;
    readonly IReplug? replug;
    ITestVolume volume;

    /// <summary>What every identity check compares with: the confirmed identity, or the one read after a replug.</summary>
    VolumeIdentity identity;

    RecordMap? map;
    long lastIdentityCheck;
    ulong sampleState;
    TestPhase phase = TestPhase.Preflight;
    long phaseOffset;

    FullTestRunner(ITestVolume volume, FullTestRequest request, FullTestOptions options, TimeProvider clock, IFullTestObserver? observer, IReplug? replug)
    {
        this.volume = volume;
        this.request = request;
        this.options = options;
        this.clock = clock;
        this.observer = observer;
        this.replug = replug;
        identity = request.ConfirmedIdentity;
        tag = TestRecord.SessionTag(request.Seed);
        sampleState = request.Seed ^ 0x5A5A_5A5A_5A5A_5A5AUL;
        folder = "FlashProof-" + request.SessionId;
        journal = new Journal { SessionId = request.SessionId, Seed = request.Seed, Identity = request.ConfirmedIdentity };
        writeSpeed = new SpeedMeter(options.SpeedSliceBytes, s => observer?.Slice(TestPhase.Write, s));
        readSpeed = new SpeedMeter(options.SpeedSliceBytes, s => observer?.Slice(TestPhase.Verify, s));
    }

    /// <summary>
    /// Runs the test and returns its result, which is also saved in the journal. Cancelling ends it as Interrupted,
    /// unless the drive ignores cancellation: then the returned task never completes, and the worker must be killed
    /// (design §5.4). The journal then shows an unfinished session; see <see cref="ResultOf"/>.
    /// </summary>
    /// <param name="replug">Set for "Unplug and replug before verifying" (design §6.2 step 7).</param>
    public static Task<TestResult> RunAsync(ITestVolume volume, FullTestRequest request, FullTestOptions? options = null, TimeProvider? clock = null,
        CancellationToken ct = default, IFullTestObserver? observer = null, IReplug? replug = null) =>
        new FullTestRunner(volume, request, options ?? new FullTestOptions(), clock ?? TimeProvider.System, observer, replug).RunAsync(ct);

    /// <summary>The result of a session from its journal. A session that never finished is Interrupted; it is never resumed.</summary>
    public static TestResult ResultOf(Journal journal) =>
        journal.Result ?? new TestResult
        {
            SessionId = journal.SessionId,
            Verdict = VerdictEngine.Decide(new TestEvidence { Interrupted = InterruptReason.Unfinished }),
            TestFilesKept = journal.Files.Count > 0,
        };

    async Task<TestResult> RunAsync(CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        SaveJournal();
        try
        {
            if (Preflight())
            {
                await FillAsync(ct);
                // Not after the early check found a fake: verifying confirms it, and a power cycle can't undo it.
                if (replug is not null && evidence.FillEnd is FillEnd.PlanReached or FillEnd.DiskFull && evidence.WrittenBytes > 0)
                    await ReplugAsync(replug, ct);
                await VerifyAsync(ct);
            }
        }
        catch (StopTest)
        {
        }
        catch (StorageIoException ex)
        {
            evidence.Errors.Add(new IoError(phase, phaseOffset, ex.Kind, ex.NativeCode, ex.Message));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            evidence.Interrupted = InterruptReason.Cancelled;
        }
        if (observer?.StopReason is { } stopped)
            evidence.Interrupted = stopped;

        if (evidence.FillEnd == FillEnd.NotStarted && phase != TestPhase.Preflight)
            evidence.FillEnd = FillEnd.Stopped;
        Analyse();
        var verdict = VerdictEngine.Decide(evidence);
        bool passed = verdict.Verdict is Verdict.PassedCapacityConfirmed or Verdict.PassedCapacityNotChecked;
        bool kept = journal.Files.Count > 0 && (!passed || request.KeepTestFiles || !TryDeleteTestFiles());

        var result = new TestResult
        {
            SessionId = request.SessionId,
            Verdict = verdict,
            Evidence = evidence,
            Speed = Speeds(),
            StartedAt = startedAt,
            FinishedAt = clock.GetUtcNow(),
            TestFilesKept = kept,
        };
        journal.Result = result;
        journal.Status = JournalStatus.Finished;
        SaveJournal();
        return result;
    }

    bool Preflight()
    {
        CheckIdentity(force: true);
        evidence.DiskSize = volume.DiskSize;
        if (volume.VolumeOffset + volume.VolumeSize > volume.DiskSize)
        {
            evidence.CantTest = CantTestReason.VolumeLargerThanDisk;
            return false;
        }
        long free = volume.FreeSpace;
        if (free < options.MinimumFreeSpace)
        {
            evidence.CantTest = CantTestReason.NotEnoughFreeSpace;
            return false;
        }
        evidence.PlannedBytes = options.PlannedBytes(free);
        journal.Plan = new TestPlan(volume.DiskSize, free, evidence.PlannedBytes, options.FileSize, options.ChunkSize);
        SaveJournal();
        return true;
    }

    async Task FillAsync(CancellationToken ct)
    {
        phase = TestPhase.Write;
        volume.CreateFolder(folder);
        using var buffer = new AlignedBuffer(options.ChunkSize, Alignment);
        long written = 0;
        while (evidence.FillEnd == FillEnd.NotStarted)
        {
            CheckIdentity(force: true);
            var entry = new TestFileEntry { Name = Path.Combine(folder, $"fill_{journal.Files.Count + 1:D4}.bin"), FirstRecordIndex = written / TestRecord.Size };
            journal.Files.Add(entry);
            SaveJournal(); // listed before it exists, so a crash can't leave an unlisted file behind
            phaseOffset = written;

            ITestFile file;
            try
            {
                file = volume.CreateFile(entry.Name);
            }
            catch (StorageIoException ex) when (ex.Kind == IoErrorKind.DiskFull)
            {
                journal.Files.Remove(entry);
                evidence.FillEnd = FillEnd.DiskFull;
                break;
            }

            using (file)
            {
                long fileBytes = Math.Min(options.FileSize, evidence.PlannedBytes - written);
                try
                {
                    for (long offset = 0; offset < fileBytes; offset += options.ChunkSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        CheckIdentity(force: false);
                        phaseOffset = written;
                        TestRecord.Fill(buffer.GetSpan(), request.Seed, tag, written / TestRecord.Size);
                        long start = clock.GetTimestamp();
                        int n = await file.WriteAsync(offset, buffer.Memory, ct);
                        if (n != options.ChunkSize)
                            throw ShortTransfer(n, options.ChunkSize);
                        writeSpeed.Add(n, clock.GetElapsedTime(start));
                        written += n;
                        entry.RecordCount += n / TestRecord.Size;
                        evidence.WrittenBytes = written;
                        observer?.Progress(TestPhase.Write, written, evidence.PlannedBytes);
                    }
                }
                catch (StorageIoException ex) when (ex.Kind == IoErrorKind.DiskFull)
                {
                    evidence.FillEnd = FillEnd.DiskFull;
                }
                file.Flush();
                entry.Extents = [.. file.GetExtents()];
            }
            journal.Checkpoints.Add(new Checkpoint(TestPhase.Write, journal.Files.Count, written, clock.GetUtcNow()));
            SaveJournal();

            if (evidence.FillEnd == FillEnd.NotStarted && written >= evidence.PlannedBytes)
                evidence.FillEnd = FillEnd.PlanReached;
            if (evidence.FillEnd == FillEnd.NotStarted && await EarlyCheckFindsFakeAsync(written, ct))
                evidence.FillEnd = FillEnd.EarlyFakeCheck;
        }
    }

    /// <summary>
    /// Reads back randomly chosen records from everything written so far (design §6.2 step 4). On a fake, this fires
    /// soon after more has been written than the drive really holds.
    /// </summary>
    async Task<bool> EarlyCheckFindsFakeAsync(long written, CancellationToken ct)
    {
        phase = TestPhase.EarlyCheck;
        long records = written / TestRecord.Size;
        var samples = new long[records == 0 ? 0 : options.EarlyCheckSamples];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (long)(SplitMix64.Next(ref sampleState) % (ulong)records);
        Array.Sort(samples);

        int block = Math.Max(MinSampleBlock, volume.SectorSize);
        using var buffer = new AlignedBuffer(block, Alignment);
        var expected = new byte[TestRecord.Size];
        bool found = false;
        int checkedSamples = 0;
        TestFileEntry? openEntry = null;
        ITestFile? file = null;
        try
        {
            foreach (long index in samples)
            {
                observer?.Progress(TestPhase.EarlyCheck, checkedSamples++, samples.Length);
                ct.ThrowIfCancellationRequested();
                var entry = journal.Files.Last(f => f.FirstRecordIndex <= index);
                if (entry != openEntry)
                {
                    file?.Dispose();
                    file = volume.OpenFile(entry.Name);
                    openEntry = entry;
                }
                long recordOffset = (index - entry.FirstRecordIndex) * TestRecord.Size;
                long blockOffset = recordOffset / block * block;
                int length = (int)Math.Min(block, entry.RecordCount * TestRecord.Size - blockOffset);
                phaseOffset = index * TestRecord.Size;
                int n = await file!.ReadAsync(blockOffset, buffer.Memory[..length], ct);
                if (n != length)
                    throw ShortTransfer(n, length);

                TestRecord.Write(expected, request.Seed, tag, index);
                var actual = buffer.GetSpan().Slice((int)(recordOffset - blockOffset), TestRecord.Size);
                var c = RecordClassifier.Classify(actual, expected, tag, out _);
                if (c != RecordClass.Ok)
                    evidence.EarlyCheckBadSamples++;
                if (c is RecordClass.Misplaced or RecordClass.Blank or RecordClass.Stale)
                    found = true;
            }
        }
        finally
        {
            file?.Dispose();
        }
        phase = TestPhase.Write;
        return found;
    }

    /// <summary>
    /// Waits for the drive to be unplugged and plugged back in (design §6.2 step 7). What comes back is accepted only if
    /// its stable identity fields match the confirmed drive's and its first test file carries this session's tag
    /// (design §5.2); anything else is refused, and the wait goes on. Nothing is written from here on.
    /// </summary>
    async Task ReplugAsync(IReplug replug, CancellationToken ct)
    {
        phase = TestPhase.Replug;
        phaseOffset = evidence.WrittenBytes;
        journal.Checkpoints.Add(new Checkpoint(TestPhase.Replug, journal.Files.Count, evidence.WrittenBytes, clock.GetUtcNow()));
        SaveJournal();
        while (true)
        {
            var candidate = await replug.NextAsync(ct);
            var (now, refusal) = await CheckReplugAsync(candidate, ct);
            if (refusal is { } why)
            {
                replug.Refuse(why);
                continue;
            }
            volume = candidate;
            identity = now!;
            evidence.Replugged = true;
            journal.IdentityAfterReplug = now;
            SaveJournal();
            return;
        }
    }

    async Task<(VolumeIdentity? Identity, ReplugRefusal? Refusal)> CheckReplugAsync(ITestVolume candidate, CancellationToken ct)
    {
        try
        {
            var now = candidate.ReadIdentity();
            if (!request.ConfirmedIdentity.SameDriveAs(now))
                return (now, ReplugRefusal.DifferentDrive);
            var first = journal.Files.First(f => f.RecordCount > 0);
            int block = Math.Max(MinSampleBlock, candidate.SectorSize);
            int length = (int)Math.Min(block, first.RecordCount * TestRecord.Size);
            using var buffer = new AlignedBuffer(block, block);
            using var file = candidate.OpenFile(first.Name);
            int n = await file.ReadAsync(0, buffer.Memory[..length], ct);
            return n == length && TestRecord.TagOf(buffer.GetSpan()) == tag ? (now, null) : (now, ReplugRefusal.TestFilesNotFound);
        }
        catch (StorageIoException)
        {
            return (null, ReplugRefusal.TestFilesNotFound);
        }
    }

    async Task VerifyAsync(CancellationToken ct)
    {
        phase = TestPhase.Verify;
        CheckIdentity(force: true); // a card swapped since the last check must not be read back as a fake
        map = new RecordMap(journal.Files);
        using var buffer = new AlignedBuffer(options.ChunkSize, Alignment);
        var expected = new byte[options.ChunkSize];
        foreach (var entry in journal.Files)
        {
            if (entry.RecordCount == 0)
                continue;
            using var file = volume.OpenFile(entry.Name);
            long fileBytes = entry.RecordCount * TestRecord.Size;
            for (long offset = 0; offset < fileBytes; offset += options.ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                CheckIdentity(force: false);
                int length = (int)Math.Min(options.ChunkSize, fileBytes - offset);
                long first = entry.FirstRecordIndex + offset / TestRecord.Size;
                phaseOffset = first * TestRecord.Size;
                long start = clock.GetTimestamp();
                int n = await file.ReadAsync(offset, buffer.Memory[..length], ct);
                if (n != length)
                    throw ShortTransfer(n, length);
                readSpeed.Add(n, clock.GetElapsedTime(start));

                TestRecord.Fill(expected.AsSpan(0, length), request.Seed, tag, first);
                Classify(buffer.GetSpan()[..length], expected.AsSpan(0, length), first);
                evidence.VerifiedBytes += length;
                observer?.Progress(TestPhase.Verify, evidence.VerifiedBytes, evidence.WrittenBytes);
            }
        }
        CheckIdentity(force: true);
        journal.Checkpoints.Add(new Checkpoint(TestPhase.Verify, journal.Files.Count, evidence.VerifiedBytes, clock.GetUtcNow()));
        SaveJournal();
    }

    void Classify(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, long firstIndex)
    {
        if (actual.SequenceEqual(expected))
        {
            evidence.Counts.Ok += actual.Length / TestRecord.Size;
            return;
        }
        for (int r = 0; r < actual.Length / TestRecord.Size; r++)
        {
            var span = new Range(r * TestRecord.Size, (r + 1) * TestRecord.Size);
            var c = RecordClassifier.Classify(actual[span], expected[span], tag, out long foundIndex);
            evidence.Counts.Add(c, 1);
            if (c == RecordClass.Ok)
                continue;
            AddBadRecord(firstIndex + r, c);
            if (c == RecordClass.Misplaced)
                AddMisplaced(firstIndex + r, foundIndex);
        }
    }

    void AddBadRecord(long index, RecordClass c)
    {
        var runs = evidence.BadRuns;
        if (runs.Count > 0 && runs[^1].Class == c && runs[^1].StartIndex + runs[^1].Count == index)
            runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
        else if (runs.Count < MaxBadRuns)
            runs.Add(new RecordRun(index, 1, c));
        else
            evidence.BadRunsTruncated = true;
    }

    /// <summary>Records how far on disk the record found here is from where it was written (design §6.1).</summary>
    void AddMisplaced(long index, long foundIndex)
    {
        long delta = map!.DiskOffsetOf(index) is { } here && map.DiskOffsetOf(foundIndex) is { } there ? Math.Abs(there - here) : 0;
        var deltas = evidence.MisplacedDeltas;
        if (!deltas.ContainsKey(delta) && deltas.Count >= MaxDistinctDeltas)
            delta = 0;
        deltas[delta] = deltas.GetValueOrDefault(delta) + 1;
    }

    void Analyse()
    {
        if (map is null || evidence.VerifiedBytes == 0)
            return;
        evidence.WrapSize = PatternAnalysis.FindWrapSize(evidence.MisplacedDeltas, evidence.Counts.Ok);
        if (evidence.BadRunsTruncated)
            return;
        var tested = map.Map(0, evidence.VerifiedBytes / TestRecord.Size).ToList();
        var blank = evidence.BadRuns
            .Where(r => r.Class is RecordClass.Blank or RecordClass.Stale)
            .SelectMany(r => map.Map(r.StartIndex, r.Count))
            .ToList();
        evidence.Boundary = PatternAnalysis.FindBoundary(tested, blank);
    }

    /// <summary>
    /// Stops the test if the device or card is no longer the one the user confirmed (design §5.2): before writing
    /// starts, before each new test file, every 30 s, and at the start and end of verification.
    /// </summary>
    void CheckIdentity(bool force)
    {
        if (!force && clock.GetElapsedTime(lastIdentityCheck) < options.IdentityCheckInterval)
            return;
        lastIdentityCheck = clock.GetTimestamp();
        if (volume.ReadIdentity() != identity)
        {
            evidence.Interrupted = InterruptReason.DeviceChanged;
            throw new StopTest();
        }
    }

    /// <summary>Deletes the files the journal lists, then the session folder (design §5.3).</summary>
    bool TryDeleteTestFiles()
    {
        try
        {
            foreach (var file in journal.Files)
                volume.DeleteFile(file.Name);
            volume.DeleteFolder(folder);
            return true;
        }
        catch (StorageIoException)
        {
            return false;
        }
    }

    SpeedSummary Speeds()
    {
        var (averageWrite, worstWrite) = writeSpeed.Finish();
        var (averageRead, worstRead) = readSpeed.Finish();
        return new SpeedSummary
        {
            AverageWrite = averageWrite,
            WorstWrite = worstWrite,
            AverageRead = averageRead,
            WorstRead = worstRead,
            WriteSlices = writeSpeed.Slices,
            ReadSlices = readSpeed.Slices,
        };
    }

    int Alignment => Math.Max(MinSampleBlock, volume.SectorSize);

    void SaveJournal() => JournalStore.Save(request.JournalPath, journal);

    static StorageIoException ShortTransfer(int done, int requested) =>
        new(IoErrorKind.Other, 0, $"The drive transferred {done} of {requested} bytes.");

    sealed class StopTest : Exception;

    /// <summary>Drive speed per slice, timing only the I/O calls.</summary>
    sealed class SpeedMeter(long sliceBytes, Action<SpeedSlice> onSlice)
    {
        public List<SpeedSlice> Slices { get; } = [];
        long sliceStart, bytes, totalBytes;
        TimeSpan time, totalTime;

        public void Add(long n, TimeSpan elapsed)
        {
            bytes += n;
            time += elapsed;
            totalBytes += n;
            totalTime += elapsed;
            if (bytes >= sliceBytes)
                CloseSlice();
        }

        public (double? Average, double? Worst) Finish()
        {
            if (bytes > 0)
                CloseSlice();
            if (totalTime <= TimeSpan.Zero)
                return (null, null);
            double average = totalBytes / totalTime.TotalSeconds;
            var full = Slices.Where(s => s.Bytes >= sliceBytes && s.Seconds > 0).ToList();
            return (average, full.Count > 0 ? full.Min(s => s.Bytes / s.Seconds) : average);
        }

        void CloseSlice()
        {
            var slice = new SpeedSlice(sliceStart, bytes, time.TotalSeconds);
            Slices.Add(slice);
            onSlice(slice);
            sliceStart += bytes;
            bytes = 0;
            time = TimeSpan.Zero;
        }
    }
}
