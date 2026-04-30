#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a TrueType Collection (.ttc) as an archive with:
/// <list type="bullet">
///   <item><description><c>FULL.ttc</c> — verbatim original collection</description></item>
///   <item><description><c>metadata.ini</c> — per-font glyph emission stats</description></item>
///   <item><description><c>fonts/&lt;i&gt;_&lt;name&gt;.{ttf,otf}</c> — sliced standalone member fonts</description></item>
///   <item><description><c>glyphs/&lt;i&gt;_&lt;name&gt;/U+XXXX.svg</c> — per-glyph SVG outlines (TrueType only)</description></item>
/// </list>
/// </summary>
public sealed class TtcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ttc";
  public string DisplayName => "TTC (TrueType collection)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ttc";
  public IReadOnlyList<string> Extensions => [".ttc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ttcf"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TrueType Collection; FULL + per-member fonts + per-glyph SVG.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(Read(stream), fullName: "FULL.ttc")
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.EntryName,
        OriginalSize: e.Bytes.Length, CompressedSize: e.Bytes.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false,
        LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(Read(stream), fullName: "FULL.ttc")) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(entry.EntryName, files))
        continue;
      FormatHelpers.WriteFile(outputDir, entry.EntryName, entry.Bytes);
    }
  }

  /// <summary>
  /// Builds the full entry list for a .ttc/.otc collection (used by both
  /// <see cref="TtcFormatDescriptor"/> and <see cref="OtcFormatDescriptor"/>).
  /// Each member's glyph emission is wrapped in try/catch — a malformed sub-font
  /// degrades to "fonts/N_..." entry only with parse_status=partial in metadata.ini.
  /// </summary>
  internal static IReadOnlyList<FontGlyphSplitter.SvgEntry> BuildEntries(byte[] full, string fullName) {
    var entries = new List<FontGlyphSplitter.SvgEntry>(64) {
      new(fullName, full),
    };

    List<TtcReader.Member> members;
    try {
      members = new TtcReader().Read(full);
    } catch {
      // Malformed collection — surface FULL only + a parse-status hint.
      entries.Add(new FontGlyphSplitter.SvgEntry(
        "metadata.ini",
        Encoding.UTF8.GetBytes("[collection]\nparse_status = partial\n")));
      return entries;
    }

    var metadata = new StringBuilder();
    metadata.Append("[collection]\n");
    metadata.Append("font_count = ").Append(members.Count).Append('\n');

    foreach (var member in members) {
      var fontName = TtfFormatDescriptor.ResolveFontName(member.Data);
      var folderSegment = $"{member.Index}_{FontGlyphSplitter.SanitiseFolderSegment(fontName ?? $"font_{member.Index}")}";

      // Always emit the per-member font slice.
      entries.Add(new FontGlyphSplitter.SvgEntry(
        $"fonts/{folderSegment}{member.Extension}", member.Data));

      // Per-glyph SVGs — wrap so any single member's failure doesn't poison the rest.
      FontGlyphSplitter.Stats stats;
      try {
        var (svgEntries, s) = FontGlyphSplitter.Split(member.Data, folderSegment);
        entries.AddRange(svgEntries);
        stats = s;
      } catch (Exception ex) {
        stats = new FontGlyphSplitter.Stats(
          fontName, 0, 0, 0, 0, 0, false, $"error: {ex.GetType().Name}");
      }

      metadata.Append('\n').Append(
        TtfFormatDescriptor.BuildMetadataIni(stats, fontIndex: member.Index));
    }

    entries.Add(new FontGlyphSplitter.SvgEntry(
      "metadata.ini", Encoding.UTF8.GetBytes(metadata.ToString())));
    return entries;
  }

  private static byte[] Read(Stream s) {
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
