#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes an OpenType Collection (.otc) — same container format as .ttc (same
/// 'ttcf' magic) — as <c>FULL.otc</c> + <c>metadata.ini</c> +
/// <c>fonts/&lt;i&gt;_&lt;name&gt;.{otf,ttf}</c> + <c>glyphs/&lt;i&gt;_&lt;name&gt;/U+XXXX.svg</c>.
/// CFF-outline members are recognised but produce no glyph SVGs (recorded in metadata).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/typography/opentype/spec/</c> — OpenType specification — the 'ttcf' collection header is defined in the font-file tables chapter</description></item>
///   <item><description><c>https://developer.apple.com/fonts/TrueType-Reference-Manual/</c> — Apple TrueType Reference Manual</description></item>
/// </list>
/// </summary>
public sealed class OtcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Otc";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "OTC (OpenType collection)";
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
public string DefaultExtension => ".otc";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".otc"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Magic overlaps with TTC ('ttcf'). Extension drives disambiguation.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
public string Description => "OpenType Collection; FULL + per-member fonts + per-glyph SVG.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    TtcFormatDescriptor.BuildEntries(Read(stream), fullName: "FULL.otc")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in TtcFormatDescriptor.BuildEntries(Read(stream), fullName: "FULL.otc")) {
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
