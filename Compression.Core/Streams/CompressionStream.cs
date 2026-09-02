namespace Compression.Core.Streams;

/// <summary>
/// Abstract base class for compression/decompression streams.
/// Routes Read/Write operations based on the mode.
/// Subclasses implement the actual compression/decompression logic.
/// </summary>
public abstract class CompressionStream : Stream {
    private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="CompressionStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
  protected CompressionStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false) {
    this.InnerStream = stream ?? throw new ArgumentNullException(nameof(stream));
    this.Mode = mode;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Gets the underlying stream.
  /// </summary>
  protected Stream InnerStream { get; }

  /// <summary>
  /// Gets the compression mode.
  /// </summary>
  public CompressionStreamMode Mode { get; }

  /// <inheritdoc />
  /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
  public override bool CanRead => this.Mode == CompressionStreamMode.Decompress;

  /// <inheritdoc />
  /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
  public override bool CanWrite => this.Mode == CompressionStreamMode.Compress;

  /// <inheritdoc />
  /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
  public override bool CanSeek => false;

  /// <inheritdoc />
  /// <summary>
  /// Gets the length.
  /// </summary>
  public override long Length => throw new NotSupportedException();

  /// <inheritdoc />
  /// <summary>
  /// Gets or sets the position.
  /// </summary>
  public override long Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  /// <inheritdoc />
  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public override int Read(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    return this.Mode != CompressionStreamMode.Decompress 
      ? throw new InvalidOperationException("Cannot read from a compression stream in Compress mode.") 
      : this.DecompressBlock(buffer, offset, count)
      ;

  }

  /// <inheritdoc />
  /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
  public override void Write(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this.Mode != CompressionStreamMode.Compress)
      throw new InvalidOperationException("Cannot write to a compression stream in Decompress mode.");

    this.CompressBlock(buffer, offset, count);
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the flush operation.
  /// </summary>
  public override void Flush() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this.InnerStream.Flush();
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the seek operation.
  /// </summary>
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  /// <inheritdoc />
  /// <summary>
  /// Sets the length.
  /// </summary>
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
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  protected override void Dispose(bool disposing) {
    if (!this._disposed) {
      if (disposing) {
        if (this.Mode == CompressionStreamMode.Compress)
          this.FinishCompression();

        if (!this._leaveOpen)
          this.InnerStream.Dispose();
      }

      this._disposed = true;
    }

    base.Dispose(disposing);
  }
}
