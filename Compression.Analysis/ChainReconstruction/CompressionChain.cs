namespace Compression.Analysis.ChainReconstruction;

/// <summary>
/// An ordered list of compression/encoding layers that were peeled off the input data.
/// </summary>
/// <param name="Layers">The layers in order from outermost to innermost.</param>
/// <param name="FinalData">The innermost data after all layers were removed.</param>
public sealed record CompressionChain(
  IReadOnlyList<ChainLayer> Layers,
  byte[] FinalData
) {
  /// <summary>Number of layers in the chain.</summary>
  public int Depth => Layers.Count;
}
