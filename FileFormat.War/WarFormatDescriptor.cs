#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.War;

public sealed class WarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "War";
  public string DisplayName => "WAR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".war";
  public IReadOnlyList<string> Extensions => [".war"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Java Web Application Archive (ZIP-based)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      w.AddEntry(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
  }
}
