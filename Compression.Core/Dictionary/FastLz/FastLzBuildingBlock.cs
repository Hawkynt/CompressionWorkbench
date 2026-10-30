using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// Exposes FastLZ as a benchmarkable building block: a 4-byte LE uncompressed size, then the
/// block <c>fastlz_compress</c> writes (level 1 below 64 KiB, level 2 from there on).
/// Reference: Ariya Hidayat, "FastLZ", https://ariya.github.io/FastLZ/.
/// </summary>
public sealed class FastLzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_FastLz";
  /// <inheritdoc/>
  public string DisplayName => "FastLZ";
  /// <inheritdoc/>
  public string Description => "Ariya Hidayat's FastLZ, as fastlz_compress writes it: byte-aligned LZ77 with an 8 KiB window, level 2 adding long lengths and 64 KiB far distances";
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
