namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// Selects the amount of model structure used by the reduced clean-room MCM
/// implementation. The names mirror MCM's public command-line compression
/// modes, but the managed implementation is independent rather than a port of
/// the GPL implementation.
/// </summary>
public enum McmCompressionProfile : byte {
  /// <summary>Local byte contexts only; lowest memory and CPU cost.</summary>
  Turbo = 1,

  /// <summary>Adds the medium-order context group.</summary>
  Fast = 2,

  /// <summary>Adds the wide and sparse context group.</summary>
  Mid = 3,

  /// <summary>Adds one secondary-symbol-estimation refinement stage.</summary>
  High = 4,

  /// <summary>Uses the full reduced model with both refinement stages.</summary>
  Max = 5,
}
