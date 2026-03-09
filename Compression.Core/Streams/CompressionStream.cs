namespace Compression.Core.Streams;

/// <summary>
/// Abstract base class for compression/decompression streams.
/// Routes Read/Write operations based on the mode.
/// Subclasses implement the actual compression/decompression logic.
/// </summary>
public abstract class CompressionStream : Stream {
  private readonly Stream _innerStream;
  private readonly CompressionStreamMode _mode;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="CompressionStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
  protected CompressionStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false) {
    this._innerStream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._mode = mode;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Gets the underlying stream.
  /// </summary>
  protected Stream InnerStream => _innerStream;

  /// <summary>
  /// Gets the compression mode.
  /// </summary>
  public CompressionStreamMode Mode => this._mode;

  /// <inheritdoc />
  public override bool CanRead => this._mode == CompressionStreamMode.Decompress;

  /// <inheritdoc />
  public override bool CanWrite => this._mode == CompressionStreamMode.Compress;

  /// <inheritdoc />
  public override bool CanSeek => false;

  /// <inheritdoc />
  public override long Length => throw new NotSupportedException();

  /// <inheritdoc />
  public override long Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  /// <inheritdoc />
  public override int Read(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._mode != CompressionStreamMode.Decompress)
      throw new InvalidOperationException("Cannot read from a compression stream in Compress mode.");

    return DecompressBlock(buffer, offset, count);
  }

  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this._mode != CompressionStreamMode.Compress)
      throw new InvalidOperationException("Cannot write to a compression stream in Decompress mode.");

    CompressBlock(buffer, offset, count);
  }

  /// <inheritdoc />
  public override void Flush() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this._innerStream.Flush();
  }

  /// <inheritdoc />
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  /// <inheritdoc />
  public override void SetLength(long value) => throw new NotSupportedException();

  /// <summary>
  /// Decompresses data from the inner stream into the provided buffer.
  /// </summary>
  /// <param name="buffer">The buffer to write decompressed data into.</param>
  /// <param name="offset">The offset in the buffer to start writing.</param>
  /// <param name="count">The maximum number of bytes to decompress.</param>
  /// <returns>The number of bytes decompressed, or 0 if the end of the compressed data has been reached.</returns>
  protected abstract int DecompressBlock(byte[] buffer, int offset, int count);

  /// <summary>
  /// Compresses data from the provided buffer and writes it to the inner stream.
  /// </summary>
  /// <param name="buffer">The buffer containing data to compress.</param>
  /// <param name="offset">The offset in the buffer to start reading.</param>
  /// <param name="count">The number of bytes to compress.</param>
  protected abstract void CompressBlock(byte[] buffer, int offset, int count);

  /// <summary>
  /// Called when the stream is being closed in Compress mode.
  /// Implementations should flush any remaining compressed data.
  /// </summary>
  protected virtual void FinishCompression() {
  }

  /// <inheritdoc />
  protected override void Dispose(bool disposing) {
    if (!this._disposed) {
      if (disposing) {
        if (this._mode == CompressionStreamMode.Compress)
          FinishCompression();

        if (!this._leaveOpen)
          this._innerStream.Dispose();
      }

      this._disposed = true;
    }

    base.Dispose(disposing);
  }
}
