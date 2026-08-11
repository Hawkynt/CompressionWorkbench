using Compression.Core.Streams;
using Compression.Registry;

namespace FileFormat.Bzip2;

/// <summary>
/// Exposes bzip2 as a benchmarkable building block.
/// Produces a complete bzip2 stream ("BZh" signature, block-size digit, one or more
/// Burrows-Wheeler blocks and the stream footer carrying the combined CRC), so the
/// payload is self-terminating and no extra uncompressed-size header is prepended.
/// </summary>
/// <remarks>
/// Stream layout per the bzip2 format description shipped with bzip2 1.0.8
/// (Julian Seward, <c>bzip2</c> manual, "Data format" appendix): RLE1 → BWT →
/// MTF → RLE2 → multi-table Huffman coding, blocks delimited by the 48-bit
/// magic 0x314159265359 and closed by 0x177245385090 plus a 32-bit combined CRC.
/// </remarks>
public sealed class Bzip2BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bzip2";
  /// <inheritdoc/>
  public string DisplayName => "bzip2";
  /// <inheritdoc/>
  public string Description => "Burrows-Wheeler transform with move-to-front and multi-table Huffman coding";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;

  /// <summary>Block size multiplier used for the benchmark stream (9 → 900 KB blocks).</summary>
  private const int BlockSize100k = 9;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var bzip2 = new Bzip2Stream(output, CompressionStreamMode.Compress, BlockSize100k, leaveOpen: true))
      bzip2.Write(data);

    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var bzip2 = new Bzip2Stream(input, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    bzip2.CopyTo(output);
    return output.ToArray();
  }
}
