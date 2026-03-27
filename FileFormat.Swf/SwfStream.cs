using System.Buffers.Binary;
using System.IO.Compression;
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

  // Compressed-size field for ZWS (4 bytes at offset 8, before LZMA properties)
  private const int ZwsCompressedSizeFieldSize = 4;

  // LZMA properties block is 5 bytes (properties byte + 4-byte dict size)
  private const int LzmaPropertiesSize = 5;

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

    // Read the 8-byte header.
    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);

    ValidateSignature(header);

    var sig0 = (char)header[0];
    var sig1 = (char)header[1];
    var sig2 = (char)header[2];

    if (sig0 == 'F') {
      // FWS — uncompressed: write header then stream remaining bytes directly.
      output.Write(header);
      input.CopyTo(output);
      return;
    }

    // Both CWS and ZWS produce an uncompressed FWS output.
    // Build the output header with the "FWS" signature; version and FileLength are preserved.
    Span<byte> outHeader = stackalloc byte[HeaderSize];
    header.CopyTo(outHeader);
    outHeader[0] = (byte)'F';
    outHeader[1] = (byte)'W';
    outHeader[2] = (byte)'S';
    output.Write(outHeader);

    if (sig0 == 'C') {
      // CWS — zlib-compressed body.
      using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
      zlib.CopyTo(output);
    } else {
      // ZWS — LZMA-compressed body.
      // Bytes 8-11: compressed payload size (uint32 LE) — not needed for decoding.
      Span<byte> compSizeBytes = stackalloc byte[ZwsCompressedSizeFieldSize];
      input.ReadExactly(compSizeBytes);

      // Bytes 12-16: 5-byte LZMA properties (properties byte + dict size uint32 LE).
      var props = new byte[LzmaPropertiesSize];
      input.ReadExactly(props);

      // The uncompressed size stored in the SWF FileLength field (bytes 4-7 of the header)
      // minus the 8-byte header gives the size of the decompressed body.
      var fileLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
      var uncompressedBodySize = fileLength - HeaderSize;

      var decoder = new LzmaDecoder(input, props, uncompressedBodySize);
      decoder.Decode(output);
    }

    _ = sig1; // suppress unused-variable warning (consumed for validation only)
    _ = sig2;
  }

  /// <summary>
  /// Compresses an uncompressed SWF from <paramref name="input"/> to a CWS (zlib-compressed) SWF
  /// written to <paramref name="output"/>.
  /// </summary>
  /// <remarks>
  /// The input must be a valid uncompressed SWF starting with the "FWS" signature.
  /// The output uses the "CWS" signature with the body bytes (after the 8-byte header)
  /// compressed using zlib.  The FileLength field in the output header retains the
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

    // Read the 8-byte header.
    Span<byte> header = stackalloc byte[HeaderSize];
    input.ReadExactly(header);

    ValidateSignature(header);

    if ((char)header[0] != 'F')
      throw new InvalidDataException(
          $"Input must be an uncompressed SWF (\"FWS\" signature); got \"{(char)header[0]}{(char)header[1]}{(char)header[2]}\".");

    // Emit the CWS header: change "FWS" → "CWS", keep version and FileLength.
    Span<byte> outHeader = stackalloc byte[HeaderSize];
    header.CopyTo(outHeader);
    outHeader[0] = (byte)'C';
    outHeader[1] = (byte)'W';
    outHeader[2] = (byte)'S';
    output.Write(outHeader);

    // Compress the body (everything after the 8-byte header) with zlib.
    using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
    input.CopyTo(zlib);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static void ValidateSignature(ReadOnlySpan<byte> header) {
    if (header.Length < HeaderSize)
      throw new InvalidDataException(
          $"SWF header is too short: expected {HeaderSize} bytes, got {header.Length}.");

    var s0 = (char)header[0];
    var s1 = (char)header[1];
    var s2 = (char)header[2];

    if (s1 != 'W' || s2 != 'S' || (s0 != 'F' && s0 != 'C' && s0 != 'Z'))
      throw new InvalidDataException(
          $"Unrecognised SWF signature \"{s0}{s1}{s2}\". Expected \"FWS\", \"CWS\", or \"ZWS\".");
  }
}
