using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// Exposes FastLZ level-1 compression as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// Reference: Ariya Hidayat, "FastLZ", https://ariya.github.io/FastLZ/.
/// </summary>
public sealed class FastLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_FastLz";
  /// <inheritdoc/>
  public string DisplayName => "FastLZ";
  /// <inheritdoc/>
  public string Description => "Byte-aligned LZ77 compression tuned for speed, 8KB hash-matched window";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = FastLzCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : FastLzDecompressor.Decompress(data[4..], originalSize);
  }
}
