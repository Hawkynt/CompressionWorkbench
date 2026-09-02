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
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzf";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZF";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Extremely fast hash-based LZ77 with a 2-byte minimum match and literal runs";
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
    var compressed = LzfCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : LzfDecompressor.Decompress(data[4..], originalSize);
  }
}
