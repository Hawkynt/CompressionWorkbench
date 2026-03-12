namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// PPMd Model I — Prediction by Partial Matching, variant I.
/// Used by RAR for PPMd compression. This variant uses interleaved context storage
/// and different update rules compared to Model H.
/// </summary>
/// <remarks>
/// Model I is characterized by:
/// <list type="bullet">
///   <item>Lower rescale threshold (1500) for more aggressive frequency adaptation.</item>
///   <item>Interleaved symbol/successor storage for cache locality.</item>
///   <item>Different context update exclusion rules compared to Model H.</item>
/// </list>
/// </remarks>
internal sealed class PpmdModelI : PpmdModelBase {
  /// <summary>Frequency total threshold for triggering a rescale in Model I.</summary>
  private const int RescaleThreshold = 1500;

  private readonly int _memorySize;

  /// <summary>
  /// Initializes a new PPMd Model I with the specified order and memory budget.
  /// </summary>
  /// <param name="order">Maximum context order (1..16). Default is 6.</param>
  /// <param name="memorySize">Memory budget in bytes (used for context capacity planning).</param>
  public PpmdModelI(int order, int memorySize) : base(order) => this._memorySize = memorySize;

  /// <summary>
  /// Initializes a new PPMd Model I with the specified order and default memory.
  /// </summary>
  /// <param name="order">Maximum context order (1..16).</param>
  public PpmdModelI(int order) : this(order, PpmdConstants.DefaultMemorySize) {
  }

  /// <inheritdoc />
  protected override int GetRescaleThreshold() => PpmdModelI.RescaleThreshold;
}
