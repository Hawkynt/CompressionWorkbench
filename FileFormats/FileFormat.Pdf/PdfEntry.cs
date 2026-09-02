#pragma warning disable CS1591
namespace FileFormat.Pdf;

/// <summary>
/// Represents an extractable resource from a PDF file: an image, an embedded
/// file attachment, or a synthesised single-page slice.
/// </summary>
public sealed class PdfEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  /// <summary>
  /// Gets or sets the object number.
  /// </summary>
public int ObjectNumber { get; init; }
  /// <summary>
  /// Gets or sets the filter.
  /// </summary>
public string Filter { get; init; } = "";
  /// <summary>
  /// Gets or sets the width.
  /// </summary>
public int Width { get; init; }
  /// <summary>
  /// Gets or sets the height.
  /// </summary>
public int Height { get; init; }
  /// <summary>For page-slice entries, the lazy data buffer produced by the splitter.</summary>
  internal byte[]? PageData { get; init; }
}
