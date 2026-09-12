using Compression.Core.Entropy.Fse;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes Finite State Entropy (FSE), the table-based ANS variant used by Zstandard,
/// as a benchmarkable building block.
/// </summary>
public sealed class FseBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_FSE";
  /// <inheritdoc/>
  public string DisplayName => "FSE";
  /// <inheritdoc/>
  public string Description => "Finite State Entropy using a 1024-state tANS table";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => FseByteCodec.Compress(data, FseByteCodec.DefaultTableLog);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => FseByteCodec.Decompress(data);
}
