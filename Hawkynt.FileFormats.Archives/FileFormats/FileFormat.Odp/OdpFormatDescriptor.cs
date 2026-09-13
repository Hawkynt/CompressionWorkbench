#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Odp;

/// <summary>
/// OpenDocument presentation (.odp) — an OASIS ODF ZIP package.
///
/// References:
/// <list type="bullet">
///   <item><description>OASIS OpenDocument Format v1.3 (also ISO/IEC 26300) — the ODF package and XML specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OpenDocument</c> — Wikipedia</description></item>
///   <item><description><c>https://www.libreoffice.org</c> — LibreOffice — principal implementation</description></item>
/// </list>
/// </summary>
public sealed class OdpFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "ODP";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".odp"];

  /// <inheritdoc />
  public override string Description => "OpenDocument presentation (ZIP-based)";
}
