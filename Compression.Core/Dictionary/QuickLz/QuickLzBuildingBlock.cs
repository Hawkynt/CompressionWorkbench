using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.QuickLz;

/// <summary>
/// Exposes QuickLZ level-1 style compression as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// Reference: Lasse Mikkel Reinhold, "QuickLZ", http://www.quicklz.com/.
/// </summary>
public sealed class QuickLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_QuickLz";
  /// <inheritdoc/>
  public string DisplayName => "QuickLZ";
  /// <inheritdoc/>
  public string Description => "Speed-focused hash-matched LZ77 with a 32-bit control word and indexed matches";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = QuickLzCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : QuickLzDecompressor.Decompress(data[4..], originalSize);
  }
}
