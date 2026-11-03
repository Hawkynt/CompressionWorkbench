using Compression.Registry;

namespace FileFormat.Ctw;

/// <summary>
/// CTW 0.1 (Context Tree Weighting, TU Eindhoven) as a building block: a complete <c>.ctw</c> file
/// with the reference tool's default parameters (depth 6, 2^22 nodes, zero-redundancy estimator,
/// strict pruning), byte-identical to what <c>ctw e</c> writes. The header records the parameters,
/// so the block decodes on its own.
/// </summary>
public sealed class CtwBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_CTW";
  /// <inheritdoc/>
  public string DisplayName => "CTW 0.1";
  /// <inheritdoc/>
  public string Description => "Context Tree Weighting as in the CTW 0.1 reference utility: eight pruned binary context trees, zero-redundancy estimator, log-domain arithmetic coding; byte-identical to the reference";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CtwStream.Encode(data, new CtwSettings());

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CtwStream.Decode(data.ToArray());
}
