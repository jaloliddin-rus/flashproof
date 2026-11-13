using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using FlashProof.Core;

namespace FlashProof.Tests;

// Design §6.1: the record layout, the generator and the classifier.
public class TestRecordTests
{
    const ulong Seed = 0x0123_4567_89AB_CDEF;
    static readonly ulong Tag = TestRecord.SessionTag(Seed);

    static byte[] Record(long index, ulong seed = Seed)
    {
        var record = new byte[TestRecord.Size];
        TestRecord.Write(record, seed, TestRecord.SessionTag(seed), index);
        return record;
    }

    static RecordClass Classify(byte[] actual, long expectedIndex) =>
        RecordClassifier.Classify(actual, Record(expectedIndex), Tag, out _);

    static byte[] FlipBits(byte[] record, int count)
    {
        var copy = (byte[])record.Clone();
        for (int i = 0; i < count; i++)
        {
            int bit = i * 63; // distinct bits spread over the whole record
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
        }
        return copy;
    }

    // ---- Layout and generator

    [Fact]
    public void Layout_matches_the_design()
    {
        var record = Record(7);
        var seedBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(seedBytes, Seed);

        Assert.Equal(SHA256.HashData(seedBytes)[..8], record[..8]);
        Assert.Equal(7, BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8)));
        Assert.Equal(XxHash64.HashToUInt64(record.AsSpan(0, 504)), BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(504)));
        Assert.True(TestRecord.HasValidChecksum(record));
    }

    [Fact]
    public void Fill_is_standard_SplitMix64()
    {
        // The reference SplitMix64 sequence for seed 0 starts e220a8397b1dcdaf, 6e789e6aa1b965f4, 06c45d188009454f.
        var record = Record(0, seed: 0);

        Assert.Equal(0xE220A8397B1DCDAFUL, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16)));
        Assert.Equal(0x6E789E6AA1B965F4UL, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24)));
        Assert.Equal(0x06C45D188009454FUL, BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32)));
    }

    [Fact]
    public void Generator_is_deterministic()
    {
        var three = new byte[3 * TestRecord.Size];
        TestRecord.Fill(three, Seed, Tag, 41);

        Assert.Equal(Record(41), Record(41));
        Assert.Equal([.. Record(41), .. Record(42), .. Record(43)], three);
    }

    [Fact]
    public void Fill_depends_on_index_and_seed()
    {
        Assert.NotEqual(Record(5)[16..504], Record(6)[16..504]);
        Assert.NotEqual(Record(5)[16..504], Record(5, seed: Seed + 1)[16..504]);
    }

    [Fact]
    public void Format_is_unchanged()
    {
        // Pins format version 1. If this fails, the generator changed: bump TestRecord.FormatVersion.
        Assert.Equal(1, TestRecord.FormatVersion);
        Assert.Equal(10937383014588070154UL, BinaryPrimitives.ReadUInt64LittleEndian(Record(0).AsSpan(504)));
        Assert.Equal(15457524894563384513UL, BinaryPrimitives.ReadUInt64LittleEndian(Record(123_456_789).AsSpan(504)));
    }

    // ---- Classifier

    [Fact]
    public void Identical_record_is_OK() => Assert.Equal(RecordClass.Ok, Classify(Record(5), 5));

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void Up_to_64_flipped_bits_are_BIT_ERRORS(int bits) =>
        Assert.Equal(RecordClass.BitErrors, Classify(FlipBits(Record(5), bits), 5));

    [Fact]
    public void Sixty_five_flipped_bits_are_GARBAGE() =>
        Assert.Equal(RecordClass.Garbage, Classify(FlipBits(Record(5), 65), 5));

    [Fact]
    public void A_flipped_bit_in_the_index_is_BIT_ERRORS_not_MISPLACED()
    {
        var record = Record(5);
        record[8] ^= 0x02; // index now reads 7

        Assert.Equal(RecordClass.BitErrors, Classify(record, 5));
    }

    [Fact]
    public void This_sessions_record_from_another_position_is_MISPLACED()
    {
        Assert.Equal(RecordClass.Misplaced, RecordClassifier.Classify(Record(9), Record(5), Tag, out long found));
        Assert.Equal(9, found);
    }

    [Fact]
    public void Another_sessions_record_is_STALE()
    {
        Assert.Equal(RecordClass.Stale, Classify(Record(5, seed: 42), 5));
        Assert.Equal(RecordClass.Stale, Classify(Record(9, seed: 42), 5));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void Erased_flash_is_BLANK(byte value) =>
        Assert.Equal(RecordClass.Blank, Classify(Enumerable.Repeat(value, TestRecord.Size).ToArray(), 5));

    [Fact]
    public void Random_data_is_GARBAGE()
    {
        var noise = new byte[TestRecord.Size];
        new Random(1).NextBytes(noise);

        Assert.Equal(RecordClass.Garbage, Classify(noise, 5));
    }
}
