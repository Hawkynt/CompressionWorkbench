#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

public sealed class CdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Cdi";
  public string DisplayName => "CDI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cdi";
  public IReadOnlyList<string> Extensions => [".cdi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // CDI version identifiers (0x80000004/5/6) appear at a variable offset from EOF,
  // making fixed-offset header magic impractical; detection relies on extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DiscJuggler CDI disc image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CdiReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CdiReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
