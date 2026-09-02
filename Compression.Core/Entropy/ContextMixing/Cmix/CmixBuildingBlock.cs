using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Cmix;

/// <summary>
/// Exposes the reduced cmix-style model set as a benchmarkable building block.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the real cmix.</b> The reference compressor
/// (<see href="https://github.com/byronknoll/cmix"/>) is a large ensemble of
/// dozens of models, including neural-network sub-models, mixed through
/// multiple layers. This building block implements a small, explicitly
/// documented subset: hashed byte-history contexts (orders 0, 1, 2, 3, 4, 6),
/// one word context, and one match model, combined by a single mixer and
/// refined by a two-stage SSE chain. See <see cref="CmixCompressor"/> for the
/// full model list and citations.
/// </para>
/// </remarks>
public sealed class CmixBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Cmix";
  /// <inheritdoc/>
  public string DisplayName => "CMIX (reduced model set)";
  /// <inheritdoc/>
  public string Description => "Reduced cmix-style subset: orders 0-6 + word + match model, one mixer, two-stage SSE (not the full cmix ensemble)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CmixCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CmixCompressor.Decompress(data);
}
