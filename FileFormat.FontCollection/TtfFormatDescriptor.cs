#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.FontCollection;

/// <summary>
/// Exposes a single-font TrueType .ttf as an archive of per-glyph SVG files.
/// Glyph outlines are decoded from 'glyf' + 'loca'; composite glyphs emit a
/// placeholder SVG that references their component glyph indices.
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
  public string Description => "TrueType font; each glyph outline extractable as SVG.";

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

internal static class GlyphCatalog {
  public sealed record Entry(string Name, string Svg);

  public static List<Entry> Build(byte[] font) {
    var dir = SfntTableDir.Parse(font);
    // OpenType (CFF-based) fonts don't have 'glyf' — we deliberately skip CFF decoding
    // and return the one "FULL.otf" blob so users still see something.
    if (!dir.Tables.TryGetValue("glyf", out var glyfInfo)) {
      return [new Entry("FULL.otf", "<!-- CFF/OpenType — glyph decoding not implemented -->")];
    }
    if (!dir.Tables.TryGetValue("loca", out var locaInfo) ||
        !dir.Tables.TryGetValue("head", out var headInfo) ||
        !dir.Tables.TryGetValue("maxp", out var maxpInfo))
      throw new InvalidDataException("TTF missing required tables head/maxp/loca for glyph extraction.");

    var head = font.AsSpan((int)headInfo.Offset, (int)headInfo.Length);
    var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head[18..]);
    var indexToLocFormat = BinaryPrimitives.ReadInt16BigEndian(head[50..]); // 0=short, 1=long
    var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan((int)maxpInfo.Offset + 4));

    var loca = font.AsSpan((int)locaInfo.Offset, (int)locaInfo.Length);
    var glyfOffsets = new uint[numGlyphs + 1];
    for (var i = 0; i <= numGlyphs; ++i)
      glyfOffsets[i] = indexToLocFormat == 0
        ? (uint)BinaryPrimitives.ReadUInt16BigEndian(loca[(2 * i)..]) * 2u
        : BinaryPrimitives.ReadUInt32BigEndian(loca[(4 * i)..]);

    var codepoints = dir.Tables.TryGetValue("cmap", out var cmapInfo)
      ? CmapReader.BuildGlyphToCodepoint(font.AsSpan((int)cmapInfo.Offset, (int)cmapInfo.Length))
      : [];

    var glyf = font.AsSpan((int)glyfInfo.Offset, (int)glyfInfo.Length);
    var result = new List<Entry>(numGlyphs);
    for (var i = 0; i < numGlyphs; ++i) {
      var start = glyfOffsets[i];
      var end = glyfOffsets[i + 1];
      var glyphBytes = end > start ? glyf.Slice((int)start, (int)(end - start)) : ReadOnlySpan<byte>.Empty;
      var decoded = TrueTypeGlyphDecoder.Decode(glyphBytes);
      string? label = codepoints.TryGetValue(i, out var cp) ? FormatCodepoint(cp) : null;
      var name = BuildName(i, label);
      var svg = GlyphSvgEmitter.Emit(decoded, unitsPerEm, label);
      result.Add(new Entry(name, svg));
    }
    return result;
  }

  private static string BuildName(int gid, string? cpLabel) {
    if (cpLabel == null) return $"glyph_{gid:D5}.svg";
    return $"glyph_{gid:D5}_{cpLabel}.svg";
  }

  private static string FormatCodepoint(int cp) {
    if (cp == 0) return "U+0000";
    var hex = $"U+{cp:X4}";
    if (cp >= 0x20 && cp <= 0x7E && cp != '/' && cp != '\\' && cp != '?' && cp != '*')
      return $"{hex}_{(char)cp}";
    return hex;
  }
}
