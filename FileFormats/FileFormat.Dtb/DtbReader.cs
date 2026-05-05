#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dtb;

/// <summary>
/// Reader for the Flattened Device Tree Blob (FDT/DTB) format used by the Linux
/// kernel and U-Boot to describe hardware. Walks the structure block and yields
/// every leaf property as a <see cref="Property"/> with its slash-delimited node
/// path, property name, and raw bytes.
/// </summary>
/// <remarks>
/// Implementation follows the Devicetree Specification v0.4
/// (libfdt's <c>fdt.h</c> layout + tokens). Big-endian u32 throughout. Properties
/// on a node appear before any child nodes in a well-formed blob, but this reader
/// tolerates arbitrary interleaving.
/// </remarks>
public sealed class DtbReader {

  /// <summary>FDT magic <c>0xD00DFEED</c> (BE u32 at offset 0).</summary>
  public const uint Magic = 0xD00DFEEDu;

  /// <summary>Structure-block tokens.</summary>
  public const uint FDT_BEGIN_NODE = 0x1;
  public const uint FDT_END_NODE = 0x2;
  public const uint FDT_PROP = 0x3;
  public const uint FDT_NOP = 0x4;
  public const uint FDT_END = 0x9;

  /// <summary>Parsed FDT header (v17 fields; older versions leave trailing fields at 0).</summary>
  public sealed record Header(
    uint Magic,
    uint TotalSize,
    uint OffsetDtStruct,
    uint OffsetDtStrings,
    uint OffsetMemRsvmap,
    uint Version,
    uint LastCompVersion,
    uint BootCpuidPhys,
    uint SizeDtStrings,
    uint SizeDtStruct
  );

  /// <summary>A reserved memory range declared in the header's memory-reservation map.</summary>
  public sealed record Reservation(ulong Address, ulong Size);

  /// <summary>A leaf property in the device tree.</summary>
  /// <param name="NodePath">Slash-delimited path, e.g. <c>/chosen</c>. Root is <c>""</c>.</param>
  /// <param name="Name">Property name (e.g. <c>compatible</c>).</param>
  /// <param name="Data">Raw property bytes (BE-ordered cells, NUL-separated strings, etc.).</param>
  public sealed record Property(string NodePath, string Name, byte[] Data);

  /// <summary>Parsed FDT blob.</summary>
  public sealed record Fdt(
    Header Header,
    IReadOnlyList<Reservation> Reservations,
    IReadOnlyList<Property> Properties
  );

  /// <summary>Parses a full DTB byte span into a <see cref="Fdt"/> record.</summary>
  public static Fdt Read(ReadOnlySpan<byte> data) {
    if (data.Length < 40)
      throw new InvalidDataException("DTB: file shorter than 40-byte FDT header.");

    var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
    if (magic != Magic)
      throw new InvalidDataException($"DTB: bad magic 0x{magic:X8} (expected 0x{Magic:X8}).");

    var h = new Header(
      Magic: magic,
      TotalSize: BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
      OffsetDtStruct: BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
      OffsetDtStrings: BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
      OffsetMemRsvmap: BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
      Version: BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
      LastCompVersion: BinaryPrimitives.ReadUInt32BigEndian(data[24..]),
      BootCpuidPhys: BinaryPrimitives.ReadUInt32BigEndian(data[28..]),
      SizeDtStrings: BinaryPrimitives.ReadUInt32BigEndian(data[32..]),
      SizeDtStruct: BinaryPrimitives.ReadUInt32BigEndian(data[36..])
    );

    if (h.TotalSize > data.Length)
      throw new InvalidDataException($"DTB: header totalsize {h.TotalSize} exceeds file length {data.Length}.");
    if (h.OffsetDtStruct + h.SizeDtStruct > data.Length)
      throw new InvalidDataException("DTB: structure block extends past file end.");
    if (h.OffsetDtStrings + h.SizeDtStrings > data.Length)
      throw new InvalidDataException("DTB: strings block extends past file end.");

    var reservations = ReadReservations(data, (int)h.OffsetMemRsvmap);
    var strings = data.Slice((int)h.OffsetDtStrings,
      h.SizeDtStrings == 0
        ? (int)(h.TotalSize - h.OffsetDtStrings)
        : (int)h.SizeDtStrings);
    var properties = WalkStructure(data, (int)h.OffsetDtStruct, strings);

    return new Fdt(h, reservations, properties);
  }

  private static List<Reservation> ReadReservations(ReadOnlySpan<byte> data, int offset) {
    var list = new List<Reservation>();
    if (offset <= 0) return list;
    var pos = offset;
    while (pos + 16 <= data.Length) {
      var addr = BinaryPrimitives.ReadUInt64BigEndian(data[pos..]);
      var size = BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
      pos += 16;
      if (addr == 0 && size == 0) break;
      list.Add(new Reservation(addr, size));
    }
    return list;
  }

  private static List<Property> WalkStructure(ReadOnlySpan<byte> data, int structOffset, ReadOnlySpan<byte> strings) {
    var result = new List<Property>();
    var path = new List<string>();
    var pos = structOffset;
    while (pos + 4 <= data.Length) {
      var token = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
      pos += 4;
      switch (token) {
        case FDT_BEGIN_NODE: {
          var nameEnd = pos;
          while (nameEnd < data.Length && data[nameEnd] != 0) nameEnd++;
          var nodeName = Encoding.ASCII.GetString(data[pos..nameEnd]);
          path.Add(nodeName);
          pos = AlignUp(nameEnd + 1);
          break;
        }
        case FDT_END_NODE: {
          if (path.Count > 0) path.RemoveAt(path.Count - 1);
          break;
        }
        case FDT_PROP: {
          if (pos + 8 > data.Length) return result;
          var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
          var nameOff = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
          pos += 8;
          if (len < 0 || pos + len > data.Length) return result;
          var propData = data.Slice(pos, len).ToArray();
          pos = AlignUp(pos + len);
          var propName = nameOff >= 0 && nameOff < strings.Length
            ? ReadCString(strings, nameOff)
            : "(invalid)";
          var nodePath = BuildNodePath(path);
          result.Add(new Property(nodePath, propName, propData));
          break;
        }
        case FDT_NOP:
          break;
        case FDT_END:
          return result;
        default:
          // Unknown token — abort to avoid walking garbage.
          return result;
      }
    }
    return result;

    static int AlignUp(int v) => (v + 3) & ~3;
  }

  private static string ReadCString(ReadOnlySpan<byte> strings, int offset) {
    var end = offset;
    while (end < strings.Length && strings[end] != 0) end++;
    return Encoding.ASCII.GetString(strings[offset..end]);
  }

  private static string BuildNodePath(List<string> path) {
    if (path.Count == 0) return "";
    // Root node is encoded as an empty name; skip it so the path starts with '/'
    // and individual nodes are slash-delimited.
    var sb = new StringBuilder();
    foreach (var seg in path)
      if (seg.Length > 0) {
        sb.Append('/');
        sb.Append(seg);
      }
    return sb.Length == 0 ? "/" : sb.ToString();
  }
}
