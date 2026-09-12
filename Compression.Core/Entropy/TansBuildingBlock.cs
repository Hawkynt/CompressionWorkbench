using Compression.Core.Entropy.Fse;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes table-based Asymmetric Numeral Systems (tANS) as a benchmarkable entropy coder.
/// </summary>
/// <remarks>
/// FSE is a practical tANS construction; this explicit family entry uses a smaller 256-state
/// table than <see cref="FseBuildingBlock"/> so the registry can compare the table-size trade-off
/// instead of hiding tANS behind the FSE name.
/// </remarks>
public sealed class TansBuildingBlock : IBuildingBlock {
  private const int TableLog = 8;

  /// <inheritdoc/>
  public string Id => "BB_tANS";
  /// <inheritdoc/>
  public string DisplayName => "tANS";
  /// <inheritdoc/>
  public string Description => "Table-based Asymmetric Numeral Systems using a compact 256-state table";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => FseByteCodec.Compress(data, TableLog);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => FseByteCodec.Decompress(data);
}
