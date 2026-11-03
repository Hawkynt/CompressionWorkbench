using Compression.Registry;

namespace Compression.Core.Dictionary.PkwareDcl;

/// <summary>
/// PKWARE Data Compression Library "implode" (binary literals, 4 KiB dictionary) as a building
/// block. The stream is self-terminating, so no size prefix is added.
/// </summary>
public sealed class PkwareDclBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_PkwareDcl";
  /// <inheritdoc/>
  public string DisplayName => "PKWARE DCL Implode";
  /// <inheritdoc/>
  public string Description => "PKWARE Data Compression Library implode/explode: LZ77 with fixed prefix codes (MPQ, InstallShield 3)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => PkwareDclEncoder.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => PkwareDclDecoder.Decompress(data);
}
