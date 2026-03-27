#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nds;

public sealed class NdsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nds";
  public string DisplayName => "NDS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nds";
  public IReadOnlyList<string> Extensions => [".nds"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A, 0xA2, 0x21], Offset: 0xC0, Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("nds", "NDS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nintendo DS ROM image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NdsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size, e.Size,
      "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NdsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
