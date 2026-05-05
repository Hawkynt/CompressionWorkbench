#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hpfs;

public sealed class HpfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Hpfs";
  public string DisplayName => "HPFS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [".img", ".hpfs"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Superblock magic at LBA 16 (offset 8192). First 4 bytes are sufficient for detection —
  // the full 8 bytes are validated by the reader.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xF9, 0x95, 0xE8, 0xF9], Offset: 8192, Confidence: 0.85),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "OS/2 High Performance File System (read-only: root directory + small files)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new HpfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new HpfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
