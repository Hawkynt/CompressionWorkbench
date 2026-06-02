namespace Compression.Registry.Streaming;

/// <summary>
/// A write-only <see cref="Stream"/> bounded to exactly <see cref="LogicalSize"/>
/// bytes. Writes past the bound throw <see cref="InvalidOperationException"/>;
/// disposing while the underwrite count is less than <see cref="LogicalSize"/>
/// (and the writer was not explicitly cancelled) also throws. Together these
/// enforce that the caller of <c>CreateFileEntry(name, length)</c> produces
/// exactly the declared number of bytes — overrun is caught at the moment of
/// the offending <c>Write</c>; underrun is caught on close so the archive
/// committer can refuse a torn entry.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the design of <see cref="BoundedEntryStream"/>: the wrapper tracks
/// its own <c>_consumed</c> counter and never trusts the inner stream's
/// <c>Position</c>. That way a caller cannot bypass the bound by seeking the
/// inner stream, and the inner stream may be a buffer (the common case for
/// the <c>ArchiveWriter</c> facade), a tee, or a pre-allocated region of the
/// output without changing the contract.
/// </para>
/// <para>
/// The default mode buffers writes into an internal <see cref="MemoryStream"/>
/// — the writer assembles the entry payload and exposes it on commit. Future
/// optimizations may tee writes directly into a pre-allocated region of the
/// target stream when the underlying format supports it (FAT cluster runs,
/// TAR positional slices); the contract on overrun/underrun stays the same.
/// </para>
/// </remarks>
public sealed class BoundedWriteStream : Stream {

  private readonly Stream _inner;
  private readonly bool _leaveOpen;
  private readonly long _logicalSize;
  private long _consumed;
  private bool _disposed;
  private bool _cancelled;
  private Action<byte[]>? _onCommit;

  /// <summary>The declared entry size — exactly the number of bytes the caller
  /// must write. Overrun throws on <c>Write</c>; underrun throws on
  /// <c>Dispose</c> unless <see cref="Cancel"/> was called first.</summary>
  public long LogicalSize => this._logicalSize;

  /// <summary>Number of bytes already written through this wrapper.</summary>
  public long BytesWritten => this._consumed;

  /// <summary>Sentinel property used by callers to assert that the bounded
  /// write contract is in effect. Always <c>true</c> by construction.</summary>
  public bool IsBoundedToSize => true;

  /// <summary>Creates a bounded write view over <paramref name="inner"/> capped
  /// at <paramref name="logicalSize"/> bytes. The wrapper enforces the bound
  /// regardless of what <paramref name="inner"/> does.</summary>
  /// <param name="inner">The underlying stream that receives the bytes.</param>
  /// <param name="logicalSize">The exact number of bytes the caller must
  /// write before disposing this wrapper. Must be &gt;= 0.</param>
  /// <param name="leaveOpen">When <c>true</c>, disposing this wrapper does not
  /// dispose <paramref name="inner"/>. Defaults to <c>true</c> so the writer
  /// stays open for subsequent entries.</param>
  public BoundedWriteStream(Stream inner, long logicalSize, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (logicalSize < 0)
      throw new ArgumentOutOfRangeException(nameof(logicalSize), "logicalSize must be >= 0.");
    if (!inner.CanWrite)
      throw new ArgumentException("Inner stream must be writable.", nameof(inner));
    this._inner = inner;
    this._logicalSize = logicalSize;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Convenience ctor: buffers writes into an internal <see cref="MemoryStream"/>
  /// and invokes <paramref name="onCommit"/> with the buffered bytes when the
  /// stream is disposed at exactly <paramref name="logicalSize"/> bytes.
  /// </summary>
  /// <remarks>
  /// This is the form the <c>ArchiveWriter</c> facade uses: each entry's payload
  /// is buffered until it's exactly the declared size, then flushed to the
  /// queued archive-input list as a byte[]. The bound itself prevents overrun
  /// during writes; the dispose-time check prevents underrun.
  /// </remarks>
  public static BoundedWriteStream CreateBuffered(long logicalSize, Action<byte[]> onCommit) {
    ArgumentNullException.ThrowIfNull(onCommit);
    var buffer = new MemoryStream(capacity: logicalSize > int.MaxValue ? int.MaxValue : (int)logicalSize);
    var stream = new BoundedWriteStream(buffer, logicalSize, leaveOpen: false) {
      _onCommit = onCommit,
    };
    return stream;
  }

  /// <summary>
  /// Cancels the bound check on dispose. After calling this, disposing the
  /// stream with fewer than <see cref="LogicalSize"/> bytes written will NOT
  /// throw — useful when the writer is being torn down due to a caller-side
  /// failure and the underrun is expected.
  /// </summary>
  public void Cancel() {
    this._cancelled = true;
  }

  public override bool CanRead => false;
  public override bool CanWrite => !this._disposed && this._inner.CanWrite;
  public override bool CanSeek => false;
  public override long Length => this._logicalSize;

  /// <summary>
  /// Position within the bounded view — always equal to the number of bytes
  /// written through this wrapper. Setting the position is not supported.
  /// </summary>
  public override long Position {
    get => this._consumed;
    set => throw new NotSupportedException("BoundedWriteStream is write-only and not seekable.");
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (count <= 0) return;

    var remaining = this._logicalSize - this._consumed;
    if (count > remaining) {
      // Auto-cancel so the impending Dispose (e.g. from the surrounding `using`)
      // doesn't fire a redundant underrun exception on top of this one.
      this._cancelled = true;
      throw new InvalidOperationException(
        $"BoundedWriteStream overrun: attempted to write {this._consumed + count} bytes, declared {this._logicalSize}.");
    }

    this._inner.Write(buffer, offset, count);
    this._consumed += count;
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (buffer.Length == 0) return;

    var remaining = this._logicalSize - this._consumed;
    if (buffer.Length > remaining) {
      this._cancelled = true;
      throw new InvalidOperationException(
        $"BoundedWriteStream overrun: attempted to write {this._consumed + buffer.Length} bytes, declared {this._logicalSize}.");
    }

    this._inner.Write(buffer);
    this._consumed += buffer.Length;
  }

  public override void WriteByte(byte value) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (this._consumed + 1 > this._logicalSize) {
      this._cancelled = true;
      throw new InvalidOperationException(
        $"BoundedWriteStream overrun: attempted to write {this._consumed + 1} bytes, declared {this._logicalSize}.");
    }
    this._inner.WriteByte(value);
    this._consumed += 1;
  }

  public override int Read(byte[] buffer, int offset, int count)
    => throw new NotSupportedException("BoundedWriteStream is write-only.");

  public override long Seek(long offset, SeekOrigin origin)
    => throw new NotSupportedException("BoundedWriteStream is write-only and not seekable.");

  public override void SetLength(long value)
    => throw new NotSupportedException("BoundedWriteStream is write-only.");

  public override void Flush() => this._inner.Flush();

  protected override void Dispose(bool disposing) {
    if (this._disposed) {
      base.Dispose(disposing);
      return;
    }
    this._disposed = true;

    // Underrun check: throw if the caller wrote fewer than declared bytes and
    // did NOT explicitly cancel. This catches torn entries at the boundary
    // they happened.
    var underrun = this._consumed < this._logicalSize && !this._cancelled;

    try {
      if (disposing) {
        // If we own the inner stream (typical buffered case), flush and
        // hand the buffered bytes to onCommit before disposing the buffer.
        // The commit happens only when the entry is fully written; underrun
        // entries are dropped.
        if (!underrun && this._onCommit != null && this._inner is MemoryStream ms) {
          this._onCommit(ms.ToArray());
        }
        if (!this._leaveOpen)
          this._inner.Dispose();
      }
    } finally {
      base.Dispose(disposing);
    }

    if (underrun)
      throw new InvalidOperationException(
        $"BoundedWriteStream underrun: wrote {this._consumed} bytes, declared {this._logicalSize}. " +
        "Call Cancel() before Dispose() if this is intentional.");
  }
}
