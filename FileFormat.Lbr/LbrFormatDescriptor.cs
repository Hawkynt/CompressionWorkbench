#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lbr;

public sealed class LbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Lbr";
  public string DisplayName => "LBR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lbr";
  public IReadOnlyList<string> Extensions => [".lbr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lbr", "LBR")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LbrReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName,
      (long)e.SectorCount * 128, (long)e.SectorCount * 128, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LbrReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LbrWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
