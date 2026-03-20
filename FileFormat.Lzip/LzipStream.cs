using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Lzip;

/// <summary>
/// Provides static methods for reading and writing Lzip (.lz) format members.
/// </summary>
/// <remarks>
/// The Lzip format wraps an LZMA1 compressed stream with a 6-byte header
/// and a 20-byte trailer containing CRC-32, uncompressed size, and member size.
/// LZMA parameters are fixed at lc=3, lp=0, pb=2; no properties header is
/// stored in the stream.
/// </remarks>
public static class LzipStream {
  /// <summary>
  /// Compresses all data from <paramref name="input"/> and writes a single Lzip member
  /// to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream that receives the Lzip member.</param>
  /// <param name="dictionarySize">
  /// The LZMA dictionary size. Must be a power of two between
  /// <see cref="LzipConstants.MinDictionarySize"/> and
  /// <see cref="LzipConstants.MaxDictionarySize"/>. Defaults to 8 MiB.
  /// </param>
  /// <param name="level">The LZMA compression level.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="dictionarySize"/> is out of the valid range.
  /// </exception>
  public static void Compress(
      Stream input,
      Stream output,
      int dictionarySize = 1 << 23,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    if (dictionarySize < LzipConstants.MinDictionarySize || dictionarySize > LzipConstants.MaxDictionarySize)
      throw new ArgumentOutOfRangeException(nameof(dictionarySize),
        $"Dictionary size must be between {LzipConstants.MinDictionarySize} and {LzipConstants.MaxDictionarySize}.");

    // Read all uncompressed data so we can compute the CRC-32 and size.
    byte[] uncompressed = ReadFully(input);

    // Write the 6-byte header.
    byte dictSizeByte = EncodeDictionarySize(dictionarySize);
    WriteHeader(output, dictSizeByte);

    // Remember where the member starts so we can calculate member size.
    long memberStart = output.CanSeek ? output.Position - LzipConstants.HeaderSize : -1;

    // Compress with LZMA (lc=3, lp=0, pb=2), writing an end-of-stream marker.
    var encoder = new LzmaEncoder(dictionarySize, lc: 3, lp: 0, pb: 2, level);
    using var lzmaBuffer = new MemoryStream();
    encoder.Encode(lzmaBuffer, uncompressed, writeEndMarker: true);
    byte[] lzmaBytes = lzmaBuffer.ToArray();
    output.Write(lzmaBytes);

    // Write the 20-byte trailer.
    uint crc = Crc32.Compute(uncompressed);
    ulong uncompressedSize = (ulong)uncompressed.LongLength;
    ulong memberSize = (ulong)(LzipConstants.HeaderSize + lzmaBytes.LongLength + LzipConstants.TrailerSize);

    WriteTrailer(output, crc, uncompressedSize, memberSize);
  }

  /// <summary>
  /// Decompresses a single Lzip member from <paramref name="input"/> and writes
  /// the uncompressed data to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing a Lzip member.</param>
  /// <param name="output">The stream that receives the decompressed data.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes or version are invalid, or the CRC-32 or
  /// size fields in the trailer do not match the decompressed data.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read and validate header.
    Span<byte> header = stackalloc byte[LzipConstants.HeaderSize];
    input.ReadExactly(header);

    if (header[0] != LzipConstants.Magic0 ||
        header[1] != LzipConstants.Magic1 ||
        header[2] != LzipConstants.Magic2 ||
        header[3] != LzipConstants.Magic3) {
      throw new InvalidDataException("Invalid Lzip magic bytes.");
    }

    if (header[4] != LzipConstants.Version)
      throw new InvalidDataException($"Unsupported Lzip version: {header[4]}.");

    int dictionarySize = DecodeDictionarySize(header[5]);

    // Build the 5-byte LZMA properties array (properties byte + dict size LE).
    byte[] lzmaProperties = BuildLzmaProperties(dictionarySize);

    // Decompress LZMA stream (end-of-marker termination: uncompressedSize = -1).
    var decoder = new LzmaDecoder(input, lzmaProperties, uncompressedSize: -1);
    using var decompressedBuffer = new MemoryStream();
    decoder.Decode(decompressedBuffer);
    byte[] decompressed = decompressedBuffer.ToArray();

    // Read and verify the 20-byte trailer.
    Span<byte> trailer = stackalloc byte[LzipConstants.TrailerSize];
    input.ReadExactly(trailer);

    uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer[..4]);
    ulong storedUncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(trailer[4..12]);
    // Stored member size is informational; we verify CRC and uncompressed size.

    uint computedCrc = Crc32.Compute(decompressed);
    if (storedCrc != computedCrc)
      throw new InvalidDataException(
        $"Lzip CRC-32 mismatch: expected 0x{storedCrc:X8}, computed 0x{computedCrc:X8}.");

    if (storedUncompressedSize != (ulong)decompressed.LongLength)
      throw new InvalidDataException(
        $"Lzip uncompressed size mismatch: expected {storedUncompressedSize}, " +
        $"got {(ulong)decompressed.LongLength}.");

    output.Write(decompressed);
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  private static void WriteHeader(Stream output, byte dictSizeByte) {
    output.WriteByte(LzipConstants.Magic0);
    output.WriteByte(LzipConstants.Magic1);
    output.WriteByte(LzipConstants.Magic2);
    output.WriteByte(LzipConstants.Magic3);
    output.WriteByte(LzipConstants.Version);
    output.WriteByte(dictSizeByte);
  }

  private static void WriteTrailer(Stream output, uint crc, ulong uncompressedSize, ulong memberSize) {
    Span<byte> trailer = stackalloc byte[LzipConstants.TrailerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(trailer[..4], crc);
    BinaryPrimitives.WriteUInt64LittleEndian(trailer[4..12], uncompressedSize);
    BinaryPrimitives.WriteUInt64LittleEndian(trailer[12..20], memberSize);
    output.Write(trailer);
  }

  /// <summary>
  /// Encodes a dictionary size as the Lzip dict-size byte.
  /// Uses the closest power-of-two encoding (fraction bits = 0).
  /// </summary>
  private static byte EncodeDictionarySize(int dictionarySize) {
    // Find floor log2 of dictionarySize.
    int log2 = 0;
    int v = dictionarySize;
    while (v > 1) {
      v >>= 1;
      ++log2;
    }

    // Clamp to [12, 29] as required by the spec (4 KiB to 512 MiB as powers of two).
    if (log2 < 12)
      log2 = 12;
    else if (log2 > 29)
      log2 = 29;

    // Fraction = 0: just store the exponent in bits 0-4.
    return (byte)(log2 & 0x1F);
  }

  /// <summary>
  /// Decodes the Lzip dict-size byte to a dictionary size in bytes.
  /// Formula: (1 &lt;&lt; (b &amp; 0x1F)) | ((1 &lt;&lt; (b &amp; 0x1F)) &gt;&gt; 2) * ((b &gt;&gt; 5) &amp; 0x07).
  /// </summary>
  private static int DecodeDictionarySize(byte b) {
    int exponent = b & 0x1F;
    int fraction = (b >> 5) & 0x07;
    int baseSize = 1 << exponent;
    int dictSize = baseSize | ((baseSize >> 2) * fraction);

    if (dictSize < LzipConstants.MinDictionarySize)
      dictSize = LzipConstants.MinDictionarySize;

    return dictSize;
  }

  /// <summary>
  /// Builds the 5-byte LZMA properties array for lc=3, lp=0, pb=2 with the given dictionary size.
  /// </summary>
  private static byte[] BuildLzmaProperties(int dictionarySize) {
    byte[] props = new byte[5];
    props[0] = LzipConstants.LzmaPropertiesByte;
    BinaryPrimitives.WriteInt32LittleEndian(props.AsSpan(1), dictionarySize);
    return props;
  }

  private static byte[] ReadFully(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
