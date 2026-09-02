using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes Differential Pulse-Code Modulation as a benchmarkable building block.
/// Stores differences between consecutive samples. The first sample is stored verbatim.
/// This is a reversible transform (not lossy) — it converts correlated signal data into
/// small residuals that compress well with entropy coders.
/// </summary>
public sealed class DpcmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Dpcm";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DPCM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Differential Pulse-Code Modulation, stores sample-to-sample differences";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];

    // First sample stored verbatim.
    result[0] = data[0];

    // Subsequent samples: store delta (mod 256) from previous sample.
    for (var i = 1; i < data.Length; i++)
      result[i] = (byte)(data[i] - data[i - 1]);

    return result;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];

    // First sample stored verbatim.
    result[0] = data[0];

    // Reconstruct by accumulating deltas.
    for (var i = 1; i < data.Length; i++)
      result[i] = (byte)(result[i - 1] + data[i]);

    return result;
  }
}
