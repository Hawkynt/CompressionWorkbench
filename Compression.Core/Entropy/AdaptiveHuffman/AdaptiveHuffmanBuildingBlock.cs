using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Entropy.AdaptiveHuffman;

/// <summary>
/// Exposes FGK adaptive (dynamic) Huffman coding as a benchmarkable building block.
/// Unlike the static <c>BB_Huffman</c> block, no code-length table is transmitted:
/// the encoder and decoder both start from an empty tree and rebuild identical codes
/// symbol-by-symbol as data flows past, per <see cref="AdaptiveHuffmanTree"/>.
/// Header: 4-byte LE original size, then the bit-packed adaptive Huffman stream.
/// </summary>
public sealed class AdaptiveHuffmanBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_AdaptiveHuffman";
  /// <inheritdoc/>
  public string DisplayName => "Adaptive Huffman (FGK)";
  /// <inheritdoc/>
  public string Description => "Faller-Gallager-Knuth dynamic Huffman coding — the code tree adapts per symbol, no table is transmitted";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var tree = new AdaptiveHuffmanTree();
    var writer = new BitWriter<MsbBitOrder>(ms);
    foreach (var b in data)
      tree.EncodeSymbol(writer, b);
    writer.FlushBits();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(data[4..].ToArray());
    var reader = new BitBuffer<MsbBitOrder>(ms);
    var tree = new AdaptiveHuffmanTree();

    var result = new byte[originalSize];
    for (var i = 0; i < originalSize; ++i)
      result[i] = tree.DecodeSymbol(reader);

    return result;
  }
}
