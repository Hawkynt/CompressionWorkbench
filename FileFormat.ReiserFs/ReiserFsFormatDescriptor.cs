#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ReiserFs;

public sealed class ReiserFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "ReiserFs";
  public string DisplayName => "ReiserFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".reiserfs";
  public IReadOnlyList<string> Extensions => [".reiserfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ReIsErFs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr2Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr3Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ReiserFS v3 filesystem image (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ReiserFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
