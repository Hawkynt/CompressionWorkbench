#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Mfs;

/// <summary>
/// Walks a Macintosh MFS image (0xD2D7 magic at offset 1024) and yields
/// the actual on-disk byte layout — system area (boot blocks + MDB),
/// file directory area, every file's contiguous data range (per the
/// writer's simplified linear allocation), and the unused tail as Free.
/// MFS uses a packed 12-bit block map but our writer-produced images
/// are linear, so we walk directory entries and emit one extent per file
/// based on (firstBlock, size).
/// </summary>
public static class MfsExtentMap {

  private const ushort MfsMagic = 0xD2D7;
  private const int MdbOffset = 1024;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < MdbOffset + 128) yield break;
    var sig = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset));
    if (sig != MfsMagic) yield break;

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(MdbOffset + 20));
    if (blockSize == 0) blockSize = 1024;
    var firstAllocBlock = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 28));
    var firstBlockOffset = (long)firstAllocBlock * 512;
    if (firstBlockOffset <= 0 || firstBlockOffset > data.Length) firstBlockOffset = data.Length;

    // System area: boot blocks (0..1023) + MDB (1024..1024+? typically 512 bytes) +
    // directory area through firstBlockOffset. Emit as a single MetadataReserved run.
    yield return new DefragBlockInfo(0, firstBlockOffset, DefragBlockKind.MetadataReserved,
      FileName: "MFS system area (boot + MDB + directory)");

    // Walk directory entries — same logic as MfsReader.
    var dirStart = MdbOffset + 128;
    var dirEnd = (int)Math.Min((long)int.MaxValue, firstBlockOffset);
    if (dirEnd > data.Length) dirEnd = data.Length;

    var fileExtents = new List<(string name, long start, long len)>();
    var pos = dirStart;
    while (pos + 40 < dirEnd) {
      var flags = data[pos];
      if (flags == 0) break;
      if ((flags & 0x80) == 0) {
        if (pos + 39 < dirEnd) {
          var nl0 = data[pos + 38];
          var entryLen = 39 + nl0;
          if ((entryLen & 1) != 0) entryLen++;
          pos += Math.Max(entryLen, 2);
          continue;
        }
        break;
      }
      if (pos + 39 > dirEnd) break;
      var dataFirstBlock = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 26));
      var dataSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 28));
      var nameLen = data[pos + 38];
      if (pos + 39 + nameLen > dirEnd) break;
      var name = Encoding.ASCII.GetString(data, pos + 39, nameLen);

      if (dataSize > 0) {
        var fileStart = firstBlockOffset + (long)dataFirstBlock * blockSize;
        var fileLen = (long)dataSize;
        // Round up to block boundary for on-disk footprint.
        var roundedLen = ((fileLen + blockSize - 1) / blockSize) * blockSize;
        if (fileStart >= 0 && fileStart < data.Length) {
          var actualLen = Math.Min(roundedLen, data.Length - fileStart);
          fileExtents.Add((name, fileStart, actualLen));
        }
      }

      var totalLen = 39 + nameLen;
      if ((totalLen & 1) != 0) totalLen++;
      pos += totalLen;
    }

    // Sort by start, emit Used + Free.
    fileExtents.Sort((a, b) => a.start.CompareTo(b.start));
    var cursor = firstBlockOffset;
    foreach (var (name, start, len) in fileExtents) {
      if (start > cursor) {
        yield return new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free);
      }
      yield return new DefragBlockInfo(start, len, DefragBlockKind.Used, name);
      cursor = start + len;
    }
    if (cursor < data.Length)
      yield return new DefragBlockInfo(cursor, data.Length - cursor, DefragBlockKind.Free);
  }
}
