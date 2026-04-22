#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDisk;

public sealed class PackDiskFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "PackDisk";
  public string DisplayName => "PackDisk (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pdsk";
  public IReadOnlyList<string> Extensions => [".pdsk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("PDSK"u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga PackDisk disk archive (XPK compression)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackDiskReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize,
      e.CompressedSize == e.Size ? "Stored" : "XPK", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackDiskReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: emit stored tracks (no XPK encoder needed). The reader recognises
    // a track as stored when no "XPKF" chunk header precedes the 5632-byte block.
    var w = new PackDiskWriter("PDSK");
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddTrack(File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
