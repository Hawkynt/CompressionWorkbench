using Compression.Registry;

namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// Exposes LZRW1 compression as a benchmarkable building block. The block's bytes are exactly
/// the stream Ross N. Williams' reference <c>lzrw1_compress</c> writes, flag bytes included.
/// Reference: Ross N. Williams, "LZRW1", http://ross.net/compression/lzrw1.html.
/// </summary>
public sealed class Lzrw1BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzrw1";
  /// <inheritdoc/>
  public string DisplayName => "LZRW1";
  /// <inheritdoc/>
  public string Description => "Ross Williams' single-pass hash-matched LZ77: 4096-entry table, 16-item control words, offsets up to 4095";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Lzrw1Compressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Lzrw1Decompressor.Decompress(data);
}
