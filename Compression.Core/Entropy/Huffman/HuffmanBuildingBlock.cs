using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Entropy.Huffman;

/// <summary>
/// Exposes canonical Huffman coding as a benchmarkable building block.
/// Builds a frequency table from input, encodes with canonical codes, and stores the code lengths for decoding.
/// </summary>
public sealed class HuffmanBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Huffman";
  /// <inheritdoc/>
  public string DisplayName => "Huffman";
  /// <inheritdoc/>
  public string Description => "Optimal prefix-free entropy coding using symbol frequencies";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    // Build frequency table
    var freqs = new long[256];
    foreach (var b in data)
      freqs[b]++;

    // Ensure at least 2 symbols for the tree
    var nonZero = 0;
    foreach (var f in freqs) if (f > 0) nonZero++;
    if (nonZero < 2) {
      for (var i = 0; i < 256; i++) {
        if (freqs[i] == 0) { freqs[i] = 1; break; }
      }
    }

    var root = HuffmanTree.BuildFromFrequencies(freqs);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 256);
    HuffmanTree.LimitCodeLengths(codeLengths, 15);
    var table = new CanonicalHuffman(codeLengths);

    using var ms = new MemoryStream();

    // Header: 4-byte LE original size, then 256 bytes of code lengths
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);
    for (var i = 0; i < 256; i++)
      ms.WriteByte((byte)codeLengths[i]);

    // Encode symbols
    var bitWriter = new BitWriter<MsbBitOrder>(ms);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);
    foreach (var b in data)
      encoder.EncodeSymbol(b);
    bitWriter.FlushBits();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var codeLengths = new int[256];
    for (var i = 0; i < 256; i++)
      codeLengths[i] = data[4 + i];

    var table = new CanonicalHuffman(codeLengths);

    using var ms = new MemoryStream(data[260..].ToArray());
    var bitBuffer = new BitBuffer<MsbBitOrder>(ms);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);

    var result = new byte[originalSize];
    for (var i = 0; i < originalSize; i++)
      result[i] = (byte)decoder.DecodeSymbol();
    return result;
  }
}
