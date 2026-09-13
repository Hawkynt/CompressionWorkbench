#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Xlsx;

/// <summary>
/// Office Open XML spreadsheet (.xlsx) — an OPC ZIP package.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-376/</c> — ECMA-376 Office Open XML File Formats (also ISO/IEC 29500)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Office_Open_XML</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class XlsxFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "XLSX";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".xlsx"];

  /// <inheritdoc />
  public override string Description => "Office Open XML spreadsheet (ZIP-based)";
}
