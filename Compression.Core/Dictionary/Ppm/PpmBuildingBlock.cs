using Compression.Registry;

namespace Compression.Core.Dictionary.Ppm;

/// <summary>
/// Exposes PPM (Prediction by Partial Matching) as a benchmarkable building
/// block: an order-3 finite-context model with escape method C and full
/// exclusion, driving a Witten-Neal-Cleary arithmetic coder. See
/// <see cref="PpmCompressor"/> for the full algorithm description and citations.
/// </summary>
public sealed class PpmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_PPM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PPM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Prediction by Partial Matching (Cleary & Witten): order-3 context model with escape method C and full exclusion, arithmetic coded";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) => PpmCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) => PpmCompressor.Decompress(data);
}
