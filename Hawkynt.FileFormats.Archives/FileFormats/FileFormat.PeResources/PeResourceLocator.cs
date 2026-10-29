using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PeResources;

/// <summary>
/// Finds one resource in a PE image without reading any resource data: walks the
/// IMAGE_RESOURCE_DIRECTORY tree (type → name → first language) and returns where the bytes are.
/// </summary>
public static class PeResourceLocator {

  /// <summary>File offset and size of a located resource.</summary>
  public readonly record struct Location(long Offset, int Size);

  /// <summary>Finds a resource by numeric type and string name (e.g. RT_RCDATA "CABINET").</summary>
  public static Location? Find(Stream stream, ushort type, string name) => Find(stream, type, name, 0);

  /// <summary>Finds a resource by numeric type and numeric id.</summary>
  public static Location? Find(Stream stream, ushort type, ushort id) => Find(stream, type, null, id);

  private static Location? Find(Stream stream, ushort type, string? name, ushort id) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      if (!stream.CanSeek || stream.Length < 0x40)
        return null;
      var head = new byte[4096];
      stream.Position = 0;
      var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
      if (head[0] != 'M' || head[1] != 'Z')
        return null;
      var pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
      if (pe < 0 || pe + 24 > n || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pe)) != 0x00004550)
        return null;
      var sections = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 6));
      var optSize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 20));
      var opt = pe + 24;
      var dirs = opt + (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(opt)) == 0x20B ? 112 : 96);
      if (dirs + 24 > n)
        return null;
      var resRva = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(dirs + 16));
      if (resRva == 0)
        return null;
      var table = opt + optSize;

      long ToOffset(uint rva) {
        for (var i = 0; i < sections && table + 40 * i + 40 <= n; ++i) {
          var s = head.AsSpan(table + 40 * i);
          var va = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
          var size = Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
          if (rva >= va && rva < va + size)
            return BinaryPrimitives.ReadUInt32LittleEndian(s[20..]) + (long)(rva - va);
        }
        return -1;
      }

      var root = ToOffset(resRva);
      if (root < 0)
        return null;
      if (Entry(stream, root, root, null, type) is not { IsDirectory: true } t)
        return null;
      if (Entry(stream, root, t.Offset, name, id) is not { IsDirectory: true } nm)
        return null;
      if (Entry(stream, root, nm.Offset, null, null) is not { IsDirectory: false } leaf)
        return null;
      Span<byte> data = stackalloc byte[8];
      stream.Position = leaf.Offset;
      stream.ReadExactly(data);
      var off = ToOffset(BinaryPrimitives.ReadUInt32LittleEndian(data));
      var len = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
      return off < 0 || len < 0 || off + len > stream.Length ? null : new Location(off, len);
    } catch (Exception ex) when (ex is IOException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// Finds the entry matching <paramref name="name"/> (string) or <paramref name="id"/>
  /// (numeric); with neither, returns the first entry.
  /// </summary>
  private static (long Offset, bool IsDirectory)? Entry(Stream stream, long root, long dir, string? name, ushort? id) {
    Span<byte> h = stackalloc byte[16];
    stream.Position = dir;
    if (stream.ReadAtLeast(h, 16, throwOnEndOfStream: false) < 16)
      return null;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
    Span<byte> e = stackalloc byte[8];
    for (var i = 0; i < Math.Min(count, 4096); ++i) {
      stream.Position = dir + 16 + 8 * i;
      if (stream.ReadAtLeast(e, 8, throwOnEndOfStream: false) < 8)
        return null;
      var nameField = BinaryPrimitives.ReadUInt32LittleEndian(e);
      var target = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
      var match = (name, id) switch {
        (null, null) => true,
        (not null, _) => (nameField & 0x80000000) != 0 && NameEquals(stream, root + (nameField & 0x7FFFFFFF), name),
        _ => (nameField & 0x80000000) == 0 && nameField == id,
      };
      if (match)
        return (root + (target & 0x7FFFFFFF), (target & 0x80000000) != 0);
    }
    return null;
  }

  // IMAGE_RESOURCE_DIR_STRING_U: u16 length then UTF-16LE characters; names compare case-insensitively.
  private static bool NameEquals(Stream stream, long at, string name) {
    Span<byte> len = stackalloc byte[2];
    stream.Position = at;
    if (stream.ReadAtLeast(len, 2, throwOnEndOfStream: false) < 2)
      return false;
    var chars = BinaryPrimitives.ReadUInt16LittleEndian(len);
    if (chars != name.Length)
      return false;
    var buf = new byte[chars * 2];
    if (stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) < buf.Length)
      return false;
    return string.Equals(Encoding.Unicode.GetString(buf), name, StringComparison.OrdinalIgnoreCase);
  }
}
