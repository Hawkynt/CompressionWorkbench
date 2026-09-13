#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Xpi;

/// <summary>
/// Mozilla XPI extension package (ZIP-based) for Firefox/Thunderbird.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://extensionworkshop.com/</c> — Mozilla Extension Workshop — extension packaging documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/XPInstall</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class XpiFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "XPI";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".xpi"];

  /// <inheritdoc />
  public override string Description => "Firefox/Thunderbird extension (ZIP-based)";
}
