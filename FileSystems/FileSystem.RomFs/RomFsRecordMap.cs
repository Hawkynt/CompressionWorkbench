#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.RomFs;

/// <summary>
/// Describes a ROMFS image one whole record at a time — header, name and, for a
/// regular file, the data that follows it.
/// </summary>
/// <remarks>
/// <para>This is not what <see cref="RomFsExtentMap" /> describes, and the
/// difference is the point. A file's bytes sit immediately behind its header,
/// at an offset nothing records: the header's position <em>is</em> the data's
/// position. So a layout pass cannot move a file's data on its own — the only
/// thing that can move is the record as a whole, and the only thing that has to
/// be rewritten is whatever pointed at it.</para>
///
/// <para>The record just past the superblock is where Linux looks for the root
/// inode, so it stays where it is; everything else in the volume is reachable
/// through a pointer that can be rewritten.</para>
/// </remarks>
public static class RomFsRecordMap {

  private static readonly byte[] Magic = "-rom1fs-"u8.ToArray();

  /// <summary>One record of the volume.</summary>
  /// <param name="Offset">Where the record's header starts.</param>
  /// <param name="Length">Header, name and data, padded as the format pads them.</param>
  /// <param name="Type">Low three bits of the header's first word.</param>
  /// <param name="Name">The record's own name, which need not be unique.</param>
  public readonly record struct Record(long Offset, long Length, int Type, string Name);

  /// <summary>Every record of the volume, in the order the chains reach them.</summary>
  public static IReadOnlyList<Record> Records(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var data = new ImageAccessor(image, leaveOpen: true);
    var records = new List<Record>();
    if (data.Length < 16) return records;

    for (var i = 0; i < Magic.Length; ++i)
      if (data.ReadByte(i) != Magic[i]) return records;

    var visited = new HashSet<long>();
    Walk(data, FirstRecord(data), records, visited);
    return records;
  }

  /// <summary>The layout a defragmentation pass plans against.</summary>
  /// <remarks>
  /// The superblock and the record the kernel reads as the root inode are
  /// pinned; each other record is one movable run of its own, named by where it
  /// currently sits so that no two share an owner.
  /// </remarks>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var data = new ImageAccessor(image, leaveOpen: true);
    if (data.Length < 16) yield break;

    for (var i = 0; i < Magic.Length; ++i)
      if (data.ReadByte(i) != Magic[i]) yield break;

    var root = FirstRecord(data);
    yield return new DefragBlockInfo(0, root, DefragBlockKind.MetadataReserved, "superblock");

    foreach (var record in Records(image)) {
      if (record.Offset == root) {
        yield return new DefragBlockInfo(record.Offset, record.Length,
          DefragBlockKind.MetadataReserved, "root directory record");
        continue;
      }

      yield return new DefragBlockInfo(record.Offset, record.Length, DefragBlockKind.Used,
        $"{record.Name}@{record.Offset}");
    }
  }

  /// <summary>Where the first record sits: straight after the superblock.</summary>
  public static long FirstRecord(ImageAccessor data) {
    var end = 16L;
    while (end < data.Length && data.ReadByte(end) != 0) ++end;
    return 16 + Align16((int)(end - 16 + 1));
  }

  private static void Walk(ImageAccessor data, long offset, List<Record> records, HashSet<long> visited) {
    while (offset != 0 && offset + 16 <= data.Length) {
      if (!visited.Add(offset)) return;

      var nextAndType = BinaryPrimitives.ReadUInt32BigEndian(data.Read(offset, 4));
      var spec = BinaryPrimitives.ReadUInt32BigEndian(data.Read(offset + 4, 4));
      var size = BinaryPrimitives.ReadUInt32BigEndian(data.Read(offset + 8, 4));
      var type = (int)(nextAndType & 0x07);
      var next = (long)(nextAndType & 0xFFFFFFF0u);

      var nameEnd = offset + 16;
      while (nameEnd < data.Length && data.ReadByte(nameEnd) != 0) ++nameEnd;
      var name = Encoding.ASCII.GetString(data.Read(offset + 16, (int)(nameEnd - offset - 16)));
      var headerLength = 16 + Align16((int)(nameEnd - offset - 16 + 1));
      var dataLength = type == 2 ? Align16Long(size) : 0;

      records.Add(new Record(offset, headerLength + dataLength, type, name));

      // A directory's spec names its chain; "." names the chain it opens, which
      // is where the walk already is.
      if (type == 1 && spec != 0 && spec < data.Length)
        Walk(data, spec, records, visited);

      offset = next;
    }
  }

  private static int Align16(int length) => (length + 15) & ~15;

  private static long Align16Long(long length) => (length + 15) & ~15;
}
