#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Tux3;

/// <summary>
/// Describes the WORM table one whole record at a time — the name, the length
/// and the bytes that follow them.
/// </summary>
/// <remarks>
/// <para>A file's bytes sit immediately behind the header naming them, at an
/// offset nothing records: the reader finds the next record by adding this
/// one's length to a cursor. So the unit that can move is the record, and it
/// can only move somewhere the walk still reaches — the records must stay in
/// some order with nothing between them.</para>
///
/// <para>Which is how they always are on one of ours, because removing a file
/// writes the container out packed. A pass finds nothing to move; what it is
/// for is a container that arrived from somewhere else.</para>
/// </remarks>
public static class Tux3RecordMap {

  /// <summary>Where the table of records begins.</summary>
  internal const long TableOffset = Tux3Reader.WormTableOffset;

  /// <summary>Bytes of table header before the first record.</summary>
  internal const int HeaderSize = 12;

  /// <summary>First byte a record may occupy.</summary>
  internal const long FirstRecord = TableOffset + HeaderSize;

  /// <summary>The layout a pass plans against: the head, then one run per record.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var data = new ImageAccessor(image, leaveOpen: true);
    var magic = Tux3Reader.WormTableMagic;
    if (data.Length < FirstRecord) yield break;
    if (!data.Read(TableOffset, magic.Length).AsSpan().SequenceEqual(magic)) yield break;

    yield return new DefragBlockInfo(0, FirstRecord, DefragBlockKind.MetadataReserved,
      "TUX3 superblock and table header");

    var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Read(TableOffset + 8, 4));
    var at = FirstRecord;
    for (var i = 0u; i < count && at + 2 <= data.Length; ++i) {
      var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Read(at, 2));
      if (at + 2 + nameLength + 4 > data.Length) yield break;

      var name = Encoding.UTF8.GetString(data.Read(at + 2, nameLength));
      var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Read(at + 2 + nameLength, 4));
      var length = 2L + nameLength + 4 + dataLength;
      if (at + length > data.Length) yield break;

      yield return new DefragBlockInfo(at, length, DefragBlockKind.Used, name);
      at += length;
    }
  }
}
