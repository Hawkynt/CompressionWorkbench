using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// The ACE 1.0 LZ77 codec (the bit stream of an ACE compression-type-1 member) as a building block.
/// A 4-byte little-endian length and the dictionary-bits byte precede the stream so it round-trips.
/// </summary>
public sealed class AceBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Ace";
  /// <inheritdoc/>
  public string DisplayName => "ACE";
  /// <inheritdoc/>
  public string Description => "ACE 1.0 LZ77 + Huffman compression, as stored in ACE archive members";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressor = new AceCompressor();
    var packed = compressor.Compress(data);
    var result = new byte[5 + packed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    result[4] = (byte)compressor.DictionaryBits;
    packed.CopyTo(result.AsSpan(5));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 5)
      throw new InvalidDataException("ACE building-block stream too short.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0)
      throw new InvalidDataException("Negative ACE building-block length.");
    return size == 0 ? [] : new AceDecompressor().Decompress(data[5..].ToArray(), size, AceConstants.CompAce10, data[4]);
  }
}
