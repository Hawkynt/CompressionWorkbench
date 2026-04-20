#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BinCue;

public sealed class BinCueFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "BinCue";
  public string DisplayName => "BIN/CUE";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".bin";
  public IReadOnlyList<string> Extensions => [".bin", ".cue"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No reliable file-header magic: the BIN file is raw sector data and the
  // CUE file is plain text; detection relies on extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BIN/CUE CD-ROM disc image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BinCueReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BinCueReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit the BIN as plain 2048-byte ISO 9660 cooked sectors. The reader
    // auto-detects this geometry. CUE sheet generation is not produced here -- the
    // Create API only gives us a single output stream; users wanting a CUE can
    // generate one trivially since a single Mode 1 data track is the default.
    var iso = new FileFormat.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    output.Write(iso.Build());
  }
}
