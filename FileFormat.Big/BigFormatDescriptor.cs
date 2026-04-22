#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Big;

public sealed class BigFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Big";
  public string DisplayName => "BIG";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".big";
  public IReadOnlyList<string> Extensions => [".big"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("BIGF"u8.ToArray(), Confidence: 0.90),
    new("BIG4"u8.ToArray(), Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("big", "BIG")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "EA Games resource archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BigReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Size, e.Size,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BigReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new BigWriter(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
  }
}
