#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDisk;

public sealed class DcsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Dcs";
  public string DisplayName => "DCS (Amiga Disk Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dcs";
  public IReadOnlyList<string> Extensions => [".dcs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xpk", "XPK")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga DCS disk archive (XPK compression)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackDiskReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, "XPK", false, false, null
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
    var w = new PackDiskWriter("DCS\0");
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddTrack(File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
