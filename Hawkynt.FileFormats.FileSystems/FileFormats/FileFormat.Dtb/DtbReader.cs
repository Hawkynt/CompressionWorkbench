#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dtb;

/// <summary>
/// Reader for the Flattened Device Tree Blob (FDT/DTB) format used by the Linux
/// kernel and U-Boot to describe hardware. Walks the structure block and yields
/// the node hierarchy plus every property with its slash-delimited node path,
/// property name, and raw bytes.
/// </summary>
/// <remarks>
/// Implementation follows the Devicetree Specification v0.4 flattened format.
/// Read/write support deliberately requires the v17 header layout also required
/// by libfdt's read/write API. All multi-byte fields are big-endian.
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

  /// <summary>Parsed v17 FDT header.</summary>
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

  /// <summary>A reserved memory range declared by the memory-reservation block.</summary>
  public sealed record Reservation(ulong Address, ulong Size);

  /// <summary>A node in the device-tree hierarchy. Root is <c>"/"</c>.</summary>
  public sealed record Node(string Path);

  /// <summary>A property in the device tree.</summary>
  /// <param name="NodePath">Slash-delimited path, e.g. <c>/chosen</c>. Root is <c>"/"</c>.</param>
  /// <param name="Name">Property name (e.g. <c>compatible</c>).</param>
  /// <param name="Data">Raw property bytes (BE-ordered cells, NUL-separated strings, etc.).</param>
  public sealed record Property(string NodePath, string Name, byte[] Data);

  /// <summary>Parsed FDT blob.</summary>
  /// <param name="ReservationMapEnd">First byte after the mandatory zero/zero reservation terminator.</param>
  public sealed record Fdt(
    Header Header,
    IReadOnlyList<Reservation> Reservations,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Property> Properties,
    int ReservationMapEnd
  );

  /// <summary>Parses a full DTB byte span into a <see cref="Fdt"/> record.</summary>
  public static Fdt Read(ReadOnlySpan<byte> data) {
    const int HeaderSize = 40;
    if (data.Length < HeaderSize)
      throw new InvalidDataException("DTB: file shorter than 40-byte FDT v17 header.");

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

    if (h.Version < 17)
      throw new InvalidDataException($"DTB: version {h.Version} predates the v17 read/write layout.");
    if (h.LastCompVersion > 17)
      throw new InvalidDataException($"DTB: last compatible version {h.LastCompVersion} is newer than supported v17.");
    if (h.TotalSize < HeaderSize)
      throw new InvalidDataException($"DTB: totalsize {h.TotalSize} is smaller than the v17 header.");
    if (h.TotalSize > data.Length)
      throw new InvalidDataException($"DTB: header totalsize {h.TotalSize} exceeds file length {data.Length}.");

    var totalSize = checked((int)h.TotalSize);
    var blob = data[..totalSize];

    if ((h.OffsetMemRsvmap & 7) != 0)
      throw new InvalidDataException("DTB: memory-reservation block is not 8-byte aligned.");
    if ((h.OffsetDtStruct & 3) != 0)
      throw new InvalidDataException("DTB: structure block is not 4-byte aligned.");
    if (h.OffsetMemRsvmap < HeaderSize)
      throw new InvalidDataException("DTB: memory-reservation block overlaps the header.");

    ValidateRange(h.OffsetDtStruct, h.SizeDtStruct, totalSize, "structure block");
    ValidateRange(h.OffsetDtStrings, h.SizeDtStrings, totalSize, "strings block");
    if (h.OffsetMemRsvmap > h.OffsetDtStruct)
      throw new InvalidDataException("DTB: memory-reservation block starts after the structure block.");

    var reservations = ReadReservations(blob, checked((int)h.OffsetMemRsvmap),
      checked((int)h.OffsetDtStruct), out var reservationMapEnd);
    if (reservationMapEnd > h.OffsetDtStruct)
      throw new InvalidDataException("DTB: memory-reservation map overlaps the structure block.");

    var structEnd = (ulong)h.OffsetDtStruct + h.SizeDtStruct;
    if (structEnd > h.OffsetDtStrings)
      throw new InvalidDataException("DTB: structure block overlaps or follows the strings block.");

    var structure = blob.Slice(checked((int)h.OffsetDtStruct), checked((int)h.SizeDtStruct));
    var strings = blob.Slice(checked((int)h.OffsetDtStrings), checked((int)h.SizeDtStrings));
    var (nodes, properties) = WalkStructure(structure, strings);

    return new Fdt(h, reservations, nodes, properties, reservationMapEnd);
  }

  private static void ValidateRange(uint offset, uint size, int totalSize, string name) {
    var end = (ulong)offset + size;
    if (offset > totalSize || end > (ulong)totalSize)
      throw new InvalidDataException($"DTB: {name} extends past totalsize.");
  }

  private static List<Reservation> ReadReservations(ReadOnlySpan<byte> data, int offset, int limit, out int end) {
    if (offset < 0 || offset > limit || limit > data.Length)
      throw new InvalidDataException("DTB: invalid memory-reservation block bounds.");

    var list = new List<Reservation>();
    var pos = offset;
    while (pos + 16 <= limit) {
      var address = BinaryPrimitives.ReadUInt64BigEndian(data[pos..]);
      var size = BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
      pos += 16;
      if (address == 0 && size == 0) {
        end = pos;
        return list;
      }
      list.Add(new Reservation(address, size));
    }

    throw new InvalidDataException("DTB: memory-reservation block has no zero/zero terminator before the structure block.");
  }

  private static (List<Node> Nodes, List<Property> Properties) WalkStructure(
      ReadOnlySpan<byte> structure, ReadOnlySpan<byte> strings) {
    var nodes = new List<Node>();
    var properties = new List<Property>();
    var path = new List<string>();
    var nodePaths = new HashSet<string>(StringComparer.Ordinal);
    var propertyKeys = new HashSet<(string Path, string Name)>();
    var pos = 0;
    var sawRoot = false;

    while (pos + 4 <= structure.Length) {
      var token = BinaryPrimitives.ReadUInt32BigEndian(structure[pos..]);
      pos += 4;
      switch (token) {
        case FDT_BEGIN_NODE: {
          var nodeName = ReadStructureCString(structure, ref pos);
          if (!sawRoot) {
            if (nodeName.Length != 0)
              throw new InvalidDataException("DTB: root node name must be empty.");
            sawRoot = true;
          } else if (path.Count == 0) {
            throw new InvalidDataException("DTB: multiple root nodes are not valid.");
          } else if (nodeName.Length == 0) {
            throw new InvalidDataException("DTB: child node name must not be empty.");
          }

          path.Add(nodeName);
          var nodePath = BuildNodePath(path);
          if (!nodePaths.Add(nodePath))
            throw new InvalidDataException($"DTB: duplicate node path '{nodePath}'.");
          nodes.Add(new Node(nodePath));
          break;
        }

        case FDT_END_NODE:
          if (path.Count == 0)
            throw new InvalidDataException("DTB: FDT_END_NODE without matching FDT_BEGIN_NODE.");
          path.RemoveAt(path.Count - 1);
          break;

        case FDT_PROP: {
          if (path.Count == 0)
            throw new InvalidDataException("DTB: property appears outside a node.");
          if (pos + 8 > structure.Length)
            throw new InvalidDataException("DTB: truncated property header.");

          var length = BinaryPrimitives.ReadUInt32BigEndian(structure[pos..]);
          var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(structure[(pos + 4)..]);
          pos += 8;
          if (length > int.MaxValue || (ulong)pos + length > (ulong)structure.Length)
            throw new InvalidDataException("DTB: property value extends past the structure block.");

          var propertyName = ReadStringsCString(strings, nameOffset);
          var nodePath = BuildNodePath(path);
          if (!propertyKeys.Add((nodePath, propertyName)))
            throw new InvalidDataException($"DTB: duplicate property '{propertyName}' in node '{nodePath}'.");

          var propertyLength = checked((int)length);
          var propertyData = structure.Slice(pos, propertyLength).ToArray();
          pos = AlignUp(pos + propertyLength);
          if (pos > structure.Length)
            throw new InvalidDataException("DTB: property alignment extends past the structure block.");
          properties.Add(new Property(nodePath, propertyName, propertyData));
          break;
        }

        case FDT_NOP:
          break;

        case FDT_END:
          if (!sawRoot)
            throw new InvalidDataException("DTB: structure block has no root node.");
          if (path.Count != 0)
            throw new InvalidDataException("DTB: structure block ended with unclosed nodes.");
          if (pos != structure.Length)
            throw new InvalidDataException("DTB: bytes remain after FDT_END inside size_dt_struct.");
          return (nodes, properties);

        default:
          throw new InvalidDataException($"DTB: unknown structure token 0x{token:X8}.");
      }
    }

    throw new InvalidDataException("DTB: structure block ended before FDT_END.");
  }

  private static string ReadStructureCString(ReadOnlySpan<byte> structure, ref int pos) {
    var end = pos;
    while (end < structure.Length && structure[end] != 0) ++end;
    if (end >= structure.Length)
      throw new InvalidDataException("DTB: unterminated node name in structure block.");
    var value = DecodeAscii(structure[pos..end], "node name");
    pos = AlignUp(end + 1);
    if (pos > structure.Length)
      throw new InvalidDataException("DTB: node-name alignment extends past the structure block.");
    return value;
  }

  private static string ReadStringsCString(ReadOnlySpan<byte> strings, uint offset) {
    if (offset >= strings.Length)
      throw new InvalidDataException($"DTB: property-name offset {offset} lies outside the strings block.");
    var start = checked((int)offset);
    var end = start;
    while (end < strings.Length && strings[end] != 0) ++end;
    if (end >= strings.Length)
      throw new InvalidDataException("DTB: unterminated property name in strings block.");
    return DecodeAscii(strings[start..end], "property name");
  }

  private static string DecodeAscii(ReadOnlySpan<byte> bytes, string what) {
    foreach (var value in bytes)
      if (value > 0x7F)
        throw new InvalidDataException($"DTB: {what} contains a non-ASCII byte.");
    return Encoding.ASCII.GetString(bytes);
  }

  private static string BuildNodePath(List<string> path) {
    if (path.Count == 0) return "";
    var sb = new StringBuilder();
    foreach (var segment in path)
      if (segment.Length > 0) {
        sb.Append('/');
        sb.Append(segment);
      }
    return sb.Length == 0 ? "/" : sb.ToString();
  }

  private static int AlignUp(int value) => checked((value + 3) & ~3);
}
