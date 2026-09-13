#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Cbz;

/// <summary>
/// Comic book archive — a ZIP container of sequentially named page images, conventionally suffixed .cbz.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Comic_book_archive</c> — the .cbr/.cbz naming convention</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE — the underlying ZIP container spec</description></item>
/// </list>
/// </summary>
public sealed class CbzFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "CBZ";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".cbz"];

  /// <inheritdoc />
  public override string Description => "Comic book ZIP archive";
}
