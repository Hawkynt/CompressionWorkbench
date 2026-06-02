namespace Compression.Registry.Streaming;

/// <summary>
/// A seekable, read-only window into a base <see cref="Stream"/> exposing the
/// half-open byte range <c>[<see cref="Origin"/>, <see cref="Origin"/> + <see cref="Length"/>)</c>.
/// Reads past the bound return 0; seek targets are clamped to <c>[0, Length]</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the canonical positional-archive primitive: a "game archive" entry
/// table maps each entry name to <c>(offset, size)</c> within the container.
/// Wrapping the source stream in a <see cref="ReadOnlyStreamSlice"/> sized to
/// the entry's logical size produces a per-entry view that is PHYSICALLY
/// incapable of reading the header table, the next entry's bytes, or any
/// trailing padding. Pair with <see cref="BoundedEntryStream"/> on the
/// descriptor's <c>OpenEntry</c> path for the universal contract.
/// </para>
/// <para>
/// The slice does not take ownership of the base stream: by default
/// <c>leaveOpen</c> is <c>true</c> so multiple slices over the same archive
/// can coexist. The base stream's <c>Position</c> is not preserved between
/// slice operations — each <see cref="Read(byte[], int, int)"/> seeks the
/// base stream to the slice's current physical offset first, so callers
/// must not assume the base position is stable across interleaved reads
/// from different slices.
/// </para>
/// </remarks>
public sealed class ReadOnlyStreamSlice : Stream {

  private readonly Stream _inner;
  private readonly long _origin;
  private readonly long _length;
  private readonly bool _leaveOpen;
  private long _position;
  private bool _disposed;

  /// <summary>The absolute byte offset of the slice within <see cref="Inner"/>.</summary>
  public long Origin => this._origin;

  /// <summary>The underlying stream the slice maps onto.</summary>
  public Stream Inner => this._inner;

  /// <summary>Creates a new read-only slice <c>[origin, origin+length)</c>.</summary>
  /// <param name="inner">The base stream — must be readable AND seekable.</param>
  /// <param name="origin">Absolute byte offset where the slice starts.</param>
  /// <param name="length">Number of bytes the slice exposes. Must be &gt;= 0.</param>
  /// <param name="leaveOpen">When <c>true</c> (default) disposing the slice
  /// does NOT dispose the base stream — multiple slices can share an archive.</param>
  public ReadOnlyStreamSlice(Stream inner, long origin, long length, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (!inner.CanRead) throw new ArgumentException("Base stream must be readable.", nameof(inner));
    if (!inner.CanSeek) throw new ArgumentException("Base stream must be seekable.", nameof(inner));
    if (origin < 0) throw new ArgumentOutOfRangeException(nameof(origin), "origin must be >= 0.");
    if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "length must be >= 0.");
    this._inner = inner;
    this._origin = origin;
    this._length = length;
    this._leaveOpen = leaveOpen;
  }

  public override bool CanRead => !this._disposed;
  public override bool CanWrite => false;
  public override bool CanSeek => !this._disposed;
  public override long Length => this._length;

  /// <summary>Position within the slice — clamped to <c>[0, Length]</c>.</summary>
  public override long Position {
    get => this._position;
    set => this.Seek(value, SeekOrigin.Begin);
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var remaining = this._length - this._position;
    if (remaining <= 0 || count <= 0) return 0;
    var allowed = (int)Math.Min(count, remaining);
    this._inner.Position = this._origin + this._position;
    var n = this._inner.Read(buffer, offset, allowed);
    if (n > 0) this._position += n;
    return n;
  }

  public override int Read(Span<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var remaining = this._length - this._position;
    if (remaining <= 0 || buffer.Length == 0) return 0;
    var allowed = (int)Math.Min(buffer.Length, remaining);
    this._inner.Position = this._origin + this._position;
    var n = this._inner.Read(buffer[..allowed]);
    if (n > 0) this._position += n;
    return n;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var requested = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentException("Invalid SeekOrigin.", nameof(origin)),
    };
    if (requested < 0) requested = 0;
    if (requested > this._length) requested = this._length;
    this._position = requested;
    return this._position;
  }

  public override void Flush() { /* read-only */ }

  public override void SetLength(long value)
    => throw new NotSupportedException("ReadOnlyStreamSlice is read-only.");

  public override void Write(byte[] buffer, int offset, int count)
    => throw new NotSupportedException("ReadOnlyStreamSlice is read-only.");

  protected override void Dispose(bool disposing) {
    if (this._disposed) {
      base.Dispose(disposing);
      return;
    }
    this._disposed = true;
    if (disposing && !this._leaveOpen)
      this._inner.Dispose();
    base.Dispose(disposing);
  }
}
