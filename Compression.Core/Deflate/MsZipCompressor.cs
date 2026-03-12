namespace Compression.Core.Deflate;

/// <summary>
/// Compresses data using the MSZIP format: 32 KB Deflate blocks each
/// preceded by a two-byte "CK" signature (0x43, 0x4B).
/// </summary>
/// <remarks>
/// MSZIP is used by the Microsoft Cabinet (CAB) file format. Each block
/// is an independent DEFLATE stream limited to 32,768 uncompressed bytes.
/// The decompressor maintains a 32 KB sliding-window history across blocks
/// so that back-references can span block boundaries on decompression.
/// On the compression side each block is compressed independently; the
/// history benefit comes from the decompressor sharing the window.
/// </remarks>
public static class MsZipCompressor {
  /// <summary>The uncompressed block size limit: 32 768 bytes.</summary>
  public const int BlockSize = 32768;

  /// <summary>MSZIP block signature byte 0 ('C').</summary>
  public const byte SignatureByte0 = 0x43;

  /// <summary>MSZIP block signature byte 1 ('K').</summary>
  public const byte SignatureByte1 = 0x4B;

  /// <summary>
  /// Compresses <paramref name="data"/> into MSZIP format.
  /// </summary>
  /// <param name="data">The uncompressed input data.</param>
  /// <param name="level">The Deflate compression level to use.</param>
  /// <returns>
  /// A byte array containing one or more "CK"-prefixed Deflate blocks.
  /// </returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var output = new MemoryStream();
    var offset = 0;

    // Always emit at least one block, even for empty input.
    do {
      var chunkLen = Math.Min(BlockSize, data.Length - offset);
      var chunk = data.Slice(offset, chunkLen);

      // Deflate-compress the chunk.
      var compressed = DeflateCompressor.Compress(chunk, level);

      // Write "CK" signature.
      output.WriteByte(SignatureByte0);
      output.WriteByte(SignatureByte1);

      // Write compressed Deflate data.
      output.Write(compressed);

      offset += chunkLen;
    } while (offset < data.Length);

    return output.ToArray();
  }

  /// <summary>
  /// Splits <paramref name="data"/> into raw (uncompressed) MSZIP blocks
  /// and returns the compressed byte counts for each block together with
  /// the full compressed output. Useful for CAB CFDATA record construction.
  /// </summary>
  /// <param name="data">The uncompressed input data.</param>
  /// <param name="level">The Deflate compression level to use.</param>
  /// <returns>
  /// A list of <c>(compressedData, uncompressedSize)</c> tuples, one per
  /// 32 KB block.
  /// </returns>
  public static List<(byte[] CompressedData, int UncompressedSize)> CompressBlocks(
    ReadOnlySpan<byte> data,
    DeflateCompressionLevel level = DeflateCompressionLevel.Default) {

    var blocks = new List<(byte[], int)>();
    var offset = 0;

    do {
      var chunkLen = Math.Min(BlockSize, data.Length - offset);
      var chunk = data.Slice(offset, chunkLen);

      var deflated = DeflateCompressor.Compress(chunk, level);

      // Prepend "CK" signature.
      var block = new byte[2 + deflated.Length];
      block[0] = SignatureByte0;
      block[1] = SignatureByte1;
      deflated.CopyTo(block, 2);

      blocks.Add((block, chunkLen));
      offset += chunkLen;
    } while (offset < data.Length);

    return blocks;
  }
}
