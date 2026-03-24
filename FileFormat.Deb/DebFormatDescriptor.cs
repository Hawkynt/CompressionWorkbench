#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Deb;

public sealed class DebFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Deb";
  public string DisplayName => "DEB";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".deb";
  public IReadOnlyList<string> Extensions => [".deb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deb", "DEB")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DebReader(stream);
    var data = r.ReadDataEntries();
    return data.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.Data.Length, e.Data.Length,
      "deb", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DebReader(stream);
    foreach (var e in r.ReadDataEntries()) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Path)); continue; }
      WriteFile(outputDir, e.Path, e.Data);
    }
  }
}
