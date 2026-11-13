using System.Buffers;
using System.Runtime.InteropServices;

namespace FlashProof.Core;

/// <summary>
/// A native buffer aligned for unbuffered I/O (design §6.2). The runner disposes it only after every I/O that
/// uses it has finished, so it is never freed while an I/O is pending.
/// </summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    readonly byte* pointer;
    readonly int length;
    bool freed;

    public AlignedBuffer(int length, int alignment)
    {
        pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        this.length = length;
    }

    public override Span<byte> GetSpan() => new(pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (freed)
            return;
        freed = true;
        NativeMemory.AlignedFree(pointer);
    }
}
