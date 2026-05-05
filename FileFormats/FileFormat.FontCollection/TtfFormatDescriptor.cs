#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a single-font TrueType .ttf as an archive of <c>FULL.ttf</c> +
/// <c>metadata.ini</c> + per-glyph SVG files under <c>glyphs/&lt;font_name&gt;/</c>.
/// Composite glyphs and CFF/CFF2 outlines are out of scope for this wave; both are
/// recorded in <c>metadata.ini</c> with a reason so the gap is visible.
/// </summary>
public sealed class TtfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ttf";
  public string DisplayName => "TTF (TrueType font — glyphs)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ttf";
  public IReadOnlyList<string> Extensions => [".ttf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x01, 0x00, 0x00], Confidence: 0.50),
    new("true"u8.ToArray(), Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TrueType font; FULL + metadata + per-glyph SVG.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = BuildEntries(Read(stream), defaultExt: ".ttf");
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.EntryName,
      OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(Read(stream), defaultExt: ".ttf")) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.EntryName, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.EntryName, entry.Bytes);
    }
  }

  /// <summary>
  /// Builds the FULL + metadata + glyphs entry list for a standalone .ttf/.otf.
  /// Per-glyph emission is wrapped in try/catch — even if cmap/glyf parsing fails
  /// catastrophically, FULL + a parse-status metadata file are still emitted so
  /// the archive view never goes empty.
  /// </summary>
  internal static IReadOnlyList<FontGlyphSplitter.SvgEntry> BuildEntries(byte[] font, string defaultExt) {
    var entries = new List<FontGlyphSplitter.SvgEntry>(64);
    entries.Add(new FontGlyphSplitter.SvgEntry($"FULL{defaultExt}", font));

    FontGlyphSplitter.Stats stats;
    try {
      var (svgEntries, s) = FontGlyphSplitter.Split(
        font,
        folderName: FontGlyphSplitter.SanitiseFolderSegment(
          ResolveFontName(font) ?? Path.GetFileNameWithoutExtension(defaultExt) ?? "Font"));
      entries.AddRange(svgEntries);
      stats = s;
    } catch (Exception ex) {
      stats = new FontGlyphSplitter.Stats(
        FontName: null, Total: 0, Emitted: 0, SkippedComposite: 0,
        SkippedNoOutline: 0, SkippedError: 0, IsCff: false,
        ParseStatus: $"error: {ex.GetType().Name}");
    }

    entries.Add(new FontGlyphSplitter.SvgEntry(
      "metadata.ini",
      Encoding.UTF8.GetBytes(BuildMetadataIni(stats, fontIndex: null))));

    return entries;
  }

  /// <summary>Returns the family name from the 'name' table or null on failure.</summary>
  internal static string? ResolveFontName(byte[] font) {
    try {
      var dir = SfntTableDir.Parse(font);
      if (!dir.Tables.TryGetValue("name", out var nameInfo)) return null;
      if (nameInfo.Offset + nameInfo.Length > (uint)font.Length) return null;
      return NameTableReader.ReadFamilyName(
        font.AsSpan((int)nameInfo.Offset, (int)nameInfo.Length));
    } catch { return null; }
  }

  internal static string BuildMetadataIni(FontGlyphSplitter.Stats stats, int? fontIndex) {
    var sb = new StringBuilder();
    var sectionTag = fontIndex.HasValue ? $"glyphs.{fontIndex.Value}" : "glyphs";
    sb.Append('[').Append(sectionTag).Append(']').Append('\n');
    if (stats.FontName != null) sb.Append("font_name = ").Append(stats.FontName).Append('\n');
    sb.Append("total = ").Append(stats.Total).Append('\n');
    sb.Append("emitted = ").Append(stats.Emitted).Append('\n');
    sb.Append("skipped_composite = ").Append(stats.SkippedComposite).Append('\n');
    sb.Append("skipped_no_outline = ").Append(stats.SkippedNoOutline).Append('\n');
    if (stats.SkippedError > 0)
      sb.Append("skipped_error = ").Append(stats.SkippedError).Append('\n');
    if (stats.IsCff) sb.Append("notes = CFF/CFF2 outlines not decoded this wave\n");
    if (stats.ParseStatus != null) sb.Append("parse_status = ").Append(stats.ParseStatus).Append('\n');
    return sb.ToString();
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
