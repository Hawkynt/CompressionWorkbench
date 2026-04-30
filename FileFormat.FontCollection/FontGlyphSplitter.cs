#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.FontCollection;

/// <summary>
/// Splits a single SFNT font (.ttf / .otf) into a set of per-glyph SVG entries
/// addressed by Unicode codepoint, plus emits stats describing what was emitted
/// and what was skipped (composite glyphs, empty .notdef-style records, CFF —
/// the latter is recognised but not decoded in this wave).
/// </summary>
internal static class FontGlyphSplitter {

  /// <summary>One per-glyph SVG entry (entryName uses forward slashes).</summary>
  public sealed record SvgEntry(string EntryName, byte[] Bytes);

  /// <summary>Aggregate statistics about per-font glyph emission.</summary>
  public sealed record Stats(
    string? FontName,
    int Total,
    int Emitted,
    int SkippedComposite,
    int SkippedNoOutline,
    int SkippedError,
    bool IsCff,
    string? ParseStatus);

  /// <summary>
  /// Builds per-glyph SVGs for the supplied font bytes. Errors during parsing of
  /// individual glyphs are caught and recorded in <see cref="Stats.SkippedError"/>;
  /// global parse failure (missing tables, truncated header) sets
  /// <see cref="Stats.ParseStatus"/> to "partial" and returns no entries.
  /// </summary>
  public static (IReadOnlyList<SvgEntry> Entries, Stats Stats) Split(
      byte[] font, string folderName) {
    SfntTableDir dir;
    try {
      dir = SfntTableDir.Parse(font);
    } catch (Exception) {
      return ([], new Stats(null, 0, 0, 0, 0, 0, false, "partial"));
    }

    string? fontName = null;
    if (dir.Tables.TryGetValue("name", out var nameInfo) &&
        nameInfo.Offset + nameInfo.Length <= (uint)font.Length) {
      try {
        fontName = NameTableReader.ReadFamilyName(
          font.AsSpan((int)nameInfo.Offset, (int)nameInfo.Length));
      } catch { /* fall through to null fontName */ }
    }

    // CFF/OTTO (PostScript Type 2 outlines) — out of scope for this wave.
    // Recognise + report; emit no glyph SVGs.
    if (!dir.Tables.ContainsKey("glyf")) {
      var isCff = dir.Tables.ContainsKey("CFF ") || dir.Tables.ContainsKey("CFF2");
      return ([], new Stats(fontName, 0, 0, 0, 0, 0, isCff, isCff ? "skipped_cff" : "no_outlines"));
    }

    if (!dir.Tables.TryGetValue("loca", out var locaInfo) ||
        !dir.Tables.TryGetValue("head", out var headInfo) ||
        !dir.Tables.TryGetValue("maxp", out var maxpInfo))
      return ([], new Stats(fontName, 0, 0, 0, 0, 0, false, "partial"));

    int unitsPerEm;
    int numGlyphs;
    short indexToLocFormat;
    uint[] glyfOffsets;
    Dictionary<int, int> codepointToGlyph;

    try {
      var head = font.AsSpan((int)headInfo.Offset, (int)headInfo.Length);
      unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head[18..]);
      indexToLocFormat = BinaryPrimitives.ReadInt16BigEndian(head[50..]);
      numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan((int)maxpInfo.Offset + 4));

      var loca = font.AsSpan((int)locaInfo.Offset, (int)locaInfo.Length);
      glyfOffsets = new uint[numGlyphs + 1];
      for (var i = 0; i <= numGlyphs; ++i)
        glyfOffsets[i] = indexToLocFormat == 0
          ? (uint)BinaryPrimitives.ReadUInt16BigEndian(loca[(2 * i)..]) * 2u
          : BinaryPrimitives.ReadUInt32BigEndian(loca[(4 * i)..]);

      codepointToGlyph = dir.Tables.TryGetValue("cmap", out var cmapInfo)
        ? CmapReader.BuildCodepointToGlyph(font.AsSpan((int)cmapInfo.Offset, (int)cmapInfo.Length))
        : [];
    } catch (Exception) {
      return ([], new Stats(fontName, 0, 0, 0, 0, 0, false, "partial"));
    }

    var glyf = font.AsSpan((int)glyfInfoOffset(dir), (int)glyfInfoLength(dir));
    var entries = new List<SvgEntry>(codepointToGlyph.Count);
    var emitted = 0;
    var skippedComposite = 0;
    var skippedNoOutline = 0;
    var skippedError = 0;

    // Iterate codepoints in ascending order so SVG output is reproducible across runs.
    foreach (var (cp, gid) in codepointToGlyph.OrderBy(kv => kv.Key)) {
      if (gid <= 0 || gid >= numGlyphs) continue; // skip .notdef + out-of-range
      try {
        var start = glyfOffsets[gid];
        var end = glyfOffsets[gid + 1];
        if (end <= start) {
          // Empty glyph record — advance-only glyph (e.g. space, NBSP).
          ++skippedNoOutline;
          continue;
        }
        var glyphBytes = glyf.Slice((int)start, (int)(end - start));
        var decoded = TrueTypeGlyphDecoder.Decode(glyphBytes);
        if (decoded.IsComposite) {
          // Out-of-scope this wave per plan; recorded in metadata.
          ++skippedComposite;
          continue;
        }
        if (decoded.Contours.Count == 0) {
          ++skippedNoOutline;
          continue;
        }
        var svg = GlyphSvgEmitter.Emit(decoded, unitsPerEm, FormatCodepoint(cp));
        var fileName = $"U+{cp:X4}.svg";
        var entryPath = string.IsNullOrEmpty(folderName)
          ? $"glyphs/{fileName}"
          : $"glyphs/{folderName}/{fileName}";
        entries.Add(new SvgEntry(entryPath, Encoding.UTF8.GetBytes(svg)));
        ++emitted;
      } catch (Exception) {
        ++skippedError;
      }
    }

    return (entries, new Stats(
      fontName, codepointToGlyph.Count, emitted, skippedComposite, skippedNoOutline,
      skippedError, IsCff: false, ParseStatus: null));
  }

  /// <summary>Sanitises a font's family name into a folder-safe segment.</summary>
  public static string SanitiseFolderSegment(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name) {
      if (char.IsLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
      else if (char.IsWhiteSpace(ch)) sb.Append('_');
      // drop other punctuation
    }
    var s = sb.ToString();
    return s.Length == 0 ? "Font" : s;
  }

  // The 'glyf' table info is fetched twice (once for offsets, once for outlines). To
  // avoid duplicating dictionary lookups in the hot path, we resolve them via
  // dedicated helpers that share the parsed SfntTableDir.
  private static uint glyfInfoOffset(SfntTableDir dir) => dir.Tables["glyf"].Offset;
  private static uint glyfInfoLength(SfntTableDir dir) => dir.Tables["glyf"].Length;

  private static string FormatCodepoint(int cp) {
    var hex = $"U+{cp:X4}";
    if (cp >= 0x20 && cp <= 0x7E && cp != '/' && cp != '\\' && cp != '?' && cp != '*')
      return $"{hex} {(char)cp}";
    return hex;
  }
}
