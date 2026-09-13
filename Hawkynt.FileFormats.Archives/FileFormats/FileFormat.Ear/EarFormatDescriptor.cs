#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Ear;

/// <summary>
/// Java EE / Jakarta EE Enterprise Application aRchive (.ear) — ZIP container with META-INF/application.xml and bundled WAR/JAR modules.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jakarta.ee/specifications/platform/</c> — Jakarta EE Platform specification — defines EAR packaging</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EAR_(file_format)</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class EarFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "EAR";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".ear"];

  /// <inheritdoc />
  public override string Description => "Java Enterprise Application Archive (ZIP-based)";
}
