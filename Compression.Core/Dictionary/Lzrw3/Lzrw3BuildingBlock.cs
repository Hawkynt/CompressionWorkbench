using Compression.Registry;

namespace Compression.Core.Dictionary.Lzrw3;

/// <summary>
/// Exposes LZRW3 compression as a benchmarkable building block. The block's bytes are exactly
/// the stream Ross N. Williams' reference <c>lzrw3.c</c> writes, flag bytes included.
/// Reference: Ross N. Williams, "LZRW3", http://ross.net/compression/lzrw3.html.
/// </summary>
public sealed class Lzrw3BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzrw3";
  /// <inheritdoc/>
  public string DisplayName => "LZRW3";
  /// <inheritdoc/>
  public string Description => "Ross Williams' LZRW1 variant whose copy items name a hash-table slot instead of an offset; both sides keep the table";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Lzrw3Compressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Lzrw3Decompressor.Decompress(data);
}
