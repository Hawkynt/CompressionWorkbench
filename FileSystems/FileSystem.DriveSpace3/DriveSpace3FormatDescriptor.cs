#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Read-only descriptor for Microsoft DriveSpace 3 CVF (DOS 6.22 /
/// Win 95 OSR2 / Win 98). Distinguished from DoubleSpace/DriveSpace 2
/// by the "MS_DSP3" MDBPB signature at file offset 3 (vs "MSDSP6.0"
/// for DoubleSpace and "MSDSP6.2" for DriveSpace 2 — both handled by
/// the sibling FileSystem.DoubleSpace project).
/// Shares the .cvf extension with DoubleSpace; FormatDetector
/// disambiguates by magic.
/// </summary>
public sealed class DriveSpace3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "DriveSpace3";
  public string DisplayName => "DriveSpace 3 CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".cvf";
  // Extension-shared with DoubleSpace; detection routes by MS_DSP3 magic.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MS_DSP3"u8.ToArray(), Offset: 3, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ds-lz77+huffman", "DS LZ77+Huffman")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Microsoft DriveSpace 3 CVF (DOS 6.22 / Win 95 OSR2 / 98) — stub: detection-only, opaque data region.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new DriveSpace3Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "DS3-LZ77+Huffman", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new DriveSpace3Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
