#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.ZipContainer;

namespace FileFormat.Xps;

/// <summary>
/// XPS / OpenXPS document — an OPC ZIP package (Microsoft / ECMA-388).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-388/</c> — ECMA-388 Open XML Paper Specification</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Open_XML_Paper_Specification</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class XpsFormatDescriptor : WipeableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "XPS / OpenXPS";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".xps", ".oxps"];

  /// <inheritdoc />
  public override string Description => "XPS document (Microsoft / ECMA-388 OPC PDF alternative)";

  /// <inheritdoc />
  public override IReadOnlyList<FormatMethodInfo> Methods => [new("xps", "XPS")];
}
