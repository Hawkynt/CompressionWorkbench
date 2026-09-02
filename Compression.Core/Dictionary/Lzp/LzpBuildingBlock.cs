using Compression.Registry;

namespace Compression.Core.Dictionary.Lzp;

/// <summary>
/// Exposes the LZP (Lempel-Ziv Prediction) algorithm as a benchmarkable building block.
/// LZP's format is self-describing (5-byte header with order and original size).
/// </summary>
public sealed class LzpBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzp";
  /// <inheritdoc/>
  public string DisplayName => "LZP";
  /// <inheritdoc/>
  public string Description => "Lempel-Ziv Prediction using context-based match prediction";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzpCompressor.Compress(data.ToArray());

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzpDecompressor.Decompress(data.ToArray());
}
