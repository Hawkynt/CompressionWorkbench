#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackIt;

public sealed class PackItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "PackIt";
  public string DisplayName => "PackIt";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pit";
  public IReadOnlyList<string> Extensions => [".pit"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'M', (byte)'a', (byte)'g'], Confidence: 0.85),
    new([(byte)'P', (byte)'M', (byte)'a', (byte)'4'], Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("packit", "PackIt")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PackIt classic Macintosh archive (.pit), Harry Chesley, 1984";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackItReader(stream, leaveOpen: true);
    return r.Entries
      .Select((e, i) => new ArchiveEntryInfo(
        i,
        e.Name,
        e.DataForkSize,
        e.DataForkSize,
        e.IsCompressed ? "Huffman" : "Stored",
        false,
        false,
        DateTime.MinValue))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackItReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new PackItWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
