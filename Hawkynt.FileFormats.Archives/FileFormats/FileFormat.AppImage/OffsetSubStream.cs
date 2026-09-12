namespace FileFormat.AppImage;

/// <summary>
/// A read-only seekable view over a contiguous slice of a seekable inner stream.
/// Used to expose the appended SquashFS payload to <see cref="FileSystem.SquashFs.SquashFsReader"/>
/// without copying the bytes into memory.
/// </summary>
internal sealed class OffsetSubStream : Stream {
  private readonly Stream _inner;
  private readonly long _origin;
  private readonly long _length;
  private readonly bool _leaveOpen;
  private long _position;

  internal OffsetSubStream(Stream inner, long origin, long length, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (!inner.CanSeek)
      throw new ArgumentException("Inner stream must be seekable.", nameof(inner));
    if (origin < 0 || length < 0 || origin + length > inner.Length)
      throw new ArgumentOutOfRangeException(nameof(length), "Slice extends past inner stream.");

    this._inner = inner;
    this._origin = origin;
    this._length = length;
    this._leaveOpen = leaveOpen;
  }

  /// <inheritdoc />
  public override bool CanRead => true;
  /// <inheritdoc />
  public override bool CanSeek => true;
  /// <inheritdoc />
  public override bool CanWrite => false;
  /// <inheritdoc />
  public override long Length => this._length;
  /// <inheritdoc />
  public override long Position {
    get => this._position;
    set {
      if (value < 0 || value > this._length)
        throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  /// <inheritdoc />
  public override int Read(byte[] buffer, int offset, int count) {
    var remaining = this._length - this._position;
    if (remaining <= 0) return 0;
    var toRead = (int)Math.Min(count, remaining);
    this._inner.Position = this._origin + this._position;
    var n = this._inner.Read(buffer, offset, toRead);
    this._position += n;
    return n;
  }

  /// <inheritdoc />
  public override int Read(Span<byte> buffer) {
    var remaining = this._length - this._position;
    if (remaining <= 0) return 0;
    var slice = buffer.Length <= remaining ? buffer : buffer[..(int)remaining];
    this._inner.Position = this._origin + this._position;
    var n = this._inner.Read(slice);
    this._position += n;
    return n;
  }

  /// <inheritdoc />
  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (newPos < 0 || newPos > this._length)
      throw new IOException("Seek past end of sub-stream.");
    this._position = newPos;
    return newPos;
  }

  /// <inheritdoc />
  public override void Flush() { }

  /// <inheritdoc />
  public override void SetLength(long value) => throw new NotSupportedException();

  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  /// <inheritdoc />
  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen)
      this._inner.Dispose();
    base.Dispose(disposing);
  }
}
