using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Lzma;

/// <summary>
/// Provides static methods for reading and writing the LZMA alone (.lzma) file format.
/// </summary>
/// <remarks>
/// The LZMA alone format consists of a 13-byte header followed by raw LZMA-compressed data:
/// <list type="bullet">
///   <item><description>Byte 0: Properties byte encoding lc, lp, pb as <c>lc + 9*(lp + 5*pb)</c>.</description></item>
///   <item><description>Bytes 1-4: Dictionary size as a little-endian uint32.</description></item>
///   <item><description>Bytes 5-12: Uncompressed size as a little-endian int64; -1 means unknown.</description></item>
/// </list>
/// </remarks>
public static class LzmaStream {
  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes the LZMA alone stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data to read.</param>
  /// <param name="output">The stream to which the LZMA alone data is written.</param>
  /// <param name="dictionarySize">The dictionary size in bytes. Default is 8 MiB.</param>
  /// <param name="lc">Literal context bits (0-8). Default 3.</param>
  /// <param name="lp">Literal position bits (0-4). Default 0.</param>
  /// <param name="pb">Position bits (0-4). Default 2.</param>
  /// <param name="level">The compression level.</param>
  public static void Compress(
      Stream input,
      Stream output,
      int dictionarySize = LzmaConstants.DefaultDictionarySize,
      int lc = 3,
      int lp = 0,
      int pb = 2,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    byte[] data = ReadAllBytes(input);

    var encoder = new LzmaEncoder(dictionarySize, lc, lp, pb, level);

    // Write 13-byte header
    Span<byte> header = stackalloc byte[LzmaConstants.HeaderSize];

    // Byte 0: properties byte (first byte of the 5-byte properties array)
    header[0] = encoder.Properties[0];

    // Bytes 1-4: dictionary size (little-endian uint32, already encoded in Properties[1..4])
    BinaryPrimitives.WriteUInt32LittleEndian(header[1..], BinaryPrimitives.ReadUInt32LittleEndian(encoder.Properties.AsSpan(1)));

    // Bytes 5-12: uncompressed size (little-endian int64)
    BinaryPrimitives.WriteInt64LittleEndian(header[5..], data.LongLength);

    output.Write(header);

    // Write raw LZMA compressed data (end-of-stream marker included)
    encoder.Encode(output, data);
  }

  /// <summary>
  /// Decompresses an LZMA alone stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing LZMA alone data to read.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the header is truncated or the properties byte is invalid.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read 13-byte header
    Span<byte> header = stackalloc byte[LzmaConstants.HeaderSize];
    input.ReadExactly(header);

    // Validate properties byte: must be < 9*5*5 = 225
    if (header[0] >= 9 * 5 * 5)
      throw new InvalidDataException(
          $"Invalid LZMA properties byte 0x{header[0]:X2}: value must be less than 225.");

    // Extract the 5-byte properties block (properties byte + dict size)
    byte[] properties = new byte[5];
    header[..5].CopyTo(properties);

    // Read uncompressed size (-1 = unknown, use end-marker)
    long uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(header[5..]);

    var decoder = new LzmaDecoder(input, properties, uncompressedSize);
    decoder.Decode(output);
  }

  private static byte[] ReadAllBytes(Stream stream) {
    if (stream is MemoryStream ms)
      return ms.ToArray();

    using var buf = new MemoryStream();
    stream.CopyTo(buf);
    return buf.ToArray();
  }
}
