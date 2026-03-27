#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zap;

public sealed class ZapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Zap";
  public string DisplayName => "ZAP (Amiga Disk Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".zap";
  public IReadOnlyList<string> Extensions => [".zap"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x5A, 0x41, 0x50, 0x00], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzrle", "LZ77+RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga ZAP disk archive (LZ77+RLE backward bitstream)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZapReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.CompressedSize, e.IsCompressed ? "LZ77+RLE" : "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZapReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
