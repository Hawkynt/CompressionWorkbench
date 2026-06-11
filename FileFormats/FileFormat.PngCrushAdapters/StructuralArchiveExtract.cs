#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Shared Extract / ExtractEntry plumbing for the structural pseudo-archive
/// descriptors (PNG / TIFF / DCX / ICNS / MPO). All five share one decomposition
/// model (a flat list of named byte blobs), so disk extraction and in-memory
/// single-entry extraction are factored here.
/// </summary>
public static class StructuralArchiveExtract {

  /// <summary>Writes the decomposed entries to <paramref name="outputDir"/>, honouring the optional name filter.</summary>
  public static void Extract(IReadOnlyList<StructuralArchiveHelper.Entry> entries, string outputDir, string[]? files) {
    foreach (var e in entries) {
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>Streams the bytes of the single named entry; no-op if the name isn't present.</summary>
  public static void ExtractEntry(IReadOnlyList<StructuralArchiveHelper.Entry> entries, string entryName, Stream output) {
    foreach (var e in entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      output.Write(e.Data, 0, e.Data.Length);
      return;
    }
  }
}
