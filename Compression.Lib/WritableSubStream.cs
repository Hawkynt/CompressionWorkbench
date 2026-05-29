#pragma warning disable CS1591

namespace Compression.Lib;

/// <summary>
/// A stream wrapper that exposes a read/write sub-range of an underlying stream.
/// Position 0 in this stream maps to <paramref name="offset"/> in the underlying stream.
/// Used by <see cref="NestedStreamResolver"/> to provide writable access to a partition
/// within a virtual disk stream.
/// </summary>
public sealed class WritableSubStream : Stream {
  private readonly Stream _inner;
  private readonly long _offset;
  private readonly long _length;
  private long _position;

  public WritableSubStream(Stream inner, long offset, long length) {
    _inner = inner;
    _offset = offset;
    _length = length;
  }

  public override bool CanRead => _inner.CanRead;
  public override bool CanSeek => _inner.CanSeek;
  public override bool CanWrite => _inner.CanWrite;
  public override long Length => _length;

  public override long Position {
    get => _position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      _position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    if (_position >= _length) return 0;
    var toRead = (int)Math.Min(count, _length - _position);
    _inner.Position = _offset + _position;
    var read = _inner.Read(buffer, offset, toRead);
    _position += read;
    return read;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    if (_position + count > _length)
      throw new IOException("Write would exceed sub-stream bounds.");
    _inner.Position = _offset + _position;
    _inner.Write(buffer, offset, count);
    _position += count;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => _position + offset,
      SeekOrigin.End => _length + offset,
      _ => throw new ArgumentException("Invalid SeekOrigin", nameof(origin)),
    };
    if (newPos < 0) throw new IOException("Seek before beginning of stream.");
    _position = newPos;
    return _position;
  }

  public override void Flush() => _inner.Flush();
  public override void SetLength(long value) => throw new NotSupportedException();
}
