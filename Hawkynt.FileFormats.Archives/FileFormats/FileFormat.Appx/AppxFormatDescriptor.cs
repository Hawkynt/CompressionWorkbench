#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Appx;

/// <summary>
/// Windows app package (.appx/.msix) — ZIP-based container with AppxManifest.xml, block map and package signature.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows/msix/</c> — MSIX/APPX packaging documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/APPX</c> — format overview</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE — the underlying ZIP container spec</description></item>
/// </list>
/// </summary>
public sealed class AppxFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "APPX";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".appx", ".msix"];

  /// <inheritdoc />
  public override string Description => "Windows application package (ZIP-based)";
}
