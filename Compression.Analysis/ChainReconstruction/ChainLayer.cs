namespace Compression.Analysis.ChainReconstruction;

/// <summary>
/// A single layer in a compression/encoding chain.
/// </summary>
/// <param name="Algorithm">Name of the algorithm used in this layer.</param>
/// <param name="InputSize">Size of the input to this layer.</param>
/// <param name="OutputSize">Size of the output from this layer (decompressed).</param>
/// <param name="Confidence">Confidence in the algorithm identification.</param>
/// <param name="OutputData">The decompressed output data for this layer (may be null if not retained).</param>
public sealed record ChainLayer(
  string Algorithm,
  int InputSize,
  int OutputSize,
  double Confidence,
  byte[]? OutputData = null
);
