namespace Compression.Core.Streams;

/// <summary>
/// Presents multiple streams as a single seekable, read-only stream.
/// Used for reading multi-volume/split archives where volumes are
/// byte-aligned splits of one logical stream.
/// </summary>
public sealed class ConcatenatedStream : Stream {
  private readonly Stream[] _segments;
  private readonly long[] _offsets; // cumulative start offset of each segment
  private readonly long _totalLength;
  private readonly bool _leaveOpen;
  private long _position;
  private int _currentSegment;
  private bool _disposed;

  /// <summary>
  /// Creates a concatenated view of multiple streams.
  /// </summary>
  /// <param name="segments">The streams to concatenate, in order.</param>
  /// <param name="leaveOpen">Whether to leave the underlying streams open on dispose.</param>
  public ConcatenatedStream(Stream[] segments, bool leaveOpen = false) {
    if (segments == null || segments.Length == 0)
      throw new ArgumentException("At least one segment stream is required.", nameof(segments));

    this._segments = segments;
    this._leaveOpen = leaveOpen;
    this._offsets = new long[segments.Length];

    long cumulative = 0;
    for (var i = 0; i < segments.Length; ++i) {
      this._offsets[i] = cumulative;
      cumulative += segments[i].Length;
    }

    this._totalLength = cumulative;
  }

  /// <inheritdoc />
  public override bool CanRead => true;

  /// <inheritdoc />
  public override bool CanSeek => true;

  /// <inheritdoc />
  public override bool CanWrite => false;

  /// <inheritdoc />
  public override long Length => this._totalLength;

  /// <inheritdoc />
  public override long Position {
    get => this._position;
    set {
      if (value < 0 || value > this._totalLength)
        throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
      UpdateCurrentSegment();
    }
  }

  /// <inheritdoc />
  public override int Read(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    var totalRead = 0;

    while (count > 0 && this._position < this._totalLength) {
      UpdateCurrentSegment();
      var seg = this._segments[this._currentSegment];
      var segOffset = this._position - this._offsets[this._currentSegment];
      var segRemaining = seg.Length - segOffset;

      if (segRemaining <= 0) {
        this._currentSegment++;
        continue;
      }

      var toRead = (int)Math.Min(count, segRemaining);
      seg.Position = segOffset;
      var read = seg.Read(buffer, offset, toRead);
      if (read == 0) break;

      totalRead += read;
      offset += read;
      count -= read;
      this._position += read;
    }

    return totalRead;
  }

  /// <inheritdoc />
  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._totalLength + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    this.Position = newPos;
    return this._position;
  }

  /// <inheritdoc />
  public override void SetLength(long value) => throw new NotSupportedException();

  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) =>
    throw new NotSupportedException();

  /// <inheritdoc />
  public override void Flush() { }

  /// <inheritdoc />
  protected override void Dispose(bool disposing) {
    if (!this._disposed && disposing && !this._leaveOpen) {
      foreach (var seg in this._segments)
        seg.Dispose();
    }

    this._disposed = true;
    base.Dispose(disposing);
  }

  private void UpdateCurrentSegment() {
    if (this._position >= this._totalLength) {
      this._currentSegment = this._segments.Length - 1;
      return;
    }

    // Binary search for the segment containing _position
    int lo = 0, hi = this._segments.Length - 1;
    while (lo < hi) {
      var mid = (lo + hi + 1) / 2;
      if (this._offsets[mid] <= this._position)
        lo = mid;
      else
        hi = mid - 1;
    }

    this._currentSegment = lo;
  }
}
