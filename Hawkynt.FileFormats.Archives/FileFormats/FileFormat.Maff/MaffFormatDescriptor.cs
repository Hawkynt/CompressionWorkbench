#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Maff;

/// <summary>
/// Mozilla Archive Format (MAFF) — a ZIP container of saved web pages plus RDF metadata.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Mozilla_Archive_Format</c> — Wikipedia</description></item>
///   <item><description>MAFF specification by the Mozilla Archive Format add-on project (formerly maf.mozdev.org; mozdev has shut down)</description></item>
/// </list>
/// </summary>
public sealed class MaffFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "MAFF";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".maff"];

  /// <inheritdoc />
  public override string Description => "Mozilla archive format (ZIP-based)";
}
