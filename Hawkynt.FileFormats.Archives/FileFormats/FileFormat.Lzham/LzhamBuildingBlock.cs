using Compression.Registry;

namespace FileFormat.Lzham;

/// <summary>
/// LZHAM (Rich Geldreich) as a building block. A raw LZHAM stream cannot be decoded without its
/// parameters, so the block carries the ones LZHAM's own tool records: the <c>LZH0</c> header with
/// the dictionary size (the smallest that covers the input) and the decompressed size, followed by
/// the stream. The Huffman table update rate is LZHAM's default (8).
/// </summary>
public sealed class LzhamBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_LZHAM";
  /// <inheritdoc/>
  public string DisplayName => "LZHAM";
  /// <inheritdoc/>
  public string Description => "LZHAM: LZ77 with adaptive Huffman and binary arithmetic coding, in lzhamtest's LZH0 layout (dictionary size and length recorded, default table update rate); read by LZHAM's own decoder";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => LzhamStream.CompressFile(data, 0, LzhamTables.DefaultTableUpdateRate);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => LzhamStream.DecompressFile(data.ToArray(), LzhamTables.DefaultTableUpdateRate);
}
