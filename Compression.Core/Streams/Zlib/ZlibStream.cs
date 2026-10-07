using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Streams;

namespace FileFormat.Zlib;

/// <summary>
/// Compresses and decompresses data in the zlib format (RFC 1950), either as a
/// <see cref="Stream"/> or through the one-shot static helpers.
/// </summary>
/// <remarks>
/// <para>The zlib format wraps a Deflate stream with a 2-byte header and a 4-byte Adler-32
/// checksum trailer. The header encodes the compression method (always Deflate), window size,
/// and compression level.</para>
/// <para>Both directions stream: compression keeps only the Deflate window in memory, and
/// decompression stops at the end of the Deflate data, checks the Adler-32 trailer and leaves
/// anything after it unread (on a seekable inner stream the position is exactly past the
/// trailer). A checksum mismatch or a malformed header raises <see cref="InvalidDataException"/>.</para>
/// </remarks>
public sealed class ZlibStream : CompressionStream {
  private readonly Adler32 _adler = new();
  private readonly DeflateCompressionLevel _level;
  private readonly int _windowBits;

  private DeflateCompressor? _compressor;
  private DeflateDecompressor? _decompressor;
  private bool _ended;

  /// <summary>Creates a zlib stream at <see cref="DeflateCompressionLevel.Default"/>.</summary>
  /// <param name="stream">The stream to read zlib data from or write it to.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public ZlibStream(Stream stream, CompressionStreamMode mode, bool leaveOpen = false)
    : this(stream, mode, DeflateCompressionLevel.Default, ZlibConstants.DefaultWindowBits, leaveOpen) {
  }

  /// <summary>Creates a zlib stream.</summary>
  /// <param name="stream">The stream to read zlib data from or write it to.</param>
  /// <param name="mode">Whether this stream compresses or decompresses.</param>
  /// <param name="level">The Deflate compression level (compress mode only).</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  public ZlibStream(Stream stream, CompressionStreamMode mode, DeflateCompressionLevel level, bool leaveOpen = false)
    : this(stream, mode, level, ZlibConstants.DefaultWindowBits, leaveOpen) {
  }

  private ZlibStream(Stream stream, CompressionStreamMode mode, DeflateCompressionLevel level, int windowBits, bool leaveOpen)
    : base(stream, mode, leaveOpen) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, 8);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, 15);
    this._level = level;
    this._windowBits = windowBits;
  }

  /// <summary>
  /// Compresses data to zlib format.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to write zlib-compressed data to.</param>
  /// <param name="level">The Deflate compression level.</param>
  /// <param name="windowBits">
  /// The window size exponent (8-15) recorded in the header. Defaults to 15 (32 KB window).
  /// </param>
  public static void Compress(Stream input, Stream output,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default,
      int windowBits = ZlibConstants.DefaultWindowBits) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var zlib = new ZlibStream(output, CompressionStreamMode.Compress, level, windowBits, leaveOpen: true);
    input.CopyTo(zlib);
  }

  /// <summary>
  /// Compresses a byte span to zlib format.
  /// </summary>
  /// <param name="data">The uncompressed data.</param>
  /// <param name="level">The Deflate compression level.</param>
  /// <returns>The zlib-compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var output = new MemoryStream();
    using (var zlib = new ZlibStream(output, CompressionStreamMode.Compress, level, leaveOpen: true))
      zlib.Write(data);

    return output.ToArray();
  }

  /// <summary>
  /// Decompresses zlib-formatted data.
  /// </summary>
  /// <param name="input">The stream containing zlib-compressed data.</param>
  /// <param name="output">The stream to write decompressed data to.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the input is empty, the zlib header is invalid or the Adler-32 checksum does not match.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var zlib = new ZlibStream(input, CompressionStreamMode.Decompress, leaveOpen: true);
    if (!zlib.TryStart())
      throw new InvalidDataException("Zlib data is too short.");

    zlib.CopyTo(output);
  }

  /// <summary>
  /// Decompresses zlib-formatted data from a byte span.
  /// </summary>
  /// <param name="data">The zlib-compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < ZlibConstants.HeaderSize + ZlibConstants.TrailerSize)
      throw new InvalidDataException("Zlib data is too short.");

    ParseHeader(data[..2]);

    var deflateData = data[ZlibConstants.HeaderSize..^ZlibConstants.TrailerSize];
    var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(data[^ZlibConstants.TrailerSize..]);

    var decompressed = DeflateDecompressor.Decompress(deflateData);

    var actualAdler = Adler32.Compute(decompressed);
    if (actualAdler != expectedAdler)
      throw new InvalidDataException(
        $"Zlib Adler-32 mismatch: expected 0x{expectedAdler:X8}, got 0x{actualAdler:X8}.");

    return decompressed;
  }

  /// <inheritdoc />
  protected override int DecompressBlock(byte[] buffer, int offset, int count)
    => this.DecompressBlock(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  protected override int DecompressBlock(Span<byte> buffer) {
    if (this._ended || buffer.IsEmpty || !this.TryStart())
      return 0;

    var n = this._decompressor!.Decompress(buffer);
    if (n > 0) {
      this._adler.Update(buffer[..n]);
      return n;
    }

    Span<byte> trailer = stackalloc byte[ZlibConstants.TrailerSize];
    if (this._decompressor.ReadRemainder(trailer) != trailer.Length)
      throw new InvalidDataException("Zlib stream is too short for Adler-32 trailer.");

    var expected = BinaryPrimitives.ReadUInt32BigEndian(trailer);
    if (expected != this._adler.Value)
      throw new InvalidDataException(
        $"Zlib Adler-32 mismatch: expected 0x{expected:X8}, got 0x{this._adler.Value:X8}.");

    this._ended = true;
    return 0;
  }

  /// <inheritdoc />
  protected override void CompressBlock(byte[] buffer, int offset, int count)
    => this.CompressBlock(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  protected override void CompressBlock(ReadOnlySpan<byte> buffer) {
    this.EnsureCompressor();
    this._adler.Update(buffer);
    this._compressor!.Write(buffer);
  }

  /// <inheritdoc />
  protected override void FinishCompression() {
    this.EnsureCompressor();
    this._compressor!.Finish();

    Span<byte> trailer = stackalloc byte[ZlibConstants.TrailerSize];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, this._adler.Value);
    this.InnerStream.Write(trailer);
    this.InnerStream.Flush();
  }

  private void EnsureCompressor() {
    if (this._compressor is not null)
      return;

    WriteHeader(this.InnerStream, this._windowBits, this._level);
    this._compressor = new(this.InnerStream, this._level);
  }

  // Reads and checks the header on first use. An input that holds nothing at all is an empty
  // stream rather than an error, as with the platform's zlib stream; a lone byte is truncation.
  private bool TryStart() {
    if (this._decompressor is not null)
      return true;
    if (this._ended)
      return false;

    Span<byte> header = stackalloc byte[ZlibConstants.HeaderSize];
    var read = this.InnerStream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
    if (read == 0) {
      this._ended = true;
      return false;
    }

    if (read < header.Length)
      throw new InvalidDataException("Zlib header is truncated.");

    ParseHeader(header);
    this._decompressor = new(this.InnerStream);
    return true;
  }

  private static void WriteHeader(Stream output, int windowBits, DeflateCompressionLevel level) {
    // CMF: method=8 (Deflate), info = windowBits - 8
    var cmf = ZlibConstants.CompressionMethodDeflate | ((windowBits - 8) << 4);

    // FLG: FLEVEL (bits 6-7), FDICT=0 (bit 5), FCHECK (bits 0-4)
    var flevel = level switch {
      DeflateCompressionLevel.None or DeflateCompressionLevel.Fast
        => ZlibConstants.LevelFastest,
      DeflateCompressionLevel.Default => ZlibConstants.LevelDefault,
      _ => ZlibConstants.LevelMaximum,
    };

    var flg = flevel << 6;

    // Adjust FCHECK so (CMF*256 + FLG) is a multiple of 31
    var check = (31 - ((cmf * 256 + flg) % 31)) % 31;
    flg |= check;

    output.WriteByte((byte)cmf);
    output.WriteByte((byte)flg);
  }

  private static void ParseHeader(ReadOnlySpan<byte> header) {
    int cmf = header[0];
    int flg = header[1];

    // Verify checksum
    if ((cmf * 256 + flg) % 31 != 0)
      throw new InvalidDataException("Invalid zlib header checksum.");

    // Verify compression method
    var method = cmf & 0x0F;
    if (method != ZlibConstants.CompressionMethodDeflate)
      throw new InvalidDataException($"Unsupported zlib compression method: {method}.");

    // FDICT not supported
    if ((flg & 0x20) != 0)
      throw new InvalidDataException("Zlib preset dictionary is not supported.");
  }
}
