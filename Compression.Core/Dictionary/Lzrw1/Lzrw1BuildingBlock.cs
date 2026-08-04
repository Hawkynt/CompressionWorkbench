using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// Exposes LZRW1 compression as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// Reference: Ross N. Williams, "LZRW1", http://ross.net/compression/lzrw1.html.
/// </summary>
public sealed class Lzrw1BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzrw1";
  /// <inheritdoc/>
  public string DisplayName => "LZRW1";
  /// <inheritdoc/>
  public string Description => "Single-pass hash-matched LZ77 with a 4096-entry table and 16-item control words";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = Lzrw1Compressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return originalSize == 0 ? [] : Lzrw1Decompressor.Decompress(data[4..], originalSize);
  }
}
