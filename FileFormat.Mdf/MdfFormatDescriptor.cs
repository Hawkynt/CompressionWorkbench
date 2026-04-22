#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mdf;

public sealed class MdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Mdf";
  public string DisplayName => "MDF/MDS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".mdf";
  public IReadOnlyList<string> Extensions => [".mdf", ".mds"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // MDF has no file-header magic; it is raw sector data.
  // Detection relies on the ISO 9660 PVD heuristic (CD001 at LBA 16).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Alcohol 120% MDF/MDS disc image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MdfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MdfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit plain 2048-byte ISO 9660 sectors. The reader's geometry detection
    // recognises this. The accompanying .MDS metadata sidecar isn't produced (the
    // Create API is single-stream); MDS isn't required to extract MDF content.
    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
  }
}
