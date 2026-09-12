using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Swf;

/// <summary>
/// Provides static methods for reading and writing SWF (Adobe Flash) files.
/// </summary>
/// <remarks>
/// SWF files begin with an 8-byte header:
/// <list type="bullet">
///   <item><description>Bytes 0-2: Signature — "FWS" (uncompressed), "CWS" (zlib), or "ZWS" (LZMA).</description></item>
///   <item><description>Byte 3: SWF version number.</description></item>
///   <item><description>Bytes 4-7: FileLength — total uncompressed size including the 8-byte header (little-endian uint32).</description></item>
/// </list>
/// For "CWS": bytes 8+ are zlib-compressed (deflate with zlib wrapper).
/// For "ZWS": bytes 8-11 are the compressed payload size (little-endian uint32), bytes 12-16 are
/// 5-byte LZMA properties, then raw LZMA-compressed data follows.
/// For "FWS": no compression, SWF body follows directly.
/// </remarks>
public static class SwfStream {
  private const int HeaderSize = 8;
  private const int ZwsCompressedSizeFieldSize = 4;
  private const int LzmaPropertiesSize = 5;
  private const int MinimumZlibVersion = 6;
  private const int MinimumLzmaVersion = 13;
  private const int OptimizedLzmaDictionarySize = 1 << 23;

  /// <summary>
  /// Decompresses an SWF file from <paramref name="input"/> and writes the uncompressed result
  /// to <paramref name="output"/>.
  /// </summary>
  /// <remarks>
  /// <list type="bullet">
  ///   <item><description>"FWS" files are passed through unchanged.</description></item>
  ///   <item><description>"CWS" files are decompressed with zlib (deflate).</description></item>
  ///   <item><description>"ZWS" files are decompressed with LZMA.</description></item>
  /// </list>
  /// The result always starts with the "FWS" signature so the output is a valid uncompressed SWF.
  /// </remarks>
  /// <param name="input">A readable stream positioned at the start of an SWF file.</param>
  /// <param name="output">The stream that receives the uncompressed SWF.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the header is too short or the signature is not recognised.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);
    ValidateSignature(header);

    var signature = (char)header[0];
    if (signature == 'F') {
      output.Write(header);
      input.CopyTo(output);
      return;
    }

    Span<byte> outHeader = stackalloc byte[HeaderSize];
    header.CopyTo(outHeader);
    outHeader[0] = (byte)'F';
    output.Write(outHeader);

    if (signature == 'C') {
      using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
      zlib.CopyTo(output);
      return;
    }

    Span<byte> compressedSize = stackalloc byte[ZwsCompressedSizeFieldSize];
    input.ReadExactly(compressedSize);

    var properties = new byte[LzmaPropertiesSize];
    input.ReadExactly(properties);

    var fileLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
    var uncompressedBodySize = fileLength - HeaderSize;
    if (uncompressedBodySize < 0)
      throw new InvalidDataException($"Invalid SWF FileLength {fileLength}: it is smaller than the {HeaderSize}-byte header.");

    var decoder = new LzmaDecoder(input, properties, uncompressedBodySize);
    decoder.Decode(output);
  }

  /// <summary>
  /// Compresses an uncompressed SWF from <paramref name="input"/> to a CWS (zlib-compressed) SWF
  /// written to <paramref name="output"/>.
  /// </summary>
  /// <remarks>
  /// The input must be a valid uncompressed SWF starting with the "FWS" signature.
  /// The output uses the "CWS" signature with the body bytes (after the 8-byte header)
  /// compressed using zlib. The FileLength field in the output header retains the
  /// original uncompressed size as required by the SWF specification.
  /// </remarks>
  /// <param name="input">A readable stream positioned at the start of an uncompressed ("FWS") SWF.</param>
  /// <param name="output">The stream that receives the CWS-compressed SWF.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the input does not begin with the "FWS" signature or the header is too short.
  /// </exception>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);
    ValidateUncompressedSignature(header);

    Span<byte> outHeader = stackalloc byte[HeaderSize];
    header.CopyTo(outHeader);
    outHeader[0] = (byte)'C';
    output.Write(outHeader);

    using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
    input.CopyTo(zlib);
  }

  /// <summary>
  /// Re-encodes an uncompressed SWF into the smallest legal envelope this implementation can
  /// produce: FWS, maximum-effort CWS/Deflate, or ZWS/LZMA.
  /// </summary>
  /// <remarks>
  /// CWS is considered only for SWF 6 or later and ZWS only for SWF 13 or later. The original
  /// uncompressed FWS representation is always a candidate, so optimization never makes the file
  /// larger merely to add a compression envelope. The SWF body itself is not parsed or modified.
  /// </remarks>
  /// <param name="input">A readable stream positioned at an uncompressed FWS file.</param>
  /// <param name="output">The stream that receives the smallest representation found.</param>
  public static void CompressOptimal(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);
    ValidateUncompressedSignature(header);

    using var bodyBuffer = new MemoryStream();
    input.CopyTo(bodyBuffer);
    var body = bodyBuffer.ToArray();

    var bestSize = HeaderSize + (long)body.Length;
    byte[]? best = null;
    var version = header[3];

    if (version >= MinimumZlibVersion) {
      var cws = CreateOptimizedCws(header, body);
      if (cws.LongLength < bestSize) {
        best = cws;
        bestSize = cws.LongLength;
      }
    }

    if (version >= MinimumLzmaVersion) {
      var zws = CreateOptimizedZws(header, body);
      if (zws.LongLength < bestSize)
        best = zws;
    }

    if (best is null) {
      output.Write(header);
      output.Write(body);
      return;
    }

    output.Write(best);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static byte[] CreateOptimizedCws(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body) {
    using var output = new MemoryStream();

    Span<byte> cwsHeader = stackalloc byte[HeaderSize];
    header.CopyTo(cwsHeader);
    cwsHeader[0] = (byte)'C';
    output.Write(cwsHeader);

    // RFC 1950: Deflate, 32 KiB window, no preset dictionary, maximum compression hint.
    // 0x78DA satisfies FCHECK and is the conventional zlib header for this combination.
    ReadOnlySpan<byte> zlibHeader = [0x78, 0xDA];
    output.Write(zlibHeader);
    output.Write(DeflateCompressor.Compress(body, DeflateCompressionLevel.Maximum));

    Span<byte> trailer = stackalloc byte[sizeof(uint)];
    BinaryPrimitives.WriteUInt32BigEndian(trailer, Adler32.Compute(body));
    output.Write(trailer);
    return output.ToArray();
  }

  private static byte[] CreateOptimizedZws(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body) {
    var encoder = new LzmaEncoder(
      dictionarySize: OptimizedLzmaDictionarySize,
      level: LzmaCompressionLevel.Best);

    using var compressed = new MemoryStream();
    encoder.Encode(compressed, body, writeEndMarker: true);
    if (compressed.Length > uint.MaxValue)
      throw new InvalidDataException("The LZMA payload is too large for the SWF ZWS compressed-length field.");

    using var output = new MemoryStream();
    Span<byte> zwsHeader = stackalloc byte[HeaderSize];
    header.CopyTo(zwsHeader);
    zwsHeader[0] = (byte)'Z';
    output.Write(zwsHeader);

    Span<byte> compressedSize = stackalloc byte[ZwsCompressedSizeFieldSize];
    BinaryPrimitives.WriteUInt32LittleEndian(compressedSize, (uint)compressed.Length);
    output.Write(compressedSize);
    output.Write(encoder.Properties);

    compressed.Position = 0;
    compressed.CopyTo(output);
    return output.ToArray();
  }

  private static void ValidateUncompressedSignature(ReadOnlySpan<byte> header) {
    ValidateSignature(header);
    if (header[0] != (byte)'F')
      throw new InvalidDataException(
        $"Input must be an uncompressed SWF (\"FWS\" signature); got \"{(char)header[0]}{(char)header[1]}{(char)header[2]}\".");
  }

  private static void ValidateSignature(ReadOnlySpan<byte> header) {
    if (header.Length < HeaderSize)
      throw new InvalidDataException(
        $"SWF header is too short: expected {HeaderSize} bytes, got {header.Length}.");

    var s0 = (char)header[0];
    var s1 = (char)header[1];
    var s2 = (char)header[2];

    if (s1 != 'W' || s2 != 'S' || s0 is not ('F' or 'C' or 'Z'))
      throw new InvalidDataException(
        $"Unrecognised SWF signature \"{s0}{s1}{s2}\". Expected \"FWS\", \"CWS\", or \"ZWS\".");
  }
}
