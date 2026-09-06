namespace Compression.Registry;

/// <summary>
/// Signature metadata contributed by a format package that is not represented by one
/// <see cref="IFormatDescriptor"/> per externally supported format.
/// </summary>
public sealed record FormatDetectionSignature(
  string FormatId,
  string DisplayName,
  FormatCategory Category,
  string DefaultExtension,
  MagicSignature Signature
);

/// <summary>
/// Result of a package-native header detector. Header detectors complement fixed signatures for
/// formats whose identity is structural (or shares a generic container magic with other formats).
/// </summary>
public sealed record FormatHeaderMatch(
  string FormatId,
  string DisplayName,
  FormatCategory Category,
  string DefaultExtension,
  double Confidence
);

/// <summary>
/// Lets referenced Hawkynt format packages contribute their own generated detection metadata to the
/// workbench without copying signature tables into <c>Compression.Analysis</c>.
/// </summary>
/// <remarks>
/// Fixed signatures are used for byte-granular carving. <see cref="DetectHeader"/> is additionally
/// invoked at offset zero and at configurable alignment boundaries so structure-only formats remain
/// discoverable in raw media without turning a scan into O(bytes × formats).
/// </remarks>
public interface IFormatDetectionSource {
  /// <summary>All fixed content signatures exposed by the source package.</summary>
  IEnumerable<FormatDetectionSignature> Signatures { get; }

  /// <summary>
  /// Maximum prefix length useful to <see cref="DetectHeader"/>. Zero means this source has no
  /// package-native structural detector.
  /// </summary>
  int HeaderProbeLength => 0;

  /// <summary>
  /// Identifies a format whose header begins at byte zero of <paramref name="header"/>.
  /// Returns null when the source cannot identify it from content alone.
  /// </summary>
  FormatHeaderMatch? DetectHeader(ReadOnlySpan<byte> header) => null;
}
