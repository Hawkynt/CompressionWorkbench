using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzf;

/// <summary>
/// Exposes LZF compression as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// Reference: Marc Lehmann, "liblzf", http://software.schmorp.de/pkg/liblzf.html.
/// </summary>
public sealed class LzfBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzf";
  /// <inheritdoc/>
  public string DisplayName => "LZF";
  /// <inheritdoc/>
  public string Description => "Marc Lehmann's LZF, as liblzf writes it: hash-matched LZ77 with 3-byte minimum matches, an 8 KiB window and literal runs";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = LzfCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : LzfDecompressor.Decompress(data[4..], originalSize);
  }
}
