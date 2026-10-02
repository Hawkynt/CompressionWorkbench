using System.Buffers;

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
  public override bool CanRead => this.Mode == CompressionStreamMode.Decompress;

  /// <inheritdoc />
  public override bool CanWrite => this.Mode == CompressionStreamMode.Compress;

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

    return this.Mode != CompressionStreamMode.Decompress 
      ? throw new InvalidOperationException("Cannot read from a compression stream in Compress mode.") 
      : this.DecompressBlock(buffer, offset, count)
      ;

  }

  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this.Mode != CompressionStreamMode.Compress)
      throw new InvalidOperationException("Cannot write to a compression stream in Decompress mode.");

    this.CompressBlock(buffer, offset, count);
  }

  /// <inheritdoc />
  public override int Read(Span<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    return this.Mode != CompressionStreamMode.Decompress
      ? throw new InvalidOperationException("Cannot read from a compression stream in Compress mode.")
      : this.DecompressBlock(buffer);
  }

  /// <inheritdoc />
  public override void Write(ReadOnlySpan<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (this.Mode != CompressionStreamMode.Compress)
      throw new InvalidOperationException("Cannot write to a compression stream in Decompress mode.");

    this.CompressBlock(buffer);
  }

  /// <summary>
  /// Decompresses into a span. The default goes through a pooled array and
  /// <see cref="DecompressBlock(byte[], int, int)"/>; codecs that can decode into a span directly
  /// override it.
  /// </summary>
  /// <param name="buffer">Where to put the decompressed bytes.</param>
  /// <returns>The number of bytes decompressed, or 0 at the end of the compressed data.</returns>
  protected virtual int DecompressBlock(Span<byte> buffer) {
    var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
    try {
      var n = this.DecompressBlock(rented, 0, buffer.Length);
      rented.AsSpan(0, n).CopyTo(buffer);
      return n;
    } finally {
      ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>
  /// Compresses a span. The default goes through a pooled array and
  /// <see cref="CompressBlock(byte[], int, int)"/>; codecs that can take a span directly override it.
  /// </summary>
  /// <param name="buffer">The bytes to compress.</param>
  protected virtual void CompressBlock(ReadOnlySpan<byte> buffer) {
    var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
    try {
      buffer.CopyTo(rented);
      this.CompressBlock(rented, 0, buffer.Length);
    } finally {
      ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <inheritdoc />
  public override void Flush() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this.InnerStream.Flush();
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
