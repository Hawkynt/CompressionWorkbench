#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Apk;

/// <summary>
/// Android application package (.apk) — a ZIP container holding the manifest, DEX bytecode, resources and native libraries.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.android.com/guide/components/fundamentals</c> — Android application fundamentals (APK packaging)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apk_(file_format)</c> — format overview</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE APPNOTE — the underlying ZIP container spec</description></item>
/// </list>
/// </summary>
public sealed class ApkFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "APK";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".apk"];

  /// <inheritdoc />
  public override string Description => "Android application package (ZIP-based)";
}
