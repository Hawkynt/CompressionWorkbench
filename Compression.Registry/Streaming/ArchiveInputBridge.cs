namespace Compression.Registry.Streaming;

/// <summary>
/// Bridges forward-only archive input to the seek-based reader implementations
/// that pre-date the explicit streaming API. Seekable inputs are passed through;
/// forward-only inputs are spooled to a temporary file so archive size is not
/// bounded by managed-array limits.
/// </summary>
internal sealed class SeekableArchiveInputLease : IDisposable {
  private const int CopyBufferSize = 128 * 1024;

  private readonly bool _ownsStream;

  /// <summary>The readable, seekable view used by the format implementation.</summary>
  public Stream Stream { get; }

  private SeekableArchiveInputLease(Stream stream, bool ownsStream) {
    this.Stream = stream;
    this._ownsStream = ownsStream;
  }

  /// <summary>
  /// Returns a seekable view over <paramref name="source"/>. When the source is
  /// already seekable it is rewound and borrowed; otherwise the remaining bytes
  /// are copied to a delete-on-close temporary file owned by the lease.
  /// </summary>
  public static SeekableArchiveInputLease Open(Stream source) {
    ArgumentNullException.ThrowIfNull(source);
    if (!source.CanRead)
      throw new ArgumentException("Archive input must be readable.", nameof(source));

    if (source.CanSeek) {
      source.Position = 0;
      return new SeekableArchiveInputLease(source, ownsStream: false);
    }

    var path = Path.Combine(Path.GetTempPath(), $"cwb-archive-{Guid.NewGuid():N}.tmp");
    FileStream? spool = null;
    try {
      spool = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.Read,
        CopyBufferSize,
        FileOptions.DeleteOnClose | FileOptions.SequentialScan);
      source.CopyTo(spool, CopyBufferSize);
      spool.Position = 0;
      return new SeekableArchiveInputLease(spool, ownsStream: true);
    } catch {
      spool?.Dispose();
      try { File.Delete(path); } catch { /* best-effort cleanup */ }
      throw;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._ownsStream)
      this.Stream.Dispose();
  }
}

/// <summary>
/// Keeps an auxiliary archive-input owner alive for exactly as long as the
/// returned entry stream. This is required when a forward-only archive was
/// spooled before <see cref="Compression.Registry.IArchiveFormatOperations.OpenEntryStreaming"/>.
/// </summary>
internal sealed class OwnedArchiveEntryStream : Stream {
  private readonly Stream _inner;
  private readonly IDisposable _owner;
  private bool _disposed;

  public OwnedArchiveEntryStream(Stream inner, IDisposable owner) {
    ArgumentNullException.ThrowIfNull(inner);
    ArgumentNullException.ThrowIfNull(owner);
    this._inner = inner;
    this._owner = owner;
  }

  public override bool CanRead => !this._disposed && this._inner.CanRead;
  public override bool CanSeek => !this._disposed && this._inner.CanSeek;
  public override bool CanWrite => !this._disposed && this._inner.CanWrite;
  public override long Length => this._inner.Length;

  public override long Position {
    get => this._inner.Position;
    set => this._inner.Position = value;
  }

  public override void Flush() => this._inner.Flush();
  public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
  public override int Read(Span<byte> buffer) => this._inner.Read(buffer);
  public override long Seek(long offset, SeekOrigin origin) => this._inner.Seek(offset, origin);
  public override void SetLength(long value) => this._inner.SetLength(value);
  public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);
  public override void Write(ReadOnlySpan<byte> buffer) => this._inner.Write(buffer);

  protected override void Dispose(bool disposing) {
    if (this._disposed) {
      base.Dispose(disposing);
      return;
    }

    this._disposed = true;
    if (disposing)
      try {
        this._inner.Dispose();
      } finally {
        this._owner.Dispose();
      }
    base.Dispose(disposing);
  }
}
