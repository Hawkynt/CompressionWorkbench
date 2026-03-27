#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nrg;

public sealed class NrgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nrg";
  public string DisplayName => "NRG";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nrg";
  public IReadOnlyList<string> Extensions => [".nrg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // NRG magic is a footer signature ("NER5" or "NERO" at a variable offset from EOF),
  // which cannot be represented as a fixed-offset MagicSignature.
  // Detection relies on the file extension and footer heuristic in NrgReader.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nero Burning ROM disc image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NrgReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NrgReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
