#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wim;

public sealed class WimFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Wim";
  public string DisplayName => "WIM";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wim";
  public IReadOnlyList<string> Extensions => [".wim", ".swm", ".esd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'S', (byte)'W', (byte)'I', (byte)'M', 0x00, 0x00, 0x00], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wim", "WIM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows Imaging Format, file-based disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WimReader(stream);
    return r.Resources.Select((e, i) => new ArchiveEntryInfo(i, $"resource_{i}", e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "LZX" : "Store", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WimReader(stream);
    for (var i = 0; i < r.Resources.Count; ++i) {
      var name = $"resource_{i}";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ReadResource(i));
    }
  }
}
