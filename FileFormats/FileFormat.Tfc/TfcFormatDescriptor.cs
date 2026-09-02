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

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "Mass Effect TFC bundles are opaque LZX-compressed chunks referenced from the parent UPK package — " +
      "rebuilding without knowledge of UPK indices would break asset references.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Tfc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Mass Effect TFC";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".tfc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".tfc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0xC1, 0x83, 0x2A, 0x9E }, Confidence: 0.85)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("tfc", "TFC")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Mass Effect Texture File Cache (WORM, reader exposes compressed bundle bytes opaquely)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TfcReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new TfcWriter(output, leaveOpen: true);
    foreach (var (_, data) in FormatHelpers.FlatFiles(inputs))
      w.AddBundle(data);
  }
}
