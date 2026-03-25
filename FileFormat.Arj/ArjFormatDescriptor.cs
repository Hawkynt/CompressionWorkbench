#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arj;

public sealed class ArjFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Arj";
  public string DisplayName => "ARJ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".arj";
  public IReadOnlyList<string> Extensions => [".arj"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x60, 0xEA], Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("1", "Compressed"), new("store", "Store"), new("2", "Method 2"), new("3", "Fastest")
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ARJ archive, popular DOS-era multi-volume compressor";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ArjReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"Method {e.Method}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ArjReader(stream, password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    byte arjMethod = options.MethodName switch {
      "store" => 0,
      "1" or "compressed" => 1,
      "2" => 2,
      "3" or "fastest" => 3,
      _ => options.Level switch {
        0 => (byte)0,
        >= 7 => (byte)1,
        >= 4 => (byte)2,
        _ => (byte)1,
      },
    };
    var w = new ArjWriter(arjMethod, password: options.Password);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);
      else w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
