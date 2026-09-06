using Compression.Registry;
using Hawkynt.FileFormats.Images;
using ImageRegistry = Hawkynt.FileFormats.Images.FormatRegistry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Bridges PNGCrushCS' generated image registry into CompressionWorkbench's forensic detector.
/// The package remains the sole owner of image signatures and structural header predicates.
/// </summary>
public sealed class PngCrushImageDetectionSource : IFormatDetectionSource {

  /// <inheritdoc />
  public IEnumerable<FormatDetectionSignature> Signatures {
    get {
      foreach (var entry in ImageRegistry.AllFormats) {
        foreach (var signature in entry.MagicSignatures) {
          yield return new FormatDetectionSignature(
            entry.Format.ToString(),
            entry.Name,
            FormatCategory.Image,
            entry.PrimaryExtension,
            new Compression.Registry.MagicSignature(
              signature.Signature,
              signature.Offset,
              ConfidenceForLength(signature.Signature.Length)));
        }
      }
    }
  }

  /// <inheritdoc />
  public int HeaderProbeLength => 64;

  /// <inheritdoc />
  public FormatHeaderMatch? DetectHeader(ReadOnlySpan<byte> header) {
    var format = ImageRegistry.DetectFromBytes(header);
    if (format == ImageFormat.Unknown)
      return null;

    var entry = ImageRegistry.GetEntry(format);
    return new FormatHeaderMatch(
      format.ToString(),
      entry?.Name ?? format.ToString(),
      FormatCategory.Image,
      entry?.PrimaryExtension ?? string.Empty,
      0.90);
  }

  private static double ConfidenceForLength(int length) => length switch {
    <= 1 => 0.35,
    2 => 0.60,
    3 => 0.75,
    4 => 0.85,
    5 => 0.90,
    _ => 0.95,
  };
}
