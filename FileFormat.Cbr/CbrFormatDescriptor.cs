#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cbr;

public sealed class CbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Cbr";
  public string DisplayName => "CBR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cbr";
  public IReadOnlyList<string> Extensions => [".cbr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rar", "RAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book RAR archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method{e.CompressionMethod}", e.IsDirectory, false, e.ModifiedTime?.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }
}
