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
  void OptimizeCompression(Stream input, Stream output) {
    if (this is IStreamFormatOperations streamOperations) {
      using var raw = new MemoryStream();
      streamOperations.Decompress(input, raw);
      raw.Position = 0;
      streamOperations.CompressOptimal(raw, output);
      return;
    }

    if (this is ILayoutOptimizable layout) {
      var features = FilesystemOptimization.GetSupportedFeatures(layout);
      var useTransparentCompression = features.HasFlag(FilesystemOptimizationFeatures.TransparentCompression);
      var tryCompressionParameters = features.HasFlag(FilesystemOptimizationFeatures.CompressionParameterSearch);
      if (useTransparentCompression || tryCompressionParameters) {
        FilesystemOptimization.Optimize(layout, input, output, new FilesystemOptimizationOptions {
          UseTransparentCompression = useTransparentCompression,
          TryCompressionParameters = tryCompressionParameters,
        });
        return;
      }
    }

    throw new NotSupportedException(
      $"The default {nameof(ICompressionOptimizable)} implementation requires either "
      + $"{nameof(IStreamFormatOperations)} or an explicitly registered filesystem compression profile.");
  }
}
