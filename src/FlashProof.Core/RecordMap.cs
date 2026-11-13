namespace FlashProof.Core;

/// <summary>A range of the disk, in bytes.</summary>
public readonly record struct DiskRange(long Offset, long Length);

/// <summary>
/// Maps every record index of a test to its disk position, using the test files' extents (design §6.1).
/// </summary>
public sealed class RecordMap
{
    readonly List<Piece> pieces = [];

    public RecordMap(IEnumerable<TestFileEntry> files)
    {
        foreach (var file in files)
        {
            foreach (var extent in file.Extents)
            {
                long first = extent.FileOffset / TestRecord.Size;
                long count = Math.Min(extent.Length / TestRecord.Size, file.RecordCount - first);
                if (count > 0)
                    pieces.Add(new Piece(file.FirstRecordIndex + first, extent.DiskOffset, count));
            }
        }
        pieces.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
    }

    /// <summary>The disk offset of a record, or null if the index isn't part of the test.</summary>
    public long? DiskOffsetOf(long index)
    {
        int i = PieceAt(index);
        return i < 0 ? null : pieces[i].DiskOffset + (index - pieces[i].StartIndex) * TestRecord.Size;
    }

    /// <summary>The disk ranges that hold records startIndex … startIndex + count − 1.</summary>
    public IEnumerable<DiskRange> Map(long startIndex, long count)
    {
        long end = startIndex + count;
        for (int i = Math.Max(PieceAt(startIndex), 0); i < pieces.Count && startIndex < end; i++)
        {
            var p = pieces[i];
            long from = Math.Max(startIndex, p.StartIndex), to = Math.Min(end, p.StartIndex + p.Count);
            if (from >= to)
                continue;
            yield return new DiskRange(p.DiskOffset + (from - p.StartIndex) * TestRecord.Size, (to - from) * TestRecord.Size);
            startIndex = to;
        }
    }

    int PieceAt(long index)
    {
        int lo = 0, hi = pieces.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var p = pieces[mid];
            if (index < p.StartIndex)
                hi = mid - 1;
            else if (index >= p.StartIndex + p.Count)
                lo = mid + 1;
            else
                return mid;
        }
        return -1;
    }

    readonly record struct Piece(long StartIndex, long DiskOffset, long Count);
}
