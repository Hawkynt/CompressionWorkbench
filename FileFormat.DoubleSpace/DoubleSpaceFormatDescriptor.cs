#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DoubleSpace;

public sealed class DoubleSpaceFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "DoubleSpace";
  public string DisplayName => "DoubleSpace CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cvf";
  public IReadOnlyList<string> Extensions => [".cvf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(Encoding.ASCII.GetBytes("MSDSP6.0"), Offset: 3, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ds-lz77", "DS LZ77")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft DoubleSpace compressed volume file (MS-DOS 6.0)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DoubleSpaceReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "DS-LZ77", e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DoubleSpaceReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new DoubleSpaceWriter { DriveSpace = false };
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
