#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.MinixFs;

public sealed class MinixFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "MinixFs";
  public string DisplayName => "Minix FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".minix";
  public IReadOnlyList<string> Extensions => [".minix", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x5A, 0x4D], Offset: 1048, Confidence: 0.80f),  // v3: magic 0x4D5A at sb+24
    new([0x7F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 14-char names
    new([0x8F, 0x13], Offset: 1040, Confidence: 0.80f),  // v1 30-char names
    new([0x68, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 14-char names
    new([0x78, 0x24], Offset: 1040, Confidence: 0.80f),  // v2 30-char names
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("minixfs", "Minix FS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Minix file system image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MinixFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MinixFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new MinixFsWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }
}
