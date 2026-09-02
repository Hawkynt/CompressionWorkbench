using Compression.Registry;

namespace Compression.Core.Dictionary.Lzrle;

/// <summary>
/// Exposes LZRLE (run-length-augmented LZ) as a benchmarkable building block.
/// </summary>
/// <remarks>
/// See <see cref="LzrleConstants"/> for the format layout and provenance notes.
/// </remarks>
public sealed class LzrleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzrle";
  /// <inheritdoc/>
  public string DisplayName => "LZRLE";
  /// <inheritdoc/>
  public string Description => "LZ77 dictionary compression augmented with a dedicated run-length token for repeated bytes";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzrleCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzrleDecompressor.Decompress(data);
}
