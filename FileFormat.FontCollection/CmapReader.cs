#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.FontCollection;

/// <summary>
/// Reads a 'cmap' table and returns the inverse map glyphIndex → first Unicode
/// codepoint that references it. Used only for naming extracted glyph entries;
/// the first-reachable codepoint suffices.
/// </summary>
internal static class CmapReader {
  public static Dictionary<int, int> BuildGlyphToCodepoint(ReadOnlySpan<byte> cmap) {
    var result = new Dictionary<int, int>();
    if (cmap.Length < 4) return result;
    var numSubtables = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);

    int? chosenOffset = null;
    // Preference order: Unicode BMP (3,1) → Unicode (0,3) → any other Unicode.
    (ushort Platform, ushort Encoding)[] preferred = [(3, 10), (3, 1), (0, 4), (0, 3)];
    foreach (var pick in preferred) {
      for (var i = 0; i < numSubtables; ++i) {
        var recPos = 4 + 8 * i;
        if (recPos + 8 > cmap.Length) break;
        var pid = BinaryPrimitives.ReadUInt16BigEndian(cmap[recPos..]);
        var eid = BinaryPrimitives.ReadUInt16BigEndian(cmap[(recPos + 2)..]);
        if (pid != pick.Platform || eid != pick.Encoding) continue;
        chosenOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cmap[(recPos + 4)..]);
        break;
      }
      if (chosenOffset != null) break;
    }
    if (chosenOffset == null) return result;

    var sub = cmap[chosenOffset.Value..];
    if (sub.Length < 2) return result;
    var format = BinaryPrimitives.ReadUInt16BigEndian(sub);
    switch (format) {
      case 4: ReadFormat4(sub, result); break;
      case 6: ReadFormat6(sub, result); break;
      case 12: ReadFormat12(sub, result); break;
    }
    return result;
  }

  private static void ReadFormat4(ReadOnlySpan<byte> s, Dictionary<int, int> result) {
    if (s.Length < 14) return;
    var segCount = BinaryPrimitives.ReadUInt16BigEndian(s[6..]) / 2;
    var endCodesPos = 14;
    var startCodesPos = endCodesPos + 2 * segCount + 2;
    var idDeltaPos = startCodesPos + 2 * segCount;
    var idRangeOffsetPos = idDeltaPos + 2 * segCount;
    if (idRangeOffsetPos + 2 * segCount > s.Length) return;

    for (var i = 0; i < segCount; ++i) {
      var endCode = BinaryPrimitives.ReadUInt16BigEndian(s[(endCodesPos + 2 * i)..]);
      var startCode = BinaryPrimitives.ReadUInt16BigEndian(s[(startCodesPos + 2 * i)..]);
      var idDelta = BinaryPrimitives.ReadInt16BigEndian(s[(idDeltaPos + 2 * i)..]);
      var idRangeOffset = BinaryPrimitives.ReadUInt16BigEndian(s[(idRangeOffsetPos + 2 * i)..]);
      if (startCode == 0xFFFF) continue;

      for (var cp = startCode; cp <= endCode; ++cp) {
        int gid;
        if (idRangeOffset == 0) {
          gid = (ushort)(cp + idDelta);
        } else {
          var glyphIdArrayPos = idRangeOffsetPos + 2 * i + idRangeOffset + 2 * (cp - startCode);
          if (glyphIdArrayPos + 2 > s.Length) continue;
          var raw = BinaryPrimitives.ReadUInt16BigEndian(s[glyphIdArrayPos..]);
          if (raw == 0) continue;
          gid = (ushort)(raw + idDelta);
        }
        result.TryAdd(gid, cp);
      }
    }
  }

  private static void ReadFormat6(ReadOnlySpan<byte> s, Dictionary<int, int> result) {
    if (s.Length < 10) return;
    var firstCode = BinaryPrimitives.ReadUInt16BigEndian(s[6..]);
    var entryCount = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
    for (var i = 0; i < entryCount; ++i) {
      var pos = 10 + 2 * i;
      if (pos + 2 > s.Length) break;
      int gid = BinaryPrimitives.ReadUInt16BigEndian(s[pos..]);
      if (gid != 0) result.TryAdd(gid, firstCode + i);
    }
  }

  private static void ReadFormat12(ReadOnlySpan<byte> s, Dictionary<int, int> result) {
    if (s.Length < 16) return;
    var numGroups = BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
    for (var g = 0; g < numGroups; ++g) {
      var pos = 16 + 12 * g;
      if (pos + 12 > s.Length) break;
      var startChar = BinaryPrimitives.ReadUInt32BigEndian(s[pos..]);
      var endChar = BinaryPrimitives.ReadUInt32BigEndian(s[(pos + 4)..]);
      var startGlyph = BinaryPrimitives.ReadUInt32BigEndian(s[(pos + 8)..]);
      for (var cp = startChar; cp <= endChar; ++cp) {
        var gid = (int)(startGlyph + (cp - startChar));
        result.TryAdd(gid, (int)cp);
      }
    }
  }
}
