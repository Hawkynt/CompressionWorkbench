#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.FontCollection;

/// <summary>
/// Reads the SFNT 'name' table to extract human-readable font naming strings.
/// Used to derive friendly folder names like <c>0_Roboto</c> for per-font glyph folders.
/// </summary>
internal static class NameTableReader {
  /// <summary>
  /// Returns the font's family name (nameID 1) preferring Windows/Unicode entries,
  /// falling back through Mac/Roman, then the full font name (nameID 4), then null
  /// if the table is absent/unparseable.
  /// </summary>
  public static string? ReadFamilyName(ReadOnlySpan<byte> nameTable) {
    if (nameTable.Length < 6) return null;
    var format = BinaryPrimitives.ReadUInt16BigEndian(nameTable);
    if (format != 0 && format != 1) return null;
    var count = BinaryPrimitives.ReadUInt16BigEndian(nameTable[2..]);
    var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(nameTable[4..]);
    var recordsStart = 6;
    if (recordsStart + 12 * count > nameTable.Length) return null;

    // Preference order: (platformID, encodingID, languageID, nameID).
    // 3,1,*,1 = Windows Unicode BMP, Family
    // 0,*,*,1 = Unicode, Family
    // 1,0,0,1 = Macintosh Roman English, Family
    // …then nameID=4 (Full font name) as fallback.
    string? best = null;
    var bestRank = int.MaxValue;
    for (var i = 0; i < count; ++i) {
      var rec = nameTable[(recordsStart + 12 * i)..];
      var platformId = BinaryPrimitives.ReadUInt16BigEndian(rec);
      var encodingId = BinaryPrimitives.ReadUInt16BigEndian(rec[2..]);
      var languageId = BinaryPrimitives.ReadUInt16BigEndian(rec[4..]);
      var nameId = BinaryPrimitives.ReadUInt16BigEndian(rec[6..]);
      var length = BinaryPrimitives.ReadUInt16BigEndian(rec[8..]);
      var offset = BinaryPrimitives.ReadUInt16BigEndian(rec[10..]);
      if (nameId != 1 && nameId != 4) continue;

      var rank = RankCandidate(platformId, encodingId, languageId, nameId);
      if (rank >= bestRank) continue;

      var stringPos = stringOffset + offset;
      if (stringPos + length > nameTable.Length || length == 0) continue;
      var raw = nameTable.Slice(stringPos, length);
      var decoded = DecodeName(platformId, encodingId, raw);
      if (string.IsNullOrWhiteSpace(decoded)) continue;
      best = decoded;
      bestRank = rank;
    }
    return best;
  }

  // Lower rank = more preferred. Windows Unicode + nameID 1 is the canonical pick.
  private static int RankCandidate(ushort platformId, ushort encodingId, ushort languageId, ushort nameId) {
    var nameBonus = nameId == 1 ? 0 : 100;             // family beats full-name
    var langBonus = (platformId == 3 && languageId == 0x0409) ||
                    (platformId == 1 && languageId == 0) ? 0 : 5;  // English preferred
    var platformBonus = (platformId, encodingId) switch {
      (3, 1) => 0,   // Windows Unicode BMP
      (3, 10) => 1,  // Windows Unicode full
      (0, _) => 2,   // Unicode platform (any encoding)
      (1, 0) => 3,   // Macintosh Roman
      _ => 50,
    };
    return nameBonus + platformBonus + langBonus;
  }

  private static string DecodeName(ushort platformId, ushort encodingId, ReadOnlySpan<byte> raw) {
    // Windows + Unicode platform: UTF-16 big-endian.
    if (platformId == 3 || platformId == 0)
      return Encoding.BigEndianUnicode.GetString(raw);
    if (platformId == 1 && encodingId == 0)
      return Encoding.Latin1.GetString(raw); // close enough to MacRoman for ASCII names
    return Encoding.UTF8.GetString(raw);
  }
}
