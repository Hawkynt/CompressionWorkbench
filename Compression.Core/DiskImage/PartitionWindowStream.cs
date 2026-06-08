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

  public override bool CanRead => _inner.CanRead;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
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
    var remaining = _length - _position;
    if (count > remaining) count = (int)remaining;
    _inner.Position = _offset + _position;
    var n = _inner.Read(buffer, offset, count);
    _position += n;
    return n;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    _position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => _position + offset,
      SeekOrigin.End => _length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return _position;
  }

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing && !_leaveOpen) _inner.Dispose();
    base.Dispose(disposing);
  }
}
