#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Kmz;

/// <summary>
/// Google Earth KMZ — a ZIP archive bundling a root KML document plus referenced resources.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developers.google.com/kml/documentation</c> — Google KML documentation (KMZ packaging rules)</description></item>
///   <item><description>OGC KML 2.3 (OGC 12-007r2) — the standardized KML specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Keyhole_Markup_Language</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class KmzFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "KMZ";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".kmz"];

  /// <inheritdoc />
  public override string Description => "Google Earth KMZ (ZIP-based)";
}
