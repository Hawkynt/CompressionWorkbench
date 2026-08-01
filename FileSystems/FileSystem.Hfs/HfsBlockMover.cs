#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Hfs;

/// <summary>
/// In-place HFS classic block mover. Moves allocation-block-aligned extents and
/// patches the catalog file record's first extent + volume bitmap so the file
/// remains reachable at its new location.
/// </summary>
public sealed class HfsBlockMover : IFilesystemBlockMover {
  private const int MdbOffset = 1024;
  private const ushort HfsMagic = 0x4244;
  private const byte RecFile = 2;

  public long FirstDataByte => 0;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < MdbOffset + 162) return;

    var mdb = data.AsSpan(MdbOffset);
    if (BinaryPrimitives.ReadUInt16BigEndian(mdb) != HfsMagic) return;

    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(mdb.Slice(20));
    if (blockSize == 0) blockSize = 512;
    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(mdb.Slice(28));
    var allocBase = drAlBlSt * 512;
    var drVBMSt = BinaryPrimitives.ReadUInt16BigEndian(mdb.Slice(14));
    var bitmapBase = drVBMSt * 512;

    var oldBlock = (ushort)((oldOffset - allocBase) / blockSize);
    var newBlock = (ushort)((newOffset - allocBase) / blockSize);
    var blockCount = (int)((length + blockSize - 1) / blockSize);

    // 1. Patch volume bitmap: clear old, set new.
    for (var i = 0; i < blockCount; i++) {
      ClearBitmapBit(data, bitmapBase, (uint)(oldBlock + i));
      SetBitmapBit(data, bitmapBase, (uint)(newBlock + i));
    }

    // 2. Walk catalog leaf to patch the file record's first extent.
    var ctStart = BinaryPrimitives.ReadUInt16BigEndian(mdb.Slice(150));
    var ctBlockCount = BinaryPrimitives.ReadUInt16BigEndian(mdb.Slice(152));
    if (ctBlockCount == 0) return;

    var catalogOff = allocBase + (long)ctStart * blockSize;
    if (catalogOff + 32 > data.Length) return;

    // Find first leaf via header node.
    var headerKind = (sbyte)data[catalogOff + 8];
    if (headerKind != 1) return;
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)catalogOff + 14 + 10));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)catalogOff + 14 + 18));
    if (nodeSize == 0) nodeSize = 512;

    var node = (int)firstLeaf;
    var visited = new HashSet<int>();
    while (node != 0 && visited.Add(node)) {
      var nodeOffset = (int)(catalogOff + (long)node * nodeSize);
      if (nodeOffset + nodeSize > data.Length) break;
      var nodeKind = (sbyte)data[nodeOffset + 8];
      if (nodeKind != -1) break;

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(nodeOffset + 10));
      for (var r = 0; r < numRecords; r++) {
        var recOffPos = nodeOffset + nodeSize - 2 * (r + 1);
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recOffPos));
        var recPos = nodeOffset + recOffset;
        if (recPos + 8 > data.Length) continue;

        var keyLen = data[recPos];
        if (keyLen < 6) continue;
        var nameLen = data[recPos + 6];
        if (recPos + 7 + nameLen > data.Length) continue;
        var name = nameLen > 0 ? Encoding.Latin1.GetString(data, recPos + 7, nameLen) : "";

        var dataPos = recPos + 1 + keyLen;
        if ((dataPos & 1) != 0) dataPos++;
        if (dataPos + 78 > data.Length) continue;

        if (data[dataPos] == RecFile && !string.IsNullOrEmpty(name)) {
          var extStart = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(dataPos + 74));
          if (extStart == oldBlock &&
              (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("*", StringComparison.Ordinal))) {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(dataPos + 74), newBlock);
            // Also patch drCTExtRec's inline record if applicable
            // (single-extent: the first extent record at offset 24 in the file record)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(dataPos + 24), newBlock);
          }
        }
      }
      node = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(nodeOffset));
    }

    // 3. Mirror alternate MDB.
    var totalSectors = data.Length / 512;
    if (totalSectors >= 4) {
      var altOff = (totalSectors - 2) * 512;
      if (altOff + 512 <= data.Length)
        data.AsSpan(MdbOffset, 512).CopyTo(data.AsSpan(altOff, 512));
    }

    image.Position = 0;
    image.Write(data, 0, data.Length);
  }

  private static void SetBitmapBit(byte[] data, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block >> 3);
    var bitIdx = 7 - (int)(block & 7);
    if (byteIdx < data.Length) data[byteIdx] |= (byte)(1 << bitIdx);
  }

  private static void ClearBitmapBit(byte[] data, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block >> 3);
    var bitIdx = 7 - (int)(block & 7);
    if (byteIdx < data.Length) data[byteIdx] &= (byte)~(1 << bitIdx);
  }
}
