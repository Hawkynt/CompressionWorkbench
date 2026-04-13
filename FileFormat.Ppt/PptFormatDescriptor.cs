#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ppt;

public sealed class PptFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ppt";
  public string DisplayName => "PPT";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ppt";
  public IReadOnlyList<string> Extensions => [".ppt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cfb", "Compound File Binary")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft PowerPoint 97-2003 presentation (OLE2)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Msi.MsiReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored",
      e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Msi.MsiReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
