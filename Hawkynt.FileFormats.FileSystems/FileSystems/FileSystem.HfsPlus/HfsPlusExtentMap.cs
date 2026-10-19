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

  /// <summary>The non-empty extent descriptors of the HFSPlusForkData at <paramref name="forkOffset"/> in the volume header.</summary>
  private static List<(uint StartBlock, uint BlockCount)> VolumeHeaderExtents(byte[] vh, int forkOffset) {
    var extents = new List<(uint, uint)>(8);
    for (var k = 0; k < 8; k++) {
      var count = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(forkOffset + 20 + k * 8, 4));
      if (count == 0) break;
      extents.Add((BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(forkOffset + 16 + k * 8, 4)), count));
    }
    return extents;
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
      // Every one of the eight extents the volume header holds: a special file that grew past
      // its first run continues in the next.
      foreach (var (startBlock, blockCount) in VolumeHeaderExtents(vh, forkOffset)) {
        var at = (long)startBlock * blockSize;
        var span = (long)blockCount * blockSize;
        if (at < 0 || at + span > image.Length) continue;
        yield return new DefragBlockInfo(at, span, DefragBlockKind.MetadataReserved, FileName: forkName);
      }
    }

    // The catalog file's extents (VH offset 272): node n lies n * nodeSize into them.
    var catalogExtents = VolumeHeaderExtents(vh, 272)
      .Where(e => e.StartBlock != 0 && (long)(e.StartBlock + e.BlockCount) * blockSize <= image.Length)
      .ToList();
    if (catalogExtents.Count == 0) yield break;
    // Only the first run carries the name the block mover can repoint (it rewrites extents[0]);
    // the runs after it are pinned under a name of their own.
    for (var k = 0; k < catalogExtents.Count; k++)
      yield return new DefragBlockInfo((long)catalogExtents[k].StartBlock * blockSize, (long)catalogExtents[k].BlockCount * blockSize,
        DefragBlockKind.MetadataReserved, FileName: k == 0 ? "HFS+ catalog file" : "HFS+ catalog file (continued)");
    var catalogOff = (long)catalogExtents[0].StartBlock * blockSize;
    long CatalogOffset(long forkOffset) {
      foreach (var (startBlock, blockCount) in catalogExtents) {
        var bytes = (long)blockCount * blockSize;
        if (forkOffset < bytes) return (long)startBlock * blockSize + forkOffset;
        forkOffset -= bytes;
      }
      return -1;
    }

    // Walk catalog B-tree leaves via fLink — node-by-node through the cache.
    if (catalogOff + 14 + 30 > image.Length) yield break;

    var hdrBytes = cache.Read(catalogOff, 32);
    var headerKind = (sbyte)hdrBytes[8];
    if (headerKind != 1) yield break;

    var btHeader = cache.Read(catalogOff + 14, 30);
    var firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(btHeader.AsSpan(10, 4));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(btHeader.AsSpan(18, 2));
    if (nodeSize == 0) yield break;

    // The journal's two files (TN1150: ".journal_info_block" at the volume header's
    // journalInfoBlock, ".journal" at the offset that block records) are found by where they
    // lie, not by name; their blocks are pinned, since nothing repoints the volume header or the
    // info block if they moved.
    var journalStarts = new HashSet<uint>();
    if ((BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(4, 4)) & (1u << 13)) != 0) {
      var infoBlock = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(12, 4));
      journalStarts.Add(infoBlock);
      var infoAt = (long)infoBlock * blockSize;
      if (infoBlock != 0 && infoAt + 52 <= image.Length) {
        var journalOffset = BinaryPrimitives.ReadUInt64BigEndian(cache.Read(infoAt + 36, 8));
        if (journalOffset % blockSize == 0) journalStarts.Add((uint)(journalOffset / blockSize));
      }
    }

    var dirPaths = new Dictionary<uint, string> { [2] = "" };
    var currentNode = firstLeafNode;
    var visited = new HashSet<uint>();

    while (currentNode != 0 && visited.Add(currentNode)) {
      var nodeOffset = CatalogOffset((long)currentNode * nodeSize);
      if (nodeOffset < 0 || nodeOffset + nodeSize > image.Length) break;

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
          name = HfsPlusName.FromCatalog(Encoding.BigEndianUnicode.GetString(nd, recOffset + 8, nameByteLen));
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
            var journalFile = parentCnid == 2 && journalStarts.Contains(BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(dataOffset + dataForkOffset + 16, 4)));
            var dataKind = journalFile ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;
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
                yield return new DefragBlockInfo(runOff, used, dataKind, fullPath);
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

    // Runs past a fork's eighth extent (extents overflow records: eight descriptors after a
    // 12-byte key) and the forks of large extended attributes (attribute records 0x20, a fork
    // after 8 bytes, and 0x30, eight descriptors after 8 bytes) belong to their files too. They
    // stay reserved where they lie — nothing repoints them — but they are owned, not stray.
    foreach (var record in LeafRecords(cache, image.Length, VolumeHeaderExtents(vh, 192), blockSize)) {
      if (record.Length < 12 + 64) continue;
      var cnid = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4));
      foreach (var extent in Descriptors(record, 12, blockSize, image.Length))
        yield return extent with { FileName = $"HFS+ overflow extent of CNID {cnid}" };
    }
    foreach (var record in LeafRecords(cache, image.Length, VolumeHeaderExtents(vh, 352), blockSize)) {
      if (record.Length < 14) continue;
      var data = 2 + BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(0, 2));
      data += data & 1;
      if (data + 8 + 64 > record.Length) continue;
      var cnid = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4));
      var at = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(data, 4)) switch {
        0x20 when data + 8 + 80 <= record.Length => data + 8 + 16,
        0x30 => data + 8,
        _ => -1,
      };
      if (at < 0) continue;
      foreach (var extent in Descriptors(record, at, blockSize, image.Length))
        yield return extent with { FileName = $"HFS+ attribute fork of CNID {cnid}" };
    }
  }

  /// <summary>The non-empty runs of eight extent descriptors at <paramref name="at"/>.</summary>
  private static IEnumerable<DefragBlockInfo> Descriptors(byte[] record, int at, uint blockSize, long imageLength) {
    for (var k = 0; k < 8; k++) {
      var start = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(at + k * 8, 4));
      var count = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(at + k * 8 + 4, 4));
      if (count == 0) yield break;
      var offset = (long)start * blockSize;
      var length = Math.Min((long)count * blockSize, imageLength - offset);
      if (length > 0) yield return new DefragBlockInfo(offset, length, DefragBlockKind.MetadataReserved);
    }
  }

  /// <summary>Every leaf record of the B-tree file in <paramref name="extents"/>, following the leaf chain.</summary>
  private static IEnumerable<byte[]> LeafRecords(SectorCache cache, long imageLength, List<(uint StartBlock, uint BlockCount)> extents, uint blockSize) {
    long ForkOffset(long offset) {
      foreach (var (startBlock, blockCount) in extents) {
        var bytes = (long)blockCount * blockSize;
        if (offset < bytes) return (long)startBlock * blockSize + offset;
        offset -= bytes;
      }
      return -1;
    }
    var headerAt = ForkOffset(0);
    if (headerAt < 0 || headerAt + 64 > imageLength) yield break;
    var header = cache.Read(headerAt, 64);
    if ((sbyte)header[8] != 1) yield break;
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 10, 4));
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14 + 18, 2));
    if (nodeSize < 512) yield break;
    var visited = new HashSet<uint>();
    for (var node = firstLeaf; node != 0 && visited.Add(node);) {
      var at = ForkOffset((long)node * nodeSize);
      if (at < 0 || at + nodeSize > imageLength) yield break;
      var nd = cache.Read(at, nodeSize);
      if ((sbyte)nd[8] != -1) yield break;
      var count = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(10, 2));
      for (var i = 0; i < count; i++) {
        var start = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(nodeSize - 2 * (i + 1), 2));
        var end = BinaryPrimitives.ReadUInt16BigEndian(nd.AsSpan(nodeSize - 2 * (i + 2), 2));
        if (start < 14 || end <= start || end > nodeSize) continue;
        yield return nd.AsSpan(start, end - start).ToArray();
      }
      node = BinaryPrimitives.ReadUInt32BigEndian(nd.AsSpan(0, 4));
    }
  }
}
