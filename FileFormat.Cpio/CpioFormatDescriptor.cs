#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cpio;

public sealed class CpioFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Cpio";
  public string DisplayName => "CPIO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cpio";
  public IReadOnlyList<string> Extensions => [".cpio"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xC7, 0x71], Confidence: 0.90),
    new([(byte)'0', (byte)'7', (byte)'0', (byte)'7', (byte)'0', (byte)'7'], Confidence: 0.95)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cpio", "CPIO")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix copy-in/copy-out archive format";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CpioReader(stream);
    var all = r.ReadAll();
    return all.Select((x, i) => new ArchiveEntryInfo(i, x.Entry.Name, x.Entry.FileSize, x.Entry.FileSize,
      "cpio", x.Entry.IsDirectory, false, DateTimeOffset.FromUnixTimeSeconds(x.Entry.ModificationTime).DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CpioReader(stream);
    foreach (var (entry, data) in r.ReadAll()) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, entry.Name)); continue; }
      WriteFile(outputDir, entry.Name, data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new CpioWriter(output);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);
      else w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.Finish();
  }
}
