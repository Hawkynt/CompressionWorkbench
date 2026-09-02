using System;

namespace OggVorbisEncoder;

/// <summary>
/// Represents an offset memory.
/// </summary>
public class OffsetMemory<T>
{
    private readonly Memory<T> _memory;

    /// <summary>
    /// Initializes a new instance of <see cref="OffsetMemory"/>.
    /// </summary>
    public OffsetMemory(in Memory<T> memory, int offset)
    {
        _memory = memory;
        Offset = offset;
    }

    /// <summary>
    /// Gets the offset.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// Gets or sets the value at the specified index.
    /// </summary>
    public T this[int index]
    {
        get { return _memory.Span[index]; }
    }
}
