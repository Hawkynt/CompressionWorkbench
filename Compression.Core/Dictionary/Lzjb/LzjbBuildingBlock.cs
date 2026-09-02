using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzjb;

/// <summary>
/// Exposes LZJB compression as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// Reference: Jeff Bonwick, LZJB, https://en.wikipedia.org/wiki/LZJB.
/// </summary>
public sealed class LzjbBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzjb";
  /// <inheritdoc/>
  public string DisplayName => "LZJB";
  /// <inheritdoc/>
  public string Description => "ZFS-era LZ77 variant with a 1KB window and an 8-flag copymap byte";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = LzjbCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : LzjbDecompressor.Decompress(data[4..], originalSize);
  }
}
