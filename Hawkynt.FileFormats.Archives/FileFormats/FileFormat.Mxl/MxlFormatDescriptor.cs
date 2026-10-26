#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Mxl;

/// <summary>
/// Compressed MusicXML (MXL) — a ZIP holding <c>META-INF/container.xml</c>, whose first
/// <c>rootfile</c> names the score, plus an optional leading stored <c>mimetype</c> member
/// (<c>application/vnd.recordare.musicxml</c>) and any linked resources.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.w3.org/2021/06/musicxml40/tutorial/compressed-mxl-files/</c> — W3C MusicXML 4.0, compressed .mxl files</description></item>
///   <item><description>PRONOM <c>fmt/2005</c> — Compressed MusicXML</description></item>
/// </list>
/// </summary>
public sealed class MxlFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "MXL (compressed MusicXML)";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".mxl"];

  /// <inheritdoc />
  public override string Description => "Compressed MusicXML score (ZIP-based)";
}
