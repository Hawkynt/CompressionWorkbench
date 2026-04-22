#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a single-font .otf. OTF fonts with TrueType outlines ('glyf') slice
/// the same way as .ttf; fonts with CFF/CFF2 outlines currently emit a single
/// whole-file entry, since CFF charstring decoding is a separate project.
/// </summary>
public sealed class OtfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Otf";
  public string DisplayName => "OTF (OpenType font — glyphs)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".otf";
  public IReadOnlyList<string> Extensions => [".otf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("OTTO"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "OpenType font; TrueType-outline OTFs expose per-glyph SVG entries.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    GlyphCatalog.Build(Read(stream)).Select((g, i) => new ArchiveEntryInfo(
      Index: i, Name: g.Name, OriginalSize: g.Svg.Length, CompressedSize: g.Svg.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in GlyphCatalog.Build(Read(stream))) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.Name, Encoding.UTF8.GetBytes(entry.Svg));
    }
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
