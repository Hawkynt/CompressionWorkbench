#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Ipa;

/// <summary>
/// Apple iOS application package (.ipa) — a ZIP archive laid out as Payload/AppName.app plus metadata.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/.ipa</c> — Wikipedia on the .ipa bundle layout</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE ZIP APPNOTE — the underlying container format</description></item>
/// </list>
/// </summary>
public sealed class IpaFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "IPA";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".ipa"];

  /// <inheritdoc />
  public override string Description => "iOS application bundle (ZIP-based)";
}
