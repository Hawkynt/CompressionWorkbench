#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Tux2;

/// <summary>
/// Describes the container one whole record at a time — the name, the lengths
/// and the bytes that follow them.
/// </summary>
/// <remarks>
/// <para>This is not what the descriptor's own map describes, and the
/// difference is the point. A file's bytes sit immediately behind the header
/// naming them, at an offset nothing records: the reader finds them by adding
/// each record's length to a cursor. So the unit that can move is the record,
/// and it can only move somewhere the walk still reaches — which means the
/// records must stay in order with nothing between them.</para>
///
/// <para>Which is how they always are, because removing a file writes the
/// container out packed. A pass over one of these finds nothing to move; what
/// it is for is an image that arrived from somewhere else.</para>
/// </remarks>
public static class Tux2RecordMap {

  private static readonly byte[] Magic = "TUX2FS\0\0"u8.ToArray();

  /// <summary>Bytes of header before the first record.</summary>
  internal const int HeaderSize = 16;

  /// <summary>The layout a pass plans against: the header, then one run per record.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var data = new ImageAccessor(image, leaveOpen: true);
    if (data.Length < HeaderSize) yield break;
    if (!data.Read(0, Magic.Length).AsSpan().SequenceEqual(Magic)) yield break;

    yield return new DefragBlockInfo(0, HeaderSize, DefragBlockKind.MetadataReserved, "TUX2 header");

    var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Read(12, 4));
    var at = (long)HeaderSize;
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
