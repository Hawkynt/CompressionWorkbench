using Compression.Core.Entropy.Fse;

namespace FileFormat.Zstd;

/// <summary>
/// Handles encoding and decoding of the literals section in Zstandard compressed blocks.
/// Supports Raw, RLE, Huffman, and Treeless literal block types.
/// </summary>
internal static class ZstdLiterals {
  /// <summary>Literal block type: raw (uncompressed).</summary>
  private const int LitTypeRaw = 0;

  /// <summary>Literal block type: RLE (single repeated byte).</summary>
  private const int LitTypeRle = 1;

  /// <summary>Literal block type: Huffman compressed.</summary>
  private const int LitTypeHuffman = 2;

  /// <summary>Literal block type: Treeless (reuse previous Huffman table).</summary>
  private const int LitTypeTreeless = 3;

  /// <summary>
  /// Decompresses the literals section of a compressed block.
  /// </summary>
  /// <param name="blockData">The compressed block data.</param>
  /// <param name="pos">The current position in the block data; updated on return.</param>
  /// <param name="huffmanWeights">
  /// The Huffman weight table from the previous block, updated if a new table is present.
  /// </param>
  /// <returns>The decompressed literal bytes.</returns>
  /// <exception cref="InvalidDataException">The literal section is malformed.</exception>
  public static byte[] DecompressLiterals(ReadOnlySpan<byte> blockData, ref int pos, ref int[]? huffmanWeights) {
    if (pos >= blockData.Length)
      throw new InvalidDataException("Truncated Zstandard literals header.");

    int headerByte = blockData[pos];
    var litType = headerByte & 3;
    var sizeFormat = (headerByte >> 2) & 3;

    switch (litType) {
      case LitTypeRaw:
        return DecompressRawLiterals(blockData, ref pos, sizeFormat);
      case LitTypeRle:
        return DecompressRleLiterals(blockData, ref pos, sizeFormat);
      case LitTypeHuffman:
        return DecompressHuffmanLiterals(blockData, ref pos, sizeFormat, ref huffmanWeights);
      case LitTypeTreeless:
        return DecompressTreelessLiterals(blockData, ref pos, sizeFormat, huffmanWeights);
      default:
        throw new InvalidDataException($"Unknown Zstandard literal type: {litType}");
    }
  }

  /// <summary>
  /// Compresses literals using Raw encoding (uncompressed passthrough).
  /// </summary>
  /// <param name="literals">The literal bytes to encode.</param>
  /// <param name="output">The output buffer.</param>
  /// <param name="outputPos">The current position in the output buffer.</param>
  /// <returns>The number of bytes written.</returns>
  public static int CompressLiterals(ReadOnlySpan<byte> literals, byte[] output, int outputPos) {
    var startPos = outputPos;

    // Use Raw literal type
    var regenSize = literals.Length;

    if (regenSize < 32) {
      // 1-byte header: sizeFormat=0 or 1, size in bits [7:3]
      output[outputPos++] = (byte)((regenSize << 3) | (0 << 2) | LitTypeRaw);
    }
    else if (regenSize < 4096) {
      // 2-byte header: sizeFormat=2 (bits [3:2]=10)
      // Byte0: type(2) | sizeFormat(2) | size[3:0](4)
      // Byte1: size[11:4](8)
      var header = LitTypeRaw | (2 << 2) | ((regenSize & 0x0F) << 4);
      output[outputPos++] = (byte)(header & 0xFF);
      output[outputPos++] = (byte)(regenSize >> 4);
    }
    else {
      // 3-byte header: sizeFormat=3 (bits [3:2]=11)
      // Byte0: type(2) | sizeFormat(2) | size[3:0](4)
      // Byte1: size[11:4](8)
      // Byte2: size[19:12](8)
      var header = LitTypeRaw | (3 << 2) | ((regenSize & 0x0F) << 4);
      output[outputPos++] = (byte)(header & 0xFF);
      output[outputPos++] = (byte)((regenSize >> 4) & 0xFF);
      output[outputPos++] = (byte)((regenSize >> 12) & 0xFF);
    }

    // Copy literal data
    literals.CopyTo(output.AsSpan(outputPos));
    outputPos += regenSize;

    return outputPos - startPos;
  }

  /// <summary>
  /// Decompresses raw (uncompressed) literals.
  /// </summary>
  private static byte[] DecompressRawLiterals(ReadOnlySpan<byte> blockData, ref int pos, int sizeFormat) {
    var regenSize = ReadLiteralSize(blockData, ref pos, sizeFormat);

    if (pos + regenSize > blockData.Length)
      throw new InvalidDataException("Truncated raw literal data.");

    var result = blockData.Slice(pos, regenSize).ToArray();
    pos += regenSize;
    return result;
  }

  /// <summary>
  /// Decompresses RLE literals (single byte repeated).
  /// </summary>
  private static byte[] DecompressRleLiterals(ReadOnlySpan<byte> blockData, ref int pos, int sizeFormat) {
    var regenSize = ReadLiteralSize(blockData, ref pos, sizeFormat);

    if (pos >= blockData.Length)
      throw new InvalidDataException("Truncated RLE literal data.");

    var value = blockData[pos++];
    var result = new byte[regenSize];
    result.AsSpan().Fill(value);
    return result;
  }

  /// <summary>
  /// Decompresses Huffman-encoded literals (includes a Huffman weight table).
  /// </summary>
  private static byte[] DecompressHuffmanLiterals(ReadOnlySpan<byte> blockData, ref int pos,
    int sizeFormat, ref int[]? huffmanWeights) {
    var (regenSize, compressedSize) = ReadCompressedLiteralSizes(blockData, ref pos, sizeFormat);

    if (pos + compressedSize > blockData.Length)
      throw new InvalidDataException("Truncated Huffman literal data.");

    var compressedData = blockData.Slice(pos, compressedSize);
    pos += compressedSize;

    // Read Huffman weight table and decode
    var result = HuffmanFse.DecompressHuffman(compressedData, regenSize);
    // Save the weights for potential Treeless reuse
    huffmanWeights = HuffmanFse.ReadWeights(compressedData, out _);
    return result;
  }

  /// <summary>
  /// Decompresses Treeless Huffman literals (reuses previous Huffman table).
  /// </summary>
  private static byte[] DecompressTreelessLiterals(ReadOnlySpan<byte> blockData, ref int pos,
    int sizeFormat, int[]? savedWeights) {
    if (savedWeights == null)
      throw new InvalidDataException("Treeless literal block requires a previous Huffman table.");

    var (regenSize, compressedSize) = ReadCompressedLiteralSizes(blockData, ref pos, sizeFormat);

    if (pos + compressedSize > blockData.Length)
      throw new InvalidDataException("Truncated Treeless literal data.");

    // Build the weight header and prepend it
    var weightHeader = new byte[256];
    var weightHeaderLen = HuffmanFse.WriteWeights(weightHeader, 0, savedWeights, FindMaxWeight(savedWeights));

    var combined = new byte[weightHeaderLen + compressedSize];
    weightHeader.AsSpan(0, weightHeaderLen).CopyTo(combined);
    blockData.Slice(pos, compressedSize).CopyTo(combined.AsSpan(weightHeaderLen));
    pos += compressedSize;

    return HuffmanFse.DecompressHuffman(combined, regenSize);
  }

  /// <summary>
  /// Reads the regenerated size for Raw and RLE literal blocks.
  /// </summary>
  private static int ReadLiteralSize(ReadOnlySpan<byte> blockData, ref int pos, int sizeFormat) {
    int regenSize;
    int headerByte = blockData[pos];

    if (sizeFormat is 0 or 1) {
      // 1-byte header: size in bits [7:3]
      regenSize = headerByte >> 3;
      pos += 1;
    }
    else if (sizeFormat == 2) {
      // 2-byte header: size in bits [7:4] of byte0 + byte1
      if (pos + 1 >= blockData.Length)
        throw new InvalidDataException("Truncated literal header.");
      regenSize = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4);
      pos += 2;
    }
    else {
      // 3-byte header: size in bits [7:4] of byte0 + byte1 + byte2
      if (pos + 2 >= blockData.Length)
        throw new InvalidDataException("Truncated literal header.");
      regenSize = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4) | (blockData[pos + 2] << 12);
      pos += 3;
    }

    return regenSize;
  }

  /// <summary>
  /// Reads the regenerated size and compressed size for Huffman/Treeless literal blocks.
  /// </summary>
  private static (int RegenSize, int CompressedSize) ReadCompressedLiteralSizes(
    ReadOnlySpan<byte> blockData, ref int pos, int sizeFormat) {
    int regenSize;
    int compressedSize;
    int headerByte = blockData[pos];

    if (sizeFormat == 0) {
      // Both sizes use 10 bits, 3-byte header total
      if (pos + 2 >= blockData.Length)
        throw new InvalidDataException("Truncated compressed literal header.");
      var combined = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4) | (blockData[pos + 2] << 12);
      regenSize = combined & 0x3FF;
      compressedSize = combined >> 10;
      pos += 3;
    }
    else if (sizeFormat == 1) {
      // Both sizes use 10 bits, 3-byte header total
      if (pos + 2 >= blockData.Length)
        throw new InvalidDataException("Truncated compressed literal header.");
      var combined = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4) | (blockData[pos + 2] << 12);
      regenSize = combined & 0x3FF;
      compressedSize = combined >> 10;
      pos += 3;
    }
    else if (sizeFormat == 2) {
      // Both sizes use 14 bits, 4-byte header total
      if (pos + 3 >= blockData.Length)
        throw new InvalidDataException("Truncated compressed literal header.");
      var combined = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4)
             | (blockData[pos + 2] << 12) | (blockData[pos + 3] << 20);
      regenSize = combined & 0x3FFF;
      compressedSize = combined >> 14;
      pos += 4;
    }
    else {
      // Both sizes use 18 bits, 5-byte header total
      if (pos + 4 >= blockData.Length)
        throw new InvalidDataException("Truncated compressed literal header.");
      var combined = ((headerByte >> 4) & 0x0F) | (blockData[pos + 1] << 4)
             | (blockData[pos + 2] << 12) | (blockData[pos + 3] << 20)
             | (blockData[pos + 4] << 28);
      regenSize = combined & 0x3FFFF;
      compressedSize = (int)((uint)combined >> 18);
      pos += 5;
    }

    return (regenSize, compressedSize);
  }

  /// <summary>
  /// Finds the maximum non-zero weight index.
  /// </summary>
  private static int FindMaxWeight(int[] weights) {
    for (var i = weights.Length - 1; i >= 0; --i) {
      if (weights[i] > 0)
        return i;
    }

    return 0;
  }
}
