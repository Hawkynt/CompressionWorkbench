#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a single-font .otf as <c>FULL.otf</c> + <c>metadata.ini</c> + per-glyph
/// SVG entries. OTFs with TrueType outlines ('glyf') split per glyph; OTFs with
/// CFF/CFF2 outlines emit FULL only and record the skip reason in metadata.ini.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/typography/opentype/spec/</c> — OpenType specification (sfnt tables, glyf/CFF outlines)</description></item>
///   <item><description>ISO/IEC 14496-22 "Open Font Format" — the same specification as an international standard</description></item>
///   <item><description><c>https://developer.apple.com/fonts/TrueType-Reference-Manual/</c> — Apple TrueType Reference Manual — glyf outline encoding</description></item>
/// </list>
/// </summary>
public sealed class OtfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Otf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "OTF (OpenType font — glyphs)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".otf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".otf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OTTO"u8.ToArray(), Confidence: 0.95),
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
public string Description => "OpenType font; FULL + metadata + per-glyph SVG (TrueType outlines).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    TtfFormatDescriptor.BuildEntries(Read(stream), defaultExt: ".otf")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in TtfFormatDescriptor.BuildEntries(Read(stream), defaultExt: ".otf")) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.EntryName, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.EntryName, entry.Bytes);
    }
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
