#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.NuPkg;

/// <summary>
/// NuGet package (.nupkg) — a ZIP/OPC container with a .nuspec manifest.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/nuget/</c> — Microsoft NuGet documentation portal (package structure, .nuspec)</description></item>
///   <item><description><c>https://github.com/NuGet/NuGet.Client</c> — canonical client implementation</description></item>
/// </list>
/// </summary>
public sealed class NuPkgFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "NuPkg";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".nupkg"];

  /// <inheritdoc />
  public override string Description => "NuGet package (ZIP-based)";
}
