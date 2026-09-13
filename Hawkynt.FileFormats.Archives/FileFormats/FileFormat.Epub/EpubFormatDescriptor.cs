#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Epub;

/// <summary>
/// EPUB e-book — ZIP-based OCF container with a mimetype entry, META-INF/container.xml and the OPF package document.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.w3.org/TR/epub-33/</c> — EPUB 3.3 — W3C Recommendation (incl. the OCF container)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EPUB</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class EpubFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "EPUB";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".epub"];

  /// <inheritdoc />
  public override string Description => "Electronic publication e-book (ZIP-based)";
}
