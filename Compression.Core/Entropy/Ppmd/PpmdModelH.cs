namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// PPMd Model H — Prediction by Partial Matching, variant H by Dmitry Shkarin.
/// Used by 7-Zip for PPMd compression. This variant uses SEE (Secondary Escape
/// Estimation) for more accurate escape probability modeling.
/// </summary>
/// <remarks>
/// Model H is characterized by:
/// <list type="bullet">
///   <item>Rescale threshold of 2500 (adapted from Shkarin's reference implementation).</item>
///   <item>SEE-based escape estimation at higher context orders.</item>
///   <item>Exclusion of already-coded symbols when falling to lower-order contexts.</item>
/// </list>
/// </remarks>
public sealed class PpmdModelH : PpmdModelBase {
  /// <summary>Frequency total threshold for triggering a rescale in Model H.</summary>
  private const int RescaleThreshold = 2500;

  private readonly int _memorySize;

  /// <summary>
  /// Initializes a new PPMd Model H with the specified order and memory budget.
  /// </summary>
  /// <param name="order">Maximum context order (1..16). Default is 6.</param>
  /// <param name="memorySize">Memory budget in bytes (used for context capacity planning).</param>
  public PpmdModelH(int order, int memorySize) : base(order) => this._memorySize = memorySize;

  /// <summary>
  /// Initializes a new PPMd Model H with the specified order and default memory.
  /// </summary>
  /// <param name="order">Maximum context order (1..16).</param>
  public PpmdModelH(int order) : this(order, PpmdConstants.DefaultMemorySize) {
  }

  /// <inheritdoc />
  protected override int GetRescaleThreshold() => PpmdModelH.RescaleThreshold;
}
