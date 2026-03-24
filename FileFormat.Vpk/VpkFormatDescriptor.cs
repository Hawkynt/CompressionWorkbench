#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vpk;

public sealed class VpkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Vpk";
  public string DisplayName => "VPK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vpk";
  public IReadOnlyList<string> Extensions => [".vpk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x34, 0x12, 0xAA, 0x55], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("vpk", "VPK")];
  public string? TarCompressionFormatId => null;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VpkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath,
      e.PreloadBytes.Length + e.Length, e.PreloadBytes.Length + e.Length,
      "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VpkReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new VpkWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }
}
