using System.Buffers.Binary;
using Compression.Core.Entropy.RangeCoding;
using Compression.Core.Transforms;

namespace Compression.Core.Entropy.ContextMixing.Bsc;

/// <summary>
/// A clean-room implementation of the BSC architecture: a Burrows-Wheeler
/// Transform, a Move-to-Front recoding, and a simple adaptive binary coder.
/// </summary>
/// <remarks>
/// <para>
/// Modelled after Ilya Grebnov's libbsc (<see href="https://github.com/IlyaGrebnov/libbsc"/>,
/// discussed at <see href="https://encode.su/threads/586-bsc-new-block-sorting-compressor"/>),
/// which follows the block sort with Move-to-Front and a lightweight adaptive
/// entropy stage rather than a full context-mixing ensemble. See also
/// Burrows &amp; Wheeler's original technical report and the general
/// "Block Sorting and Compression" literature this family descends from.
/// </para>
/// <para>
/// This is a reduced, from-specification reimplementation: the BWT
/// (<see cref="Transforms.BurrowsWheelerTransform"/>) output is recoded with
/// Move-to-Front (<see cref="Transforms.MoveToFrontTransform"/>) and each
/// resulting rank byte is entropy-coded MSB-first through an LZMA-style
/// adaptive bit-tree (<see cref="BitTreeEncoder"/>/<see cref="BitTreeDecoder"/>
/// over the validated <see cref="RangeEncoder"/>/<see cref="RangeDecoder"/>).
/// Two bit-trees are kept — one for ranks that immediately follow a zero rank
/// and one for the rest — since MTF output alternates between long zero runs
/// and scattered non-zero ranks; this single order-1 split is the entire
/// context model, deliberately far lighter than BCM's mixed context set,
/// matching where libbsc's actual entropy stage sits relative to full CM
/// coders.
/// </para>
/// </remarks>
public static class BscCompressor {
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
  /// Decompresses BSC-style compressed data.
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
