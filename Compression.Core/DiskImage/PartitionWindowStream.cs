namespace Compression.Core.DiskImage;

/// <summary>
/// Read-only window onto a sub-range of an underlying disk stream. Position 0 in
/// the window maps to <c>offset</c> in the underlying stream; reads past
/// <c>length</c> return EOF. Used to hand an inner-FS reader a stream that looks
/// like the whole filesystem starts at byte 0, even though it actually lives at
/// the partition's start offset on the host disk.
/// </summary>
public sealed class PartitionWindowStream : Stream {
  private readonly Stream _inner;
  private readonly long _offset;
  private readonly long _length;
  private readonly bool _leaveOpen;
  private long _position;

    /// <summary>
  /// Initializes a new instance of <see cref="PartitionWindowStream"/>.
  /// </summary>
public PartitionWindowStream(Stream inner, long offset, long length, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (!inner.CanSeek) throw new ArgumentException("Underlying stream must be seekable.", nameof(inner));
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
    _inner = inner;
    _offset = offset;
    _length = length;
    _leaveOpen = leaveOpen;
  }

    /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
public override bool CanRead => _inner.CanRead;
    /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
public override bool CanSeek => true;
    /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
public override bool CanWrite => _inner.CanWrite;
    /// <summary>
  /// Gets the length.
  /// </summary>
public override long Length => _length;
    /// <summary>
  /// Gets or sets the position.
  /// </summary>
public override long Position {
    get => _position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      _position = value;
    }
  }

    /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
public override int Read(byte[] buffer, int offset, int count) {
    if (_position >= _length) return 0;
    var remaining = _length - _position;
    if (count > remaining) count = (int)remaining;
    _inner.Position = _offset + _position;
    var n = _inner.Read(buffer, offset, count);
    _position += n;
    return n;
  }

    /// <summary>
  /// Performs the seek operation.
  /// </summary>
public override long Seek(long offset, SeekOrigin origin) {
    _position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => _position + offset,
      SeekOrigin.End => _length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return _position;
  }

    /// <summary>
  /// Performs the flush operation.
  /// </summary>
public override void Flush() => _inner.Flush();
    /// <summary>
  /// Sets the length.
  /// </summary>
public override void SetLength(long value) {
    if (value > _length)
      throw new IOException($"Cannot extend partition window beyond {_length} bytes.");
    // Shrinking the logical view is a no-op; the underlying partition bytes
    // outside [0, value) remain in place as unused space inside the partition.
  }

    /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
public override void Write(byte[] buffer, int offset, int count) {
    if (!_inner.CanWrite) throw new NotSupportedException("Underlying stream is read-only.");
    if (_position + count > _length)
      throw new IOException($"Write at position {_position} for {count} bytes exceeds partition window {_length}.");
    _inner.Position = _offset + _position;
    _inner.Write(buffer, offset, count);
    _position += count;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
protected override void Dispose(bool disposing) {
    if (disposing && !_leaveOpen) _inner.Dispose();
    base.Dispose(disposing);
  }
}
