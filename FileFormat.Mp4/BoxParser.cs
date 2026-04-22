#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mp4;

/// <summary>
/// ISO Base Media File Format (ISO/IEC 14496-12) box walker. An MP4/MOV/3GP file
/// is a tree of boxes, each with a 32-bit size, 4-char type, optional 64-bit
/// largesize, and a body. Compound boxes (moov, trak, mdia, minf, …) contain
/// child boxes; leaf boxes (mdat, tkhd, hdlr, …) carry payload bytes.
/// </summary>
public sealed class BoxParser {
  public sealed record Box(string Type, long Offset, long Size, long BodyOffset, long BodyLength, List<Box>? Children);

  // Compound-box types whose body is another box list. Leaves aren't in this set.
  private static readonly HashSet<string> Compound = new(StringComparer.Ordinal) {
    "moov", "trak", "mdia", "minf", "dinf", "stbl", "edts", "udta", "moof", "traf",
    "mvex", "meta", "ipro", "sinf", "mfra", "tref",
  };

  public List<Box> Parse(ReadOnlySpan<byte> data) => ParseRange(data, 0, data.Length);

  private List<Box> ParseRange(ReadOnlySpan<byte> data, long start, long end) {
    var list = new List<Box>();
    var pos = start;
    while (pos + 8 <= end) {
      var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data[(int)pos..]);
      var type = Encoding.ASCII.GetString(data.Slice((int)(pos + 4), 4));
      var hdr = 8L;
      if (size == 1) {
        if (pos + 16 > end) break;
        size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(int)(pos + 8)..]);
        hdr = 16;
      } else if (size == 0) {
        size = end - pos; // extends to end of file
      }
      if (size < hdr || pos + size > end) break;

      var bodyOff = pos + hdr;
      var bodyLen = size - hdr;
      List<Box>? children = null;
      if (Compound.Contains(type) && bodyLen > 0)
        children = ParseRange(data, bodyOff, bodyOff + bodyLen);

      list.Add(new Box(type, pos, size, bodyOff, bodyLen, children));
      pos += size;
    }
    return list;
  }

  public static Box? Find(IEnumerable<Box> boxes, string type) {
    foreach (var b in boxes) {
      if (b.Type == type) return b;
      if (b.Children != null) {
        var inner = Find(b.Children, type);
        if (inner != null) return inner;
      }
    }
    return null;
  }

  public static IEnumerable<Box> FindAll(IEnumerable<Box> boxes, string type) {
    foreach (var b in boxes) {
      if (b.Type == type) yield return b;
      if (b.Children != null)
        foreach (var inner in FindAll(b.Children, type))
          yield return inner;
    }
  }
}
