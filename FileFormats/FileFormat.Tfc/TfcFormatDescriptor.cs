#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tfc;

/// <summary>
/// Unreal Engine 3 Texture File Cache (TFC) as shipped by Mass Effect — opaque compressed texture bundles.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/ME3Tweaks/LegendaryExplorer</c> — Legendary Explorer (ME3Tweaks) — implements Mass Effect TFC handling</description></item>
///   <item><description>Unreal Engine 3 streamed-texture cache; no official spec</description></item>
/// </list>
/// </summary>
public sealed class TfcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "Mass Effect TFC bundles are opaque LZX-compressed chunks referenced from the parent UPK package — " +
      "rebuilding without knowledge of UPK indices would break asset references.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  public string Id => "Tfc";
  public string DisplayName => "Mass Effect TFC";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tfc";
  public IReadOnlyList<string> Extensions => [".tfc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0xC1, 0x83, 0x2A, 0x9E }, Confidence: 0.85)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tfc", "TFC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Mass Effect Texture File Cache (WORM, reader exposes compressed bundle bytes opaquely)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TfcReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i,
      e.Name,
      e.UncompressedSize,
      e.CompressedSize,
      e.IsCompressed ? "LZX" : "Stored",
      false,
      false,
      null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TfcReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new TfcWriter(output, leaveOpen: true);
    foreach (var (_, data) in FormatHelpers.FlatFiles(inputs))
      w.AddBundle(data);
  }
}
