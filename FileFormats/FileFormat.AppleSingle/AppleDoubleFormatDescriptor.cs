#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.AppleSingle;

/// <summary>
/// Pseudo-archive descriptor for AppleDouble (RFC 1740) sidecar files — the
/// resource fork + Finder metadata Macs leave alongside files when copied to
/// non-HFS filesystems (commonly named <c>._foo</c>). Same on-disk layout as
/// AppleSingle but the data fork lives in the sibling file rather than this one.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1740</c> — RFC 1740 — carries the AppleSingle/AppleDouble format description as an appendix</description></item>
///   <item><description>Apple "AppleSingle/AppleDouble Formats for Foreign Files Developer's Note" (1990) — the defining vendor document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class AppleDoubleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "AppleDouble";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "AppleDouble";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".appledouble";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".appledouble"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x05, 0x16, 0x07], Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
public string Description =>
    "AppleDouble (RFC 1740) sidecar — Finder metadata + resource fork " +
    "for files copied from HFS to non-HFS filesystems.";

  // Both descriptors delegate to the shared reader.
  private readonly AppleSingleFormatDescriptor _shared = new();

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) => this._shared.List(stream, password);
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    this._shared.Extract(stream, outputDir, password, files);
}
