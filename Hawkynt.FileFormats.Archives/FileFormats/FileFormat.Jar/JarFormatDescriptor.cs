#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Jar;

/// <summary>
/// Java Archive (JAR) — a ZIP container with a META-INF/MANIFEST.MF manifest.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.oracle.com/javase/8/docs/technotes/guides/jar/jar.html</c> — Oracle JAR File Specification</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE ZIP APPNOTE — the underlying container format</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/JAR_(file_format)</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class JarFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "JAR";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".jar"];

  /// <inheritdoc />
  public override string Description => "Java Archive (ZIP-based)";
}
