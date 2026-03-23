namespace FileFormat.Nsis;

/// <summary>
/// A read-only, non-seekable view over a slice of an inner stream starting at the stream's
/// current position when this object is constructed.
/// </summary>
internal sealed class SubStream : Stream {
  private readonly Stream _inner;
  private readonly bool _leaveOpen;
  private readonly long _length;
  private long _position;

  internal SubStream(Stream inner, long length, bool leaveOpen = false) {
    _inner     = inner;
    _length    = length;
    _leaveOpen = leaveOpen;
  }

  public override bool CanRead  => true;
  public override bool CanSeek  => false;
  public override bool CanWrite => false;
  public override long Length   => _length;
  public override long Position {
    get => _position;
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count) {
    var remaining = _length - _position;
    if (remaining <= 0) return 0;
    var toRead = (int)Math.Min(count, remaining);
    var n = _inner.Read(buffer, offset, toRead);
    _position += n;
    return n;
  }

  public override int Read(Span<byte> buffer) {
    var remaining = _length - _position;
    if (remaining <= 0) return 0;
    var slice = buffer.Length <= remaining ? buffer : buffer[..(int)remaining];
    var n = _inner.Read(slice);
    _position += n;
    return n;
  }

  public override void Flush() { }
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing && !_leaveOpen)
      _inner.Dispose();
    base.Dispose(disposing);
  }
}
