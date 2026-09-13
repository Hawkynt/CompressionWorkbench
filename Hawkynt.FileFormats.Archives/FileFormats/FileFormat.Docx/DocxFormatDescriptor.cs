#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Docx;

/// <summary>
/// Office Open XML word-processing document (.docx) — an OPC/ZIP package per ECMA-376 / ISO/IEC 29500.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-376/</c> — ECMA-376 — Office Open XML file formats</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Office_Open_XML</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class DocxFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "DOCX";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".docx"];

  /// <inheritdoc />
  public override string Description => "Office Open XML document (ZIP-based)";
}
