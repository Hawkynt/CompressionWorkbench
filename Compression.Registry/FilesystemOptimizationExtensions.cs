namespace Compression.Registry;

/// <summary>Convenience API for option-aware filesystem maintenance.</summary>
public static class FilesystemOptimizationExtensions {
  /// <summary>
  /// Optimizes a filesystem layout with the requested sparse/link/compression
  /// transforms and emits the input unchanged when no candidate is smaller.
  /// </summary>
  public static void Optimize(
    this ILayoutOptimizable layout,
    Stream input,
    Stream output,
    FilesystemOptimizationOptions? options = null)
    => FilesystemOptimization.Optimize(layout, input, output, options);

  /// <summary>Returns the transforms the current writer can actually emit.</summary>
  public static FilesystemOptimizationFeatures GetOptimizationFeatures(this ILayoutOptimizable layout)
    => FilesystemOptimization.GetSupportedFeatures(layout);
}
