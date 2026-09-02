using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;
using Compression.Registry;

namespace Compression.Core.Dictionary.Zling;

/// <summary>
/// Exposes a Zling-style LZ77 + Huffman hybrid as a benchmarkable building block.
/// Zling (libzling, by Zhang Li / "richox") pairs an order-1 ROLZ dictionary stage
/// with Huffman entropy coding to get most of LZMA's ratio at a fraction of its cost.
/// This building block follows the same two-stage shape — a windowed LZ77 dictionary
/// pass (see <see cref="ZlingLz"/>) followed by canonical Huffman coding of the
/// resulting token stream — using plain LZ77 in place of ROLZ as a clean-room
/// simplification of the offset-reduction scheme.
/// Reference: https://github.com/richox/libzling (algorithm description); D. A.
/// Huffman, "A Method for the Construction of Minimum-Redundancy Codes", 1952.
/// </summary>
public sealed class ZlingBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Zling";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Zling";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "LZ77 dictionary matching followed by canonical Huffman entropy coding";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var intermediate = ZlingLz.Encode(data);

    BinaryPrimitives.WriteInt32LittleEndian(header, intermediate.Length);
    ms.Write(header);

    var freqs = new long[256];
    foreach (var b in intermediate)
      freqs[b]++;

    var nonZero = freqs.Count(f => f > 0);
    if (nonZero < 2)
      for (var i = 0; i < 256; i++)
        if (freqs[i] == 0) {
          freqs[i] = 1;
          break;
        }

    var root = HuffmanTree.BuildFromFrequencies(freqs);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 256);
    HuffmanTree.LimitCodeLengths(codeLengths, 15);
    var table = new CanonicalHuffman(codeLengths);

    for (var i = 0; i < 256; i++)
      ms.WriteByte((byte)codeLengths[i]);

    var bitWriter = new BitWriter<MsbBitOrder>(ms);
    var encoder = new HuffmanEncoder<MsbBitOrder>(table, bitWriter);
    foreach (var b in intermediate)
      encoder.EncodeSymbol(b);
    bitWriter.FlushBits();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength == 0)
      return [];

    var intermediateLength = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);

    var codeLengths = new int[256];
    for (var i = 0; i < 256; i++)
      codeLengths[i] = data[8 + i];

    var table = new CanonicalHuffman(codeLengths);

    using var ms = new MemoryStream(data[264..].ToArray());
    var bitBuffer = new BitBuffer<MsbBitOrder>(ms);
    var decoder = new HuffmanDecoder<MsbBitOrder>(table, bitBuffer);

    var intermediate = new byte[intermediateLength];
    for (var i = 0; i < intermediateLength; i++)
      intermediate[i] = (byte)decoder.DecodeSymbol();

    return ZlingLz.Decode(intermediate, originalLength);
  }
}
