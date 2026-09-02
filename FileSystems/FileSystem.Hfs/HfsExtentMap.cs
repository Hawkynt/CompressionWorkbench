#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Hfs;

/// <summary>
/// Walks a classic HFS image and yields the actual on-disk byte layout — the
/// boot blocks (sectors 0-1) + MDB (sector 2) + alternate MDB + volume bitmap
/// + catalog file as <see cref="DefragBlockKind.MetadataReserved"/>, every file
/// record's data-fork extent as <see cref="DefragBlockKind.Used"/>. The reader
/// only walks the first leaf chain via fLink so coverage matches what the
/// reader can extract.
/// </summary>
public static class HfsExtentMap {

  private const ushort HfsMagic = 0x4244;
  private const int MdbOffset = 1024;
  private const byte RecFile = 2;
  private const byte RecFolder = 1;

  /// <summary>Offset of the data fork's extent record inside a file record.</summary>
  private const int DataForkExtents = 74;

  /// <summary>Offset of the resource fork's extent record inside a file record.</summary>
  private const int ResourceForkExtents = 86;

  /// <summary>Descriptors an extent record holds before the overflow file takes over.</summary>
  private const int ExtentsPerRecord = 3;

  /// <summary>Bytes a catalog file record occupies.</summary>
  private const int FileRecordLength = 102;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < MdbOffset + 162) yield break;

    var sig = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset));
    if (sig != HfsMagic) yield break;

    // Boot blocks (1024 B at offset 0) + MDB sector (512 B).
    yield return new DefragBlockInfo(0, MdbOffset,
      DefragBlockKind.MetadataReserved, FileName: "HFS boot blocks");
    yield return new DefragBlockInfo(MdbOffset, 512,
      DefragBlockKind.MetadataReserved, FileName: "HFS MDB");

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(MdbOffset + 20));
    if (blockSize == 0) blockSize = 512;
    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 28));
    var firstBlockOffset = (long)drAlBlSt * 512;

    // Volume bitmap: starts at sector 3 typically (offset 1536), ends before
    // first allocation block. Emit the gap [MDB+512 .. firstBlockOffset) as
    // metadata covering the bitmap.
    var bitmapStart = (long)MdbOffset + 512;
    if (firstBlockOffset > bitmapStart) {
      yield return new DefragBlockInfo(bitmapStart, firstBlockOffset - bitmapStart,
        DefragBlockKind.MetadataReserved, FileName: "HFS volume bitmap");
    }

    // The alternate MDB, in the second-to-last sector. Leaving it out of the
    // map reads it as free space, and an end-packed layout puts a file there.
    var alternateMdb = (data.Length / 512 - 2) * 512;
    if (alternateMdb > MdbOffset && alternateMdb + 512 <= data.Length)
      yield return new DefragBlockInfo(alternateMdb, 512,
        DefragBlockKind.MetadataReserved, FileName: "HFS alternate MDB");

    // The extents overflow file, which the MDB names the same way it names the
    // catalog. It is where a file's fourth and further extents live, so a
    // layout that reads it as free space writes over the map of itself.
    for (var e = 0; e < ExtentsPerRecord; ++e) {
      var start = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 134 + e * 4));
      var blocks = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 136 + e * 4));
      if (blocks == 0) break;

      var at = firstBlockOffset + (long)start * blockSize;
      var span = (long)blocks * blockSize;
      if (at + span > data.Length) break;
      yield return new DefragBlockInfo(at, span,
        DefragBlockKind.MetadataReserved, FileName: "HFS extents overflow file");
    }

    // Catalog file extents. The second and third are as real as the first.
    for (var e = 1; e < ExtentsPerRecord; ++e) {
      var start = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 150 + e * 4));
      var blocks = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 152 + e * 4));
      if (blocks == 0) break;

      var at = firstBlockOffset + (long)start * blockSize;
      var span = (long)blocks * blockSize;
      if (at + span > data.Length) break;
      yield return new DefragBlockInfo(at, span,
        DefragBlockKind.MetadataReserved, FileName: "HFS catalog file");
    }

    var catalogStartBlock = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 150));
    var catalogBlockCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(MdbOffset + 152));
    if (catalogBlockCount == 0) yield break;

    var catalogOff = firstBlockOffset + (long)catalogStartBlock * blockSize;
    var catalogLen = (long)catalogBlockCount * blockSize;
    if (catalogOff + catalogLen > data.Length) yield break;

    yield return new DefragBlockInfo(catalogOff, catalogLen,
      DefragBlockKind.MetadataReserved, FileName: "HFS catalog file");

    // Walk the catalog leaf chain — same logic as HfsReader.
    if (catalogOff + 32 > data.Length) yield break;
    var headerKind = (sbyte)data[catalogOff + 8];
    if (headerKind != 1) yield break;

    var hdr = data.AsSpan((int)(catalogOff + 14));
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[18..]);
    if (nodeSize == 0) nodeSize = 512;

    var node = (int)firstLeaf;
    var visited = new HashSet<int>();
    while (node != 0 && visited.Add(node)) {
      var nodeOffset = catalogOff + (long)node * nodeSize;
      if (nodeOffset + nodeSize > data.Length) break;

      var nodeKind = (sbyte)data[nodeOffset + 8];
      if (nodeKind != -1) break;

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)nodeOffset + 10));
      for (var r = 0; r < numRecords; r++) {
        var recOffsetPos = (int)nodeOffset + nodeSize - 2 * (r + 1);
        if (recOffsetPos < nodeOffset) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOffsetPos));
        var recPos = (int)nodeOffset + recOffset;
        if (recPos + 8 > data.Length) continue;

        var keyLen = data[recPos];
        if (keyLen < 6) continue;
        var nameLen = data[recPos + 6];
        if (recPos + 7 + nameLen > data.Length) continue;
        var name = nameLen > 0 ? Encoding.Latin1.GetString(data, recPos + 7, nameLen) : "";

        var dataPos = recPos + 1 + keyLen;
        if ((dataPos & 1) != 0) dataPos++;
        if (dataPos + 2 > data.Length) continue;

        var recType = data[dataPos];
        if (recType == RecFile && !string.IsNullOrEmpty(name)) {
          if (dataPos + FileRecordLength > data.Length) continue;

          // Both forks, and all three descriptors of each. A file in more than
          // one piece had its second and third pieces read as free space, which
          // is an invitation to write another file over them.
          foreach (var (extentRecord, forkLength) in new[] {
              (DataForkExtents, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataPos + 26))),
              (ResourceForkExtents, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataPos + 36))) }) {
            var remaining = (long)forkLength;
            for (var e = 0; e < ExtentsPerRecord; ++e) {
              var at = dataPos + extentRecord + e * 4;
              var extStart = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
              var extBlocks = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
              if (extBlocks == 0) break;

              var span = (long)extBlocks * blockSize;
              var fileOff = firstBlockOffset + (long)extStart * blockSize;
              var fileLen = remaining > 0 ? Math.Min(remaining, span) : span;
              remaining -= span;
              if (fileOff + fileLen > data.Length) fileLen = Math.Max(0, data.Length - fileOff);
              if (fileLen > 0)
                yield return new DefragBlockInfo(fileOff, fileLen, DefragBlockKind.Used, name);
            }
          }
        }
        // Folder records contribute no data extents — skip.
        _ = RecFolder;
      }

      node = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)nodeOffset));
    }
  }
}
