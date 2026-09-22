namespace Compression.Registry;

/// <summary>
/// Opt-in capability for changing only the compression representation of live
/// payloads while preserving their logical content and container format.
/// </summary>
/// <remarks>
/// This is deliberately separate from canonicalization, repacking and layout
/// geometry. A format must not implement this interface merely because it can
/// be recreated with different allocation parameters.
/// </remarks>
public interface ICompressionOptimizable {
  /// <summary>
  /// Re-encodes <paramref name="input"/> into <paramref name="output"/> using
  /// the format's best supported compression choices.
  /// </summary>
  void OptimizeCompression(Stream input, Stream output);
}
