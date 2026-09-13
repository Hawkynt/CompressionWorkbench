#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Ods;

/// <summary>
/// OpenDocument spreadsheet (.ods) — an OASIS ODF ZIP package.
///
/// References:
/// <list type="bullet">
///   <item><description>OASIS OpenDocument Format v1.3 (also ISO/IEC 26300) — the ODF package and XML specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OpenDocument</c> — Wikipedia</description></item>
///   <item><description><c>https://www.libreoffice.org</c> — LibreOffice — principal implementation</description></item>
/// </list>
/// </summary>
public sealed class OdsFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "ODS";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".ods"];

  /// <inheritdoc />
  public override string Description => "OpenDocument spreadsheet (ZIP-based)";
}
