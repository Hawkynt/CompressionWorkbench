using Compression.Core.Streams;

namespace Compression.Core.Deflate;

/// <summary>
/// Stream that reads or writes a bare DEFLATE bitstream (RFC 1951) — no zlib or gzip framing.
/// </summary>
/// <remarks>
/// Decompression stops at the final block; when the inner stream is seekable it is left on the
/// first byte after the DEFLATE data, so a container can go on reading whatever follows.
/// Compression streams through <see cref="DeflateCompressor"/>; nothing is buffered beyond its
/// sliding window except at <see cref="DeflateCompressionLevel.Maximum"/>.
/// </remarks>
public sealed class RawDeflateStream : CompressionStream {
  private readonly DeflateDecompressor? _decompressor;
  private readonly DeflateCompressor? _compressor;

  /// <summary>Creates a DEFLATE stream at <see cref="DeflateCompressionLevel.Default"/>.</summary>
  /// <param name="stream">The stream to read compressed data from or write it to.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public RawDeflateStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false)
    : this(stream, mode, DeflateCompressionLevel.Default, leaveOpen) {
  }

  /// <summary>Creates a DEFLATE stream.</summary>
  /// <param name="stream">The stream to read compressed data from or write it to.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="level">The compression level (compress mode only).</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public RawDeflateStream(Stream stream, CompressionStreamMode mode, DeflateCompressionLevel level, bool leaveOpen = false)
    : base(stream, mode, leaveOpen) {
    if (mode == CompressionStreamMode.Compress)
      this._compressor = new(stream, level);
    else
      this._decompressor = new(stream);
  }

  /// <inheritdoc />
  protected override int DecompressBlock(Span<byte> buffer) => this._decompressor!.Decompress(buffer);

  /// <inheritdoc />
  protected override void CompressBlock(ReadOnlySpan<byte> buffer) => this._compressor!.Write(buffer);

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count)
    => this._decompressor!.Decompress(buffer, offset, count);

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count)
    => this._compressor!.Write(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  protected override void FinishCompression() {
    this._compressor!.Finish();
    this.InnerStream.Flush();
  }
}
