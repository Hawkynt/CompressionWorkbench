#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.AppleSingle;

/// <summary>
/// Pseudo-archive descriptor for AppleDouble (RFC 1740) sidecar files — the
/// resource fork + Finder metadata Macs leave alongside files when copied to
/// non-HFS filesystems (commonly named <c>._foo</c>). Same on-disk layout as
/// AppleSingle but the data fork lives in the sibling file rather than this one.
/// </summary>
public sealed class AppleDoubleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "AppleDouble";
  public string DisplayName => "AppleDouble";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".appledouble";
  public IReadOnlyList<string> Extensions => [".appledouble"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x05, 0x16, 0x07], Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AppleDouble (RFC 1740) sidecar — Finder metadata + resource fork " +
    "for files copied from HFS to non-HFS filesystems.";

  // Both descriptors delegate to the shared reader.
  private readonly AppleSingleFormatDescriptor _shared = new();

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => this._shared.List(stream, password);
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    this._shared.Extract(stream, outputDir, password, files);
}
