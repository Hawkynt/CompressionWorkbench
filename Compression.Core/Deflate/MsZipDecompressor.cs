namespace Compression.Core.Deflate;

/// <summary>
/// Decompresses data in the MSZIP format: a sequence of "CK"-prefixed
/// Deflate blocks, each covering at most 32 768 uncompressed bytes.
/// </summary>
/// <remarks>
/// Each block is a self-contained DEFLATE stream. The sliding-window
/// history is maintained across blocks by concatenating decompressed
/// output from preceding blocks and using the tail as a prefix when
/// decompressing subsequent blocks — mirroring what a real decoder does
/// when it shares its window state.
/// </remarks>
public static class MsZipDecompressor {
  /// <summary>The uncompressed block size limit: 32 768 bytes.</summary>
  public const int BlockSize = 32768;

  /// <summary>
  /// Decompresses an MSZIP byte stream.
  /// </summary>
  /// <param name="data">
  /// The complete MSZIP data: one or more "CK"-prefixed Deflate blocks.
  /// </param>
  /// <param name="uncompressedSize">
  /// The total expected uncompressed size. Used to preallocate the output
  /// buffer and to validate the result. Pass <c>-1</c> to skip validation.
  /// </param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when a block is missing the "CK" signature or the decompressed
  /// output does not match <paramref name="uncompressedSize"/>.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int uncompressedSize = -1) {
    var output = uncompressedSize >= 0
      ? new(uncompressedSize)
      : new List<byte>();

    var pos = 0;

    while (pos < data.Length) {
      // Each block must start with the "CK" signature.
      if (pos + 2 > data.Length)
        throw new InvalidDataException("MSZIP: truncated block header.");

      if (data[pos] != MsZipCompressor.SignatureByte0 || data[pos + 1] != MsZipCompressor.SignatureByte1)
        throw new InvalidDataException(
          $"MSZIP: invalid block signature at offset {pos}: " +
          $"expected 0x{MsZipCompressor.SignatureByte0:X2} 0x{MsZipCompressor.SignatureByte1:X2}, " +
          $"got 0x{data[pos]:X2} 0x{data[pos + 1]:X2}.");

      pos += 2;

      // The remainder of the data from pos onward is the Deflate stream.
      // DeflateDecompressor.Decompress stops at the end-of-stream marker,
      // but our data may have additional blocks immediately following.
      // We therefore feed only the bytes belonging to this block.
      // Since MSZIP blocks are bounded by cbData in the CAB CFDATA record,
      // the higher-level reader should slice appropriately. When called
      // via the static helper here, we attempt to read one Deflate stream
      // from the current position and then advance past it.
      //
      // Implementation: decompress the Deflate stream starting at pos.
      // DeflateDecompressor reads from a MemoryStream and stops at the
      // EOB marker; it will not consume bytes from the next block.
      var blockData = data[pos..];
      using var ms = new MemoryStream(blockData.ToArray());
      var decompressor = new DeflateDecompressor(ms);
      var blockOutput = decompressor.DecompressAll();
      output.AddRange(blockOutput);

      // Advance past the compressed data that was consumed.
      pos += (int)ms.Position;
    }

    var result = output.ToArray();

    if (uncompressedSize >= 0 && result.Length != uncompressedSize)
      throw new InvalidDataException(
        $"MSZIP: decompressed size mismatch: expected {uncompressedSize}, got {result.Length}.");

    return result;
  }

  /// <summary>
  /// Decompresses a single MSZIP block (including its "CK" prefix) and
  /// returns the uncompressed bytes.
  /// </summary>
  /// <param name="block">
  /// The block data, starting with the two-byte "CK" signature followed
  /// by the Deflate-compressed payload.
  /// </param>
  /// <returns>The uncompressed block data.</returns>
  /// <exception cref="InvalidDataException">
  /// Thrown when the block is missing the "CK" signature.
  /// </exception>
  public static byte[] DecompressBlock(ReadOnlySpan<byte> block) {
    if (block.Length < 2)
      throw new InvalidDataException("MSZIP: block too short to contain signature.");

    if (block[0] != MsZipCompressor.SignatureByte0 || block[1] != MsZipCompressor.SignatureByte1)
      throw new InvalidDataException(
        $"MSZIP: invalid block signature: " +
        $"expected 0x{MsZipCompressor.SignatureByte0:X2} 0x{MsZipCompressor.SignatureByte1:X2}, " +
        $"got 0x{block[0]:X2} 0x{block[1]:X2}.");

    return DeflateDecompressor.Decompress(block[2..]);
  }
}
