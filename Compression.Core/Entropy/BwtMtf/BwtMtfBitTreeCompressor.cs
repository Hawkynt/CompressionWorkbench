using System.Buffers.Binary;
using Compression.Core.Entropy.RangeCoding;
using Compression.Core.Transforms;

namespace Compression.Core.Entropy.BwtMtf;

/// <summary>
/// A block-sorting coder of this repository's own design: the cyclic Burrows-Wheeler
/// Transform, Move-to-Front recoding, and an adaptive binary range coder over the MTF ranks.
/// </summary>
/// <remarks>
/// <para>
/// The input is transformed with <see cref="BurrowsWheelerTransform"/> (sorted rotations, as
/// bzip2 does), recoded with <see cref="MoveToFrontTransform"/>, and every rank byte is coded
/// MSB-first through an LZMA-style adaptive bit tree (<see cref="BitTreeEncoder"/> /
/// <see cref="BitTreeDecoder"/> over <see cref="RangeEncoder"/> / <see cref="RangeDecoder"/>).
/// Two bit trees are kept, one for the rank after a zero rank and one for the rest, because MTF
/// output alternates between zero runs and scattered non-zero ranks; that split is the whole
/// context model.
/// </para>
/// <para>
/// Layout: 4-byte little-endian length, 4-byte little-endian zero-based rotation index (absent
/// for empty input), range-coded ranks. No other program reads or writes it.
/// </para>
/// </remarks>
public static class BwtMtfBitTreeCompressor {
  /// <summary>
  /// Compresses data via BWT, Move-to-Front, and adaptive bit-tree coding.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var (bwt, index) = BurrowsWheelerTransform.Forward(data);
    var mtf = MoveToFrontTransform.Encode(bwt);

    Span<byte> indexHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader, index);
    output.Write(indexHeader);

    var encoder = new RangeEncoder(output);
    var trees = new[] { new BitTreeEncoder(8), new BitTreeEncoder(8) };

    var context = 0;
    foreach (var b in mtf) {
      trees[context].Encode(encoder, b);
      context = b == 0 ? 0 : 1;
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses data written by <see cref="Compress"/>.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    var index = BinaryPrimitives.ReadInt32LittleEndian(compressed[4..]);

    using var input = new MemoryStream(compressed[8..].ToArray());
    var decoder = new RangeDecoder(input);
    var trees = new[] { new BitTreeDecoder(8), new BitTreeDecoder(8) };

    var mtf = new byte[size];
    var context = 0;
    for (var i = 0; i < size; ++i) {
      var b = (byte)trees[context].Decode(decoder);
      mtf[i] = b;
      context = b == 0 ? 0 : 1;
    }

    var bwt = MoveToFrontTransform.Decode(mtf);
    return BurrowsWheelerTransform.Inverse(bwt, index);
  }
}
