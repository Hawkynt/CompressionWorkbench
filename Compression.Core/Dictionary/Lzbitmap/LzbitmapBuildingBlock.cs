using Compression.Registry;

namespace Compression.Core.Dictionary.Lzbitmap;

/// <summary>
/// Exposes Apple's LZBITMAP (see <see cref="Lzbitmap"/>) as a benchmarkable building block.
/// </summary>
public sealed class LzbitmapBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzbitmap";
  /// <inheritdoc/>
  public string DisplayName => "LZBITMAP";
  /// <inheritdoc/>
  public string Description => "Apple's LZBITMAP: 32 KiB chunks of 8-byte groups, each a bitmap choosing literals or bytes one period back";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Lzbitmap.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Lzbitmap.Decompress(data);
}
