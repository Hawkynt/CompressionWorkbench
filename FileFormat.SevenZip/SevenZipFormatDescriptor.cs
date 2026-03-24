#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.SevenZip;

public sealed class SevenZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "SevenZip";
  public string DisplayName => "7z";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".7z";
  public IReadOnlyList<string> Extensions => [".7z"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("lzma2", "LZMA2"), new("lzma", "LZMA"), new("ppmd", "PPMd"),
    new("bzip2", "BZip2"), new("deflate", "Deflate"), new("copy", "Store")
  ];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SevenZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SevenZipReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }
}
