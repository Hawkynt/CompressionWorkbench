#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Adapts PNGCrushCS' PNG chunk layout to CompressionWorkbench's block-map view.
/// The PNG parser and byte offsets have a single owner in <c>Hawkynt.FileFormats.Images</c>;
/// this class keeps only Workbench-specific visualization labels/classification.
/// </summary>
public static class PngLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      yield break;

    using var copy = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(copy);

    foreach (var chunk in Hawkynt.FileFormats.Images.FormatRegistry.EnumerateChunks(copy.ToArray()))
      yield return Classify(chunk.Name, chunk.Offset, chunk.Length);
  }

  private static DefragBlockInfo Classify(string type, long offset, long size) => type switch {
    "SIGNATURE" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "PNG signature"),
    "IHDR" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "IHDR (Image header)"),
    "PLTE" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "PLTE (Palette)"),
    "IDAT" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "IDAT (Image data)", Classification: DefragBlockClass.Normal),
    "IEND" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "IEND (End marker)"),
    "tEXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "tEXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "iTXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "iTXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "zTXt" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "zTXt (Text metadata)", Classification: DefragBlockClass.Cold),
    "eXIf" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "eXIf (EXIF)", Classification: DefragBlockClass.Hot),
    "iCCP" => new DefragBlockInfo(offset, size, DefragBlockKind.Used, FileName: "iCCP (ICC Profile)", Classification: DefragBlockClass.Cold),
    "pHYs" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "pHYs (Display hints)"),
    "tIME" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "tIME (Display hints)"),
    "gAMA" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "gAMA (Display hints)"),
    "cHRM" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "cHRM (Display hints)"),
    "sRGB" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sRGB (Display hints)"),
    "sBIT" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sBIT (Display hints)"),
    "bKGD" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "bKGD (Display hints)"),
    "tRNS" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "tRNS (Transparency)"),
    "hIST" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "hIST (Histogram)"),
    "sPLT" => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: "sPLT (Suggested palette)"),
    _ => new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, FileName: type),
  };
}
