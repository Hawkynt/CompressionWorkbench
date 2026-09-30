#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.HfsPlus;

/// <summary>
/// Walks an HFS+ (or HFSX) image and yields the actual on-disk byte layout —
/// the reserved boot region (first 1024 bytes) + volume header + allocation
/// file + catalog file as <see cref="DefragBlockKind.MetadataReserved"/>,
/// every file record's first data-fork extent (HFSPlusForkData.extents[0]) as
/// <see cref="DefragBlockKind.Used"/>. Mirrors what <see cref="HfsPlusReader"/>
/// can extract — leaf chain via fLink, single primary extent per file.
/// <para>
/// Streaming: never loads the whole image. All reads flow through a
/// <see cref="SectorCache"/> so multi-TB HFS+ images (the catalog file alone
/// can be tens of MB on large volumes) work without OOM.
/// </para>
/// </summary>
public static class HfsPlusExtentMap {

  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX"

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = EnumerateCore(image).ToList();
    if (extents.Count == 0) return extents;
    AddUnclaimedAllocated(image, extents);
    return extents;
  }

  /// <summary>
  /// Fail closed: every allocation block the allocation file marks in use that no
  /// extent above claims — overflow extents, attribute-file forks, anything a later
  /// HFS+ added — is reported as reserved, so a wipe or a layout never treats it as
  /// free.
  /// </summary>
  private static void AddUnclaimedAllocated(Stream image, List<DefragBlockInfo> extents) {
    var vh = new byte[VolumeHeaderSize];
    image.Position = VolumeHeaderOffset;
    image.ReadExactly(vh);
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(40, 4));
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(44, 4));
    var allocStart = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 16, 4));
    var allocCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 20, 4));
    var bitmapBytes = (totalBlocks + 7) / 8;
    if (blockSize == 0 || allocCount == 0 || (long)allocCount * blockSize < bitmapBytes) return;
    var bitmapOffset = (long)allocStart * blockSize;
    if (bitmapOffset + bitmapBytes > image.Length) return;
    var bitmap = new byte[bitmapBytes];
    image.Position = bitmapOffset;
    image.ReadExactly(bitmap);

    var claimed = extents.Select(e => (Start: e.Offset, End: e.Offset + e.Length)).OrderBy(r => r.Start).ToList();
    var index = 0;
    bool IsClaimed(long offset) {
      while (index < claimed.Count && claimed[index].End <= offset) ++index;
      for (var j = index; j < claimed.Count && claimed[j].Start <= offset; ++j)
        if (claimed[j].End > offset) return true;
      return false;
    }

    var extra = new List<DefragBlockInfo>();
    long runStart = -1;
    for (long block = 0; block <= totalBlocks; block++) {
      var allocated = block < totalBlocks && (bitmap[block >> 3] & (0x80 >> (int)(block & 7))) != 0;  // MSB first
      var offset = block * blockSize;
      if (allocated && !IsClaimed(offset)) {
        if (runStart < 0) runStart = block;
      } else if (runStart >= 0) {
        var start = runStart * blockSize;
        var end = Math.Min(image.Length, offset);
        if (end > start)
          extra.Add(new DefragBlockInfo(start, end - start, DefragBlockKind.MetadataReserved, FileName: "allocated (unattributed)"));
        runStart = -1;
      }
    }
    extents.AddRange(extra);
  }

  private static IEnumerable<DefragBlockInfo> EnumerateCore(Stream image) {
    if (image.Length < VolumeHeaderOffset + VolumeHeaderSize) yield break;

    // Read just the 512-byte volume header via the cache (so subsequent
    // metadata reads can share cached chunks).
    using var cache = new SectorCache(image);
    var vh = cache.Read(VolumeHeaderOffset, VolumeHeaderSize);

    var sig = BinaryPrimitives.ReadUInt16BigEndian(vh.AsSpan(0, 2));
    if (sig != HfsPlusSignature && sig != HfsxSignature) yield break;

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(40, 4));
    if (blockSize == 0) yield break;

    // Boot blocks (reserved 1024 B) + volume header + alternate VH at end.
    yield return new DefragBlockInfo(0, VolumeHeaderOffset,
      DefragBlockKind.MetadataReserved, FileName: "HFS+ reserved boot region");
    yield return new DefragBlockInfo(VolumeHeaderOffset, VolumeHeaderSize,
      DefragBlockKind.MetadataReserved, FileName: "HFS+ volume header");

    // Allocation file ForkData starts at offset 112 (TN1150 §2.6).
    var allocationStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 16, 4));
    var allocationBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(112 + 20, 4));
    if (allocationBlockCount > 0) {
      var allocOff = (long)allocationStartBlock * blockSize;
      var allocLen = (long)allocationBlockCount * blockSize;
      if (allocOff + allocLen <= image.Length) {
        yield return new DefragBlockInfo(allocOff, allocLen,
          DefragBlockKind.MetadataReserved, FileName: "HFS+ allocation file");
      }
    }

    // The alternate volume header lives in the second-to-last sector, and the
    // last sector is reserved with it. Reading them as free space is what lets
    // an end-packed layout write a file over the volume's own spare copy.
    if (image.Length >= VolumeHeaderOffset + 1024)
      yield return new DefragBlockInfo(image.Length - 1024, 1024,
        DefragBlockKind.MetadataReserved, FileName: "HFS+ alternate volume header");

    // The extents overflow file, the attributes file and the startup file. The
    // volume header names them exactly as it names the catalog, and a layout
    // that does not know they are there writes over them.
    foreach (var (forkOffset, forkName) in new[] {
        (192, "HFS+ extents overflow file"),
        (352, "HFS+ attributes file"),
        (432, "HFS+ startup file") }) {
      var startBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(forkOffset + 16, 4));
      var blockCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(forkOffset + 20, 4));
      if (blockCount == 0) continue;

      var at = (long)startBlock * blockSize;
      var span = (long)blockCount * blockSize;
      if (at < 0 || at + span > image.Length) continue;
      yield return new DefragBlockInfo(at, span, DefragBlockKind.MetadataReserved, FileName: forkName);
    }

    // Catalog file extent[0] at VH offset 272+16=288 / 272+20=292.
    var catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(288, 4));
    var catalogBlockCount = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(292, 4));
    if (catalogBlockCount == 0 || catalogStartBlock == 0) yield break;

    var catalogOff = (long)catalogStartBlock * blockSize;
    var catalogLen = (long)catalogBlockCount * blockSize;
    if (catalogOff + catalogLen > image.Length) catalogLen = image.Length - catalogOff;
    if (catalogLen <= 0) yield break;

    yield return new DefragBlockInfo(catalogOff, catalogLen,
      DefragBlockKind.MetadataReserved, FileName: "HFS+ catalog file");

    // Walk catalog B-tree leaves via fLink — node-by-node through the cache.
    if (catalogOff + 14 + 30 > image.Length) yield break;

    var hdrBytes = cache.Read(catalogOff, 32);
    var headerKind = (sbyte)hdrBytes[8];
    if (headerKind != 1) yield break;

    var btHeader = cache.Read(catalogOff + 14, 30);
    var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(btHeader.AsSpan(10, 4));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(btHeader.AsSpan(18, 2));
    if (nodeSize == 0) yield break;

    var dirPaths = new Dictionary<uint, string> { [2] = "" };
    var currentNode = firstLeafNode;
    var visited = new HashSet<uint>();

    while (currentNode != 0 && visited.Add(currentNode)) {
      var nodeOffset = catalogOff + (long)currentNode * nodeSize;
      if (nodeOffset + nodeSize > image.Length) break;

      // Read this leaf via cache — keeps working set bounded to one node.
      var nd = cache.Read(nodeOffset, nodeSize);
      var ndKind = (sbyte)nd[8];
      if (ndKind != -1) break;

      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(10, 2));
      for (var i = 0; i < numRecords; i++) {
        var offsetPos = nodeSize - 2 * (i + 1);
        if (offsetPos < 12) break;
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(offsetPos, 2));
        if (recOffset + 6 > nodeSize) continue;
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset, 2));
        if (keyLength < 6) continue;
        var parentCnid = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(recOffset + 2, 4));
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(recOffset + 6, 2));
        var nameByteLen = nameLength * 2;
        var name = "";
        if (nameLength > 0 && recOffset + 8 + nameByteLen <= nodeSize) {
          name = Encoding.BigEndianUnicode.GetString(nd, recOffset + 8, nameByteLen);
        }
        var dataOffset = recOffset + 2 + keyLength;
        if ((dataOffset & 1) != 0) dataOffset++;
        if (dataOffset + 2 > nodeSize) continue;
        var recordType = BinaryPrimitives.ReadInt16BigEndian(nd.AsSpan(dataOffset, 2));

        switch (recordType) {
          case 1: { // Folder
            if (dataOffset + 12 > nd.Length) break;
            var cnid = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(dataOffset + 8, 4));
            // The volume root folder (parent CNID 1) carries the VOLUME NAME but
            // anchors paths at the empty root — mirror HfsPlusReader so a file's
            // FileName resolves to a bare path ("secret.bin"), not
            // "<volume>/secret.bin". Keeps extent FileName == reader FullPath.
            if (parentCnid == 1) {
              dirPaths[cnid] = "";
              break;
            }
            var parentPath = dirPaths.GetValueOrDefault(parentCnid, "");
            var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;
            dirPaths[cnid] = fullPath;
            break;
          }
          case 2: { // File — every extent of both forks.
            if (dataOffset + 248 > nd.Length) break;
            var parentPath = dirPaths.GetValueOrDefault(parentCnid, "");
            var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

            // Data fork: each of the eight extent descriptors in the record is the
            // file's own (the mover repoints the descriptor that names a moved run);
            // runs past the eighth live in the overflow file and are left to the
            // allocation-bitmap sweep, which pins them.
            const int dataForkOffset = 88;
            var remaining = (long)BinaryPrimitives.ReadUInt64BigEndian(nd.AsSpan(dataOffset + dataForkOffset, 8));
            for (var k = 0; k < 8; k++) {
              var at = dataOffset + dataForkOffset + 16 + k * 8;
              var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(at, 4));
              var blockCount = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(at + 4, 4));
              if (blockCount == 0) break;
              var runOff = (long)startBlock * blockSize;
              var runBytes = (long)blockCount * blockSize;
              var used = Math.Min(Math.Max(remaining, 1), runBytes);
              if (runOff + used > image.Length) used = Math.Max(0, image.Length - runOff);
              if (used > 0 && startBlock != 0)
                yield return new DefragBlockInfo(runOff, used, DefragBlockKind.Used, fullPath);
              remaining -= runBytes;
            }

            // Resource fork: not repointed by the mover, so reserved where it lies.
            const int resourceForkOffset = 168;
            for (var k = 0; k < 8; k++) {
              var at = dataOffset + resourceForkOffset + 16 + k * 8;
              var startBlock = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(at, 4));
              var blockCount = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(at + 4, 4));
              if (blockCount == 0) break;
              var runOff = (long)startBlock * blockSize;
              var runBytes = Math.Min((long)blockCount * blockSize, image.Length - runOff);
              if (runBytes > 0 && startBlock != 0)
                yield return new DefragBlockInfo(runOff, runBytes, DefragBlockKind.MetadataReserved,
                  FileName: fullPath + " (resource fork)");
            }
            break;
          }
        }
      }

      currentNode = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(0, 4));
    }
  }
}
