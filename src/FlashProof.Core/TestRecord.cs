using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FlashProof.Core;

/// <summary>
/// The 512-byte test record (design §6.1). Little-endian:
/// bytes 0–7 session tag, 8–15 record index, 16–503 SplitMix64 fill keyed by (seed, index),
/// 504–511 unkeyed XxHash64 of bytes 0–503.
/// </summary>
public static class TestRecord
{
    public const int Size = 512;

    /// <summary>Stored in the journal; change it whenever the layout or the generator changes.</summary>
    public const int FormatVersion = 1;

    const int FillStart = 16;
    const int FillWords = (ChecksumStart - FillStart) / 8; // 61
    const int ChecksumStart = 504;

    /// <summary>The first 8 bytes of SHA-256 of the seed, read as a little-endian number.</summary>
    public static ulong SessionTag(ulong seed)
    {
        Span<byte> seedBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(seedBytes, seed);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(seedBytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>Fills <paramref name="dest"/> (a whole number of records) with records firstIndex, firstIndex + 1, …</summary>
    public static void Fill(Span<byte> dest, ulong seed, ulong tag, long firstIndex)
    {
        for (int r = 0; r < dest.Length / Size; r++)
            Write(dest.Slice(r * Size, Size), seed, tag, firstIndex + r);
    }

    public static void Write(Span<byte> record, ulong seed, ulong tag, long index)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(record, tag);
        BinaryPrimitives.WriteInt64LittleEndian(record[8..], index);
        // SplitMix64 in counter mode: word n of the test is the n-th SplitMix64 output for this seed.
        ulong word = (ulong)index * FillWords;
        for (int j = 0; j < FillWords; j++)
            BinaryPrimitives.WriteUInt64LittleEndian(record[(FillStart + j * 8)..], SplitMix64.Output(seed, word + (ulong)j));
        BinaryPrimitives.WriteUInt64LittleEndian(record[ChecksumStart..], XxHash64.HashToUInt64(record[..ChecksumStart]));
    }

    public static bool HasValidChecksum(ReadOnlySpan<byte> record) =>
        XxHash64.HashToUInt64(record[..ChecksumStart]) == BinaryPrimitives.ReadUInt64LittleEndian(record[ChecksumStart..]);

    public static ulong TagOf(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record);

    public static long IndexOf(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
}

/// <summary>The SplitMix64 generator (Steele, Lea and Flood), used in counter mode.</summary>
internal static class SplitMix64
{
    const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>The n-th output (from 0) of SplitMix64 started at <paramref name="seed"/>.</summary>
    public static ulong Output(ulong seed, ulong n) => Mix(seed + (n + 1) * Gamma);

    public static ulong Next(ref ulong state) => Mix(state += Gamma);

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>What a record read back turned out to be (design §6.1). Read errors are tracked separately.</summary>
public enum RecordClass
{
    /// <summary>Stored correctly.</summary>
    Ok,

    /// <summary>At most <see cref="RecordClassifier.MaxBitErrors"/> bits differ: the flash is degrading.</summary>
    BitErrors,

    /// <summary>A valid record of this session, but from another position.</summary>
    Misplaced,

    /// <summary>A valid record of another session: the write never happened.</summary>
    Stale,

    /// <summary>All 0x00 or all 0xFF: a dropped write, or empty flash.</summary>
    Blank,

    /// <summary>Anything else: corruption.</summary>
    Garbage,
}

public static class RecordClassifier
{
    /// <summary>Hamming distance up to which a record counts as BIT_ERRORS rather than GARBAGE.</summary>
    public const int MaxBitErrors = 64;

    /// <summary>
    /// Classifies a record read back against the record that was written there.
    /// For MISPLACED, <paramref name="foundIndex"/> is the index the record carries; otherwise -1.
    /// </summary>
    public static RecordClass Classify(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, ulong sessionTag, out long foundIndex)
    {
        foundIndex = -1;
        if (actual.SequenceEqual(expected))
            return RecordClass.Ok;
        if (HammingDistanceAtMost(actual, expected, MaxBitErrors))
            return RecordClass.BitErrors;
        if (TestRecord.HasValidChecksum(actual))
        {
            if (TestRecord.TagOf(actual) != sessionTag)
                return RecordClass.Stale;
            long index = TestRecord.IndexOf(actual);
            if (index != TestRecord.IndexOf(expected))
            {
                foundIndex = index;
                return RecordClass.Misplaced;
            }
        }
        if (!actual.ContainsAnyExcept((byte)0x00) || !actual.ContainsAnyExcept((byte)0xFF))
            return RecordClass.Blank;
        return RecordClass.Garbage;
    }

    static bool HammingDistanceAtMost(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int max)
    {
        var x = MemoryMarshal.Cast<byte, ulong>(a);
        var y = MemoryMarshal.Cast<byte, ulong>(b);
        int distance = 0;
        for (int i = 0; i < x.Length; i++)
        {
            distance += BitOperations.PopCount(x[i] ^ y[i]);
            if (distance > max)
                return false;
        }
        return true;
    }
}
