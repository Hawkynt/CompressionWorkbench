namespace Compression.Core.Entropy.ContextMixing.TwoLevel;

/// <summary>
/// How much of the model graph of <see cref="ContextMixingTwoLevelCompressor"/> is enabled. Each
/// step adds to the one before it.
/// </summary>
public enum ContextMixingTwoLevelProfile : byte {
  /// <summary>Local byte contexts only (orders 0-2); lowest memory and CPU cost.</summary>
  Local = 1,

  /// <summary>Adds the medium-order context group (orders 3-4).</summary>
  Medium = 2,

  /// <summary>Adds the wide and sparse context group (order 6, skip-1).</summary>
  Wide = 3,

  /// <summary>Adds one secondary-symbol-estimation refinement stage.</summary>
  WideSse = 4,

  /// <summary>The full model with both refinement stages.</summary>
  WideDoubleSse = 5,
}
