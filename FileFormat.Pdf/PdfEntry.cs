#pragma warning disable CS1591
namespace FileFormat.Pdf;

/// <summary>
/// Represents an extracted image from a PDF file.
/// </summary>
public sealed class PdfEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int ObjectNumber { get; init; }
  public string Filter { get; init; } = "";
  public int Width { get; init; }
  public int Height { get; init; }
}
