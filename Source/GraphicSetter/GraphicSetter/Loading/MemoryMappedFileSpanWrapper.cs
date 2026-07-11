using System;
using System.IO.MemoryMappedFiles;

namespace GraphicSetter;

internal unsafe sealed class MemoryMappedFileSpanWrapper : IDisposable
{
    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor accessor;
    private readonly long length;
    private byte* pointer;
    private bool disposed;

    public MemoryMappedFileSpanWrapper((MemoryMappedFile file, long length) source, MemoryMappedFileAccess access)
    {
        file = source.file ?? throw new ArgumentNullException(nameof(source));
        length = source.length;
        accessor = file.CreateViewAccessor(0, length, access);
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
    }

    public Span<byte> GetSpan(long offset)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MemoryMappedFileSpanWrapper));
        if (offset < 0 || offset > length || length - offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new Span<byte>(pointer + (int)accessor.PointerOffset + (int)offset, (int)(length - offset));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor.Dispose();
        file.Dispose();
        pointer = null;
    }
}
