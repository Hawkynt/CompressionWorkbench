#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CramFs;

public sealed class CramFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "CramFs";
  public string DisplayName => "CramFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cramfs";
  public IReadOnlyList<string> Extensions => [".cramfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x45, 0x3D, 0xCD, 0x28], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cramfs", "CramFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux Compressed ROM filesystem";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CramFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size, -1,
      "cramfs", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CramFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new CramFsWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName.TrimEnd('/'));
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.ArchiveName, data);
      }
    }
  }
}
