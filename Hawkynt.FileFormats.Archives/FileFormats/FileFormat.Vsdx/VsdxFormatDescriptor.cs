#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.ZipContainer;

namespace FileFormat.Vsdx;

/// <summary>
/// Microsoft Visio VSDX drawing — an OPC ZIP package.
///
/// References:
/// <list type="bullet">
///   <item><description>[MS-VSDX]: Visio Graphics Service File Format (Microsoft Open Specifications, learn.microsoft.com)</description></item>
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-376/</c> — ECMA-376 Part 2 — Open Packaging Conventions, the container VSDX uses</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Microsoft_Visio</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class VsdxFormatDescriptor : WipeableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "Visio Drawing";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".vsdx", ".vstx", ".vssx", ".vsdm", ".vstm", ".vssm"];

  /// <inheritdoc />
  public override string Description => "Microsoft Visio drawing (OPC ZIP package)";

  /// <inheritdoc />
  public override IReadOnlyList<FormatMethodInfo> Methods => [new("vsdx", "Visio")];
}
