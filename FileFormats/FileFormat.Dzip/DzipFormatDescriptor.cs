#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dzip;

public sealed class DzipFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Dzip";
  public string DisplayName => "Bloodlines DZIP";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  // Bloodlines ships its DZIPs with a .vpk extension, but that conflicts with Valve VPK
  // (a much more common format), so we register only ".dzip" here and rely on magic-byte
  // detection for the in-game files.
  public string DefaultExtension => ".dzip";
  public IReadOnlyList<string> Extensions => [".dzip"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DZIP"u8.ToArray(), Confidence: 0.92)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dzip", "DZIP")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Vampire The Masquerade Bloodlines (DZIP v2) — WORM, reader handles LZSS-compressed entries";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DzipReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize,
      e.CompressionFlag == 0 ? "Stored" : "LZSS",
      false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DzipReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new DzipWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddEntry(name, data);
  }
}
