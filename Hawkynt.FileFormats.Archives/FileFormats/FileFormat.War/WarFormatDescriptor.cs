#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.War;

/// <summary>
/// Java Web Application Archive (WAR) — a ZIP/JAR with WEB-INF layout.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jakarta.ee/specifications/servlet/</c> — Jakarta Servlet specification — defines WAR packaging</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/WAR_(file_format)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class WarFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "WAR";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".war"];

  /// <inheritdoc />
  public override string Description => "Java Web Application Archive (ZIP-based)";
}
