#pragma warning disable CS1591
namespace FileFormat.Pdf;

/// <summary>
/// Represents an extractable resource from a PDF file: an image, an embedded
/// file attachment, or a synthesised single-page slice.
/// </summary>
public sealed class PdfEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int ObjectNumber { get; init; }
  public string Filter { get; init; } = "";
  public int Width { get; init; }
  public int Height { get; init; }
  /// <summary>For page-slice entries, the lazy data buffer produced by the splitter.</summary>
  internal byte[]? PageData { get; init; }
}
