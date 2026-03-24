#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zip;

public sealed class ZipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Zip";
  public string DisplayName => "ZIP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsBenchmark | FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".zip";
  public IReadOnlyList<string> Extensions => [".zip", ".zipx", ".jar", ".war", ".ear", ".xpi", ".odt", ".ods", ".odp", ".docx", ".xlsx", ".pptx", ".apk", ".ipa", ".nupkg", ".epub", ".cbz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'K', 0x03, 0x04], Confidence: 0.95),
    new([(byte)'P', (byte)'K', 0x05, 0x06], Confidence: 0.90)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("deflate", "Deflate", SupportsOptimize: true),
    new("store", "Store"), new("deflate64", "Deflate64"),
    new("bzip2", "BZip2"), new("lzma", "LZMA"), new("zstd", "Zstandard"), new("ppmd", "PPMd")
  ];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }
}
