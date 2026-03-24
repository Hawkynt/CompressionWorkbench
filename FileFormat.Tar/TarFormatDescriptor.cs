#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tar;

public sealed class TarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Tar";
  public string DisplayName => "TAR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".tar";
  public IReadOnlyList<string> Extensions => [".tar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x75, 0x73, 0x74, 0x61, 0x72], Offset: 257, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tar", "TAR")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TarReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e) {
      entries.Add(new(i++, e.Name, e.Size, e.Size, "tar", e.IsDirectory, false, e.ModifiedTime.DateTime));
      r.Skip();
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TarReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.Name, files)) { r.Skip(); continue; }
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var data = new byte[e.Size];
      es.ReadExactly(data);
      WriteFile(outputDir, e.Name, data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new TarWriter(output);
    foreach (var i in inputs) {
      if (i.IsDirectory) {
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = 0, TypeFlag = (byte)'5' }, []);
      } else {
        var data = File.ReadAllBytes(i.FullPath);
        w.AddEntry(new TarEntry { Name = i.ArchiveName, Size = data.Length }, data);
      }
    }
    w.Finish();
  }
}
