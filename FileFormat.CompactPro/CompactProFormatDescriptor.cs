#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CompactPro;

public sealed class CompactProFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "CompactPro";
  public string DisplayName => "Compact Pro";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cpt";
  public IReadOnlyList<string> Extensions => [".cpt"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x01], Confidence: 0.20)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("compactpro", "Compact Pro")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Macintosh Compact Pro archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CompactProReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.DataForkSize,
      e.DataForkCompressedSize, $"Method {e.DataForkMethod}", e.IsDirectory, false, e.ModifiedDate)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CompactProReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new CompactProWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
