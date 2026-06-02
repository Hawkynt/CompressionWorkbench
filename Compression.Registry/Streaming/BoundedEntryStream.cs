namespace Compression.Registry.Streaming;

/// <summary>
/// A read-only <see cref="Stream"/> whose <see cref="Read(byte[], int, int)"/>
/// never produces more than <see cref="LogicalSize"/> bytes regardless of the
/// underlying stream's state. Reads past the bound return 0 (EOF). Seek
/// targets are clamped to the range <c>[0, LogicalSize]</c>. Disposes the
/// underlying stream when <c>leaveOpen=false</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the per-entry isolation primitive used by archive/filesystem
/// source readers to expose an entry as a stream that is PHYSICALLY incapable
/// of reading slack space (cluster tail past the entry's logical size), the
/// next entry's bytes, padding/alignment fillers, or header/metadata
/// regions. The wrapper subtracts its own <c>_consumed</c> counter from
/// <see cref="LogicalSize"/> and never trusts <c>inner.Position</c>, so a
/// reader that aliases multiple bounded views over the same underlying
/// stream still cannot leak past each view's bound.
/// </para>
/// <para>
/// Source readers wrap their per-entry decoder output (or store passthrough)
/// in a <see cref="BoundedEntryStream"/> sized to the entry's LOGICAL size
/// from format metadata (not the physical allocation). This is the bound that
/// downstream <c>CopyTo</c> / <c>ReadExactly</c> calls actually see.
/// </para>
/// </remarks>
public sealed class BoundedEntryStream : Stream {

  private readonly Stream _inner;
  private readonly bool _leaveOpen;
  private readonly long _logicalSize;
  private long _consumed;
  private bool _disposed;

  /// <summary>The logical entry size — the absolute ceiling on bytes this
  /// stream will ever produce, regardless of the underlying stream.</summary>
  public long LogicalSize => this._logicalSize;

  /// <summary>Sentinel property used by callers to assert that an
  /// <c>OpenEntry</c> override actually returned a bounded stream rather
  /// than a raw decoder. Always <c>true</c> by construction.</summary>
  public bool IsBoundedToSize => true;

  /// <summary>Creates a bounded view of <paramref name="inner"/> capped at
  /// <paramref name="logicalSize"/> bytes. The current position of
  /// <paramref name="inner"/> is treated as the bounded view's position 0.</summary>
  /// <param name="inner">The underlying stream to read through.</param>
  /// <param name="logicalSize">Maximum number of bytes the bounded view will
  /// ever produce. Must be &gt;= 0.</param>
  /// <param name="leaveOpen">When <c>true</c>, disposing this wrapper does
  /// not dispose <paramref name="inner"/>. Defaults to <c>true</c> so callers
  /// can safely compose multiple bounded views over a single reader's
  /// internal stream.</param>
  public BoundedEntryStream(Stream inner, long logicalSize, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (logicalSize < 0)
      throw new ArgumentOutOfRangeException(nameof(logicalSize), "logicalSize must be >= 0.");
    this._inner = inner;
    this._logicalSize = logicalSize;
    this._leaveOpen = leaveOpen;
  }

  public override bool CanRead => !this._disposed && this._inner.CanRead;
  public override bool CanWrite => false;
  public override bool CanSeek => !this._disposed && this._inner.CanSeek;
  public override long Length => this._logicalSize;

  /// <summary>
  /// Position within the bounded view — always equal to the number of
  /// bytes consumed via <see cref="Read(byte[], int, int)"/>. Setting
  /// the position clamps to <c>[0, LogicalSize]</c>.
  /// </summary>
  public override long Position {
    get => this._consumed;
    set {
      if (value < 0)
        throw new ArgumentOutOfRangeException(nameof(value), "Position must be >= 0.");
      this.Seek(value, SeekOrigin.Begin);
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    var remaining = this._logicalSize - this._consumed;
    if (remaining <= 0 || count <= 0) return 0;

    var allowed = (int)Math.Min(count, remaining);
    var n = this._inner.Read(buffer, offset, allowed);
    if (n > 0) this._consumed += n;
    return n;
  }

  public override int Read(Span<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var remaining = this._logicalSize - this._consumed;
    if (remaining <= 0 || buffer.Length == 0) return 0;
    var allowed = (int)Math.Min(buffer.Length, remaining);
    var n = this._inner.Read(buffer[..allowed]);
    if (n > 0) this._consumed += n;
    return n;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (!this._inner.CanSeek)
      throw new NotSupportedException("Underlying stream is not seekable.");

    var requested = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._consumed + offset,
      SeekOrigin.End => this._logicalSize + offset,
      _ => throw new ArgumentException("Invalid SeekOrigin.", nameof(origin)),
    };
    // Clamp to [0, LogicalSize] so callers physically can't address past
    // the bound, even via Seek(End, +N).
    if (requested < 0) requested = 0;
    if (requested > this._logicalSize) requested = this._logicalSize;

    // Mirror the seek on the inner stream so subsequent Read() picks up
    // from the right physical position.
    var delta = requested - this._consumed;
    if (delta != 0) this._inner.Seek(delta, SeekOrigin.Current);
    this._consumed = requested;
    return this._consumed;
  }

  public override void Flush() { /* read-only */ }

  public override void SetLength(long value)
    => throw new NotSupportedException("BoundedEntryStream is read-only.");

  public override void Write(byte[] buffer, int offset, int count)
    => throw new NotSupportedException("BoundedEntryStream is read-only.");

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
