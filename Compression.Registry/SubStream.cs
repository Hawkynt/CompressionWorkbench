namespace Compression.Registry;

/// <summary>
/// A stream wrapper that exposes a sub-range of an underlying stream.
/// Position 0 in the SubStream maps to the <c>offset</c> constructor argument in the underlying stream.
/// Used to pass embedded archives (e.g., SFX payloads) to readers that assume Position=0 is the archive start.
/// <para>
/// Handing such a reader the whole containing file instead is a real bug, not a tidiness issue: a ZIP
/// locates its central directory by scanning back from the end of the stream, so it would read the
/// SFX trailer as archive data and fail on the signature.
/// </para>
/// </summary>
public sealed class SubStream : Stream {
  private readonly Stream _inner;
  private readonly long _offset;
  private readonly long _length;
  private long _position;

  public SubStream(Stream inner, long offset, long length) {
    _inner = inner;
    _offset = offset;
    _length = length;
  }

  public override bool CanRead => true;
  public override bool CanSeek => _inner.CanSeek;
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
    var toRead = (int)Math.Min(count, _length - _position);
    _inner.Position = _offset + _position;
    var read = _inner.Read(buffer, offset, toRead);
    _position += read;
    return read;
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

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
