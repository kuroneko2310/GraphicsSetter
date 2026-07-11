using System;
using System.IO.MemoryMappedFiles;

namespace GraphicSetter;

internal sealed unsafe class MemoryMappedFileSpanWrapper : IDisposable
{
    private readonly MemoryMappedFile file;
    private readonly long length;
    private readonly MemoryMappedFileAccess access;
    private MemoryMappedViewAccessor accessor;
    private byte* pointer;

    public MemoryMappedFileSpanWrapper((MemoryMappedFile file, long length) data, MemoryMappedFileAccess access)
    {
        file = data.file ?? throw new ArgumentNullException(nameof(data));
        length = data.length;
        this.access = access;
    }

    public Span<byte> GetSpan(long offset)
    {
        if (offset < 0 || offset > length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length - offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(offset), "Mapped DDS files larger than 2 GB are not supported.");

        EnsurePointer();
        return new Span<byte>(pointer + accessor.PointerOffset + offset, (int)(length - offset));
    }

    private void EnsurePointer()
    {
        if (pointer != null)
            return;

        accessor = file.CreateViewAccessor(0, length, access);
        byte* acquiredPointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref acquiredPointer);
        if (acquiredPointer == null)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            accessor = null;
            throw new InvalidOperationException("Failed to acquire a memory-mapped DDS pointer.");
        }

        pointer = acquiredPointer;
    }

    public void Dispose()
    {
        if (pointer != null)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            pointer = null;
        }

        accessor?.Dispose();
        accessor = null;
        file.Dispose();
    }
}
