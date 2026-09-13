using Compression.Core.Dictionary.Zstd;
using Compression.Core.Streams;

namespace FileFormat.Zstd;

/// <summary>
/// Stream for reading and writing Zstandard (zstd) compressed data (RFC 8878).
/// Wraps an underlying stream and provides transparent compression or decompression.
/// </summary>
public sealed class ZstdStream : CompressionStream {
  private readonly int _compressionLevel;
  private readonly ZstdDictionary? _dictionary;
  private ZstdDecompressor? _decompressor;
  private ZstdCompressor? _compressor;
  private bool _initialized;

  /// <summary>
  /// Initializes a new <see cref="ZstdStream"/>.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="compressionLevel">Compression level (1-9). Default 3.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open when this stream is disposed.</param>
  /// <param name="dictionary">Optional Zstd dictionary for prepopulating the window.</param>
  public ZstdStream(Stream stream, CompressionStreamMode mode,
    int compressionLevel = 3, bool leaveOpen = false,
    ZstdDictionary? dictionary = null)
    : base(stream, mode, leaveOpen) {
    this._compressionLevel = compressionLevel;
    this._dictionary = dictionary;
  }

  /// <summary>
  /// Initializes a new <see cref="ZstdStream"/> with a typed compression level.
  /// </summary>
  /// <param name="stream">The underlying stream.</param>
  /// <param name="mode">Whether to compress or decompress.</param>
  /// <param name="level">The compression level.</param>
  /// <param name="leaveOpen">Whether to leave the inner stream open when this stream is disposed.</param>
  /// <param name="dictionary">Optional Zstd dictionary for prepopulating the window.</param>
  public ZstdStream(Stream stream, CompressionStreamMode mode,
    ZstdCompressionLevel level, bool leaveOpen = false,
    ZstdDictionary? dictionary = null)
    : this(stream, mode, (int)level, leaveOpen, dictionary) { }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count) {
    if (!this._initialized) {
      this._decompressor = new ZstdDecompressor(InnerStream, this._dictionary);
      this._initialized = true;
    }

    if (this._decompressor!.IsFinished)
      return 0;

    return this._decompressor.Read(buffer, offset, count);
  }

  /// <summary>
  /// Input accumulated per frame before one is emitted. ZstdCompressor holds the
  /// whole frame in memory, so without a cap a large payload both exhausts the
  /// array limit and scales memory with the input.
  /// </summary>
  private const long FrameFlushThreshold = 64L * 1024 * 1024;

  private long _pendingFrameBytes;

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count) {
    if (!this._initialized) {
      this._compressor = new ZstdCompressor(InnerStream, this._compressionLevel, this._dictionary);
      this._initialized = true;
      this._pendingFrameBytes = 0;
    }

    this._compressor!.Write(buffer.AsSpan(offset, count));
    this._pendingFrameBytes += count;

    // A .zst file is a sequence of frames, so closing the current one and opening
    // another is a valid continuation that every compliant decoder concatenates.
    if (this._pendingFrameBytes >= FrameFlushThreshold) {
      this._compressor.Finish();
      this._compressor = new ZstdCompressor(InnerStream, this._compressionLevel, this._dictionary);
      this._pendingFrameBytes = 0;
    }
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    if (!this._initialized) {
      this._compressor = new ZstdCompressor(InnerStream, this._compressionLevel, this._dictionary);
      this._initialized = true;
    }

    this._compressor!.Finish();
  }
}
