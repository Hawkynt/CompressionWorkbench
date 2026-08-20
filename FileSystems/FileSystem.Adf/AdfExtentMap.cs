#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.Adf;

/// <summary>
/// Walks an Amiga ADF image (901,120 bytes, 1760 × 512-byte sectors) and
/// yields the actual on-disk byte layout — root block + bitmap + boot
/// blocks as metadata, every file's header / extension / data blocks as
/// contiguous-run extents (per-file), and unallocated sectors as Free.
/// Supports both OFS and FFS layouts.
/// </summary>
public static class AdfExtentMap {

  private const int SectorSize = 512;
  private const int RootSector = 880;
  private const int TotalSectors = 1760;
  private const int DiskSize = TotalSectors * SectorSize;

  private const uint TypeHeader = 2;
  private const uint SecTypeRoot = 1;
  private const uint SecTypeDir = 2;
  private const int SecTypeFile = unchecked((int)0xFFFFFFFD);

  private const int HashTableOffset = 24;
  private const int HashTableCount = 72;
  private const int FirstDataOffset = 16;
  private const int DataBlockPtrsTop = 308;
  private const int HashChainOffset = 496;
  private const int ExtBlockOffset = 496;
  private const int NameOffset = 432;
  private const int SecTypeWordOff = 508;
  private const int BitmapPagesOffset = 318; // root: 25 bitmap-block pointers

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < DiskSize) yield break;

    var owned = new bool[TotalSectors];
    var isFfs = (data[3] & 1) != 0;

    // Boot blocks (sectors 0-1).
    yield return new DefragBlockInfo(0, 2L * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: $"Amiga ADF boot ({(isFfs ? "FFS" : "OFS")})");
    owned[0] = true;
    owned[1] = true;

    // Root block (sector 880).
    yield return new DefragBlockInfo((long)RootSector * SectorSize, SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "Amiga ADF root block");
    owned[RootSector] = true;

    // Bitmap block pointers in root: 25 pointers at offset 318 onwards.
    var rootBase = RootSector * SectorSize;
    for (var i = 0; i < 25; i++) {
      var bmp = ReadUInt32BE(data, rootBase + BitmapPagesOffset + i * 4);
      if (bmp == 0 || bmp >= TotalSectors) continue;
      yield return new DefragBlockInfo((long)bmp * SectorSize, SectorSize,
        DefragBlockKind.MetadataReserved, FileName: "Amiga ADF bitmap");
      owned[(int)bmp] = true;
    }

    // Walk directory tree to collect files (and gather subdirs as metadata).
    var fileHeads = new List<(string name, int header, int size)>();
    var dirHeads = new List<(string name, int header)>();
    WalkDir(data, RootSector, "", fileHeads, dirHeads, isRoot: true, new HashSet<int>());

    foreach (var (name, header) in dirHeads) {
      if (header < 0 || header >= TotalSectors || owned[header]) continue;
      owned[header] = true;
      yield return new DefragBlockInfo((long)header * SectorSize, SectorSize,
        DefragBlockKind.Used, FileName: $"Amiga ADF directory: {name}",
        Classification: DefragBlockClass.Directory);
    }

    foreach (var (name, header, size) in fileHeads) {
      if (header < 0 || header >= TotalSectors) continue;

      var blocks = new List<int> { header };
      var dataBlocks = new List<int>();

      var headerBase = header * SectorSize;
      AppendDataBlockPtrs(data, headerBase, dataBlocks);
      var ext = ReadUInt32BE(data, headerBase + ExtBlockOffset);
      var seenExt = new HashSet<int>();
      while (ext != 0 && ext < TotalSectors && seenExt.Add((int)ext)) {
        blocks.Add((int)ext); // extension block is metadata for the file
        var extBase = (int)ext * SectorSize;
        AppendDataBlockPtrs(data, extBase, dataBlocks);
        ext = ReadUInt32BE(data, extBase + ExtBlockOffset);
      }

      // For OFS, also chain from file header's first-data pointer through the
      // 24-byte data-block headers to enumerate data blocks (they may differ
      // from the AppendDataBlockPtrs list which is intended for FFS).
      if (!isFfs) {
        var nextData = ReadUInt32BE(data, headerBase + FirstDataOffset);
        var visited = new HashSet<int>();
        // Stop at a block another file already owns. A block belongs to exactly
        // one file, so reaching one that is spoken for means the chain has left
        // this file's — a stale "next" pointer in a data block header walks
        // straight into the neighbouring file, and the map then reported the
        // same blocks under both names. Two files appeared to share space on a
        // volume where both read back correctly, which is the signature of a
        // map that is wrong rather than a volume that is.
        while (nextData != 0 && nextData < TotalSectors
               && !owned[nextData] && visited.Add((int)nextData)) {
          dataBlocks.Add((int)nextData);
          var dBase = (int)nextData * SectorSize;
          nextData = ReadUInt32BE(data, dBase + 16);
        }
      }

      blocks.AddRange(dataBlocks);
      // Distinct + sort.
      blocks.Sort();
      var distinct = new List<int>();
      var prev = -1;
      foreach (var b in blocks) {
        if (b == prev || b < 0 || b >= TotalSectors) continue;
        if (owned[b]) continue;          // already another file's
        distinct.Add(b);
        prev = b;
      }

      // Coalesce.
      var runStart = -1;
      var runEnd = -1;
      foreach (var b in distinct) {
        owned[b] = true;
        if (runStart < 0) { runStart = b; runEnd = b; }
        else if (b == runEnd + 1) runEnd = b;
        else {
          yield return new DefragBlockInfo((long)runStart * SectorSize,
            (long)(runEnd - runStart + 1) * SectorSize, DefragBlockKind.Used, name);
          runStart = b; runEnd = b;
        }
      }
      if (runStart >= 0)
        yield return new DefragBlockInfo((long)runStart * SectorSize,
          (long)(runEnd - runStart + 1) * SectorSize, DefragBlockKind.Used, name);
    }

    // Free runs.
    {
      var freeStart = -1;
      for (var s = 0; s < TotalSectors; s++) {
        if (!owned[s]) {
          if (freeStart < 0) freeStart = s;
        } else if (freeStart >= 0) {
          yield return new DefragBlockInfo((long)freeStart * SectorSize,
            (long)(s - freeStart) * SectorSize, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0)
        yield return new DefragBlockInfo((long)freeStart * SectorSize,
          (long)(TotalSectors - freeStart) * SectorSize, DefragBlockKind.Free);
    }
  }

  private static void WalkDir(byte[] data, int dirBlock, string parentPath,
      List<(string, int, int)> files, List<(string, int)> dirs, bool isRoot, HashSet<int> seen) {
    if (dirBlock < 0 || dirBlock >= TotalSectors) return;
    if (!seen.Add(dirBlock)) return;
    var dirBase = dirBlock * SectorSize;
    var type = ReadUInt32BE(data, dirBase + 0);
    if (type != TypeHeader) return;
    if (isRoot) {
      var st = ReadUInt32BE(data, dirBase + SecTypeWordOff);
      if (st != SecTypeRoot) return;
    }
    for (var i = 0; i < HashTableCount; i++) {
      var firstBlock = ReadUInt32BE(data, dirBase + HashTableOffset + i * 4);
      if (firstBlock == 0) continue;
      var block = (int)firstBlock;
      var chainSeen = new HashSet<int>();
      while (block != 0 && block < TotalSectors && chainSeen.Add(block)) {
        var secBase = block * SectorSize;
        var secType = (int)ReadUInt32BE(data, secBase + SecTypeWordOff);
        var nameLen = data[secBase + NameOffset];
        if (nameLen > 30) nameLen = 30;
        var name = Encoding.ASCII.GetString(data, secBase + NameOffset + 1, nameLen);
        var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;
        if (secType == (int)SecTypeDir) {
          dirs.Add((fullPath, block));
          WalkDir(data, block, fullPath, files, dirs, isRoot: false, seen);
        } else if (secType == SecTypeFile) {
          var size = (int)ReadUInt32BE(data, secBase + 324);
          files.Add((fullPath, block, size));
        }
        block = (int)ReadUInt32BE(data, secBase + HashChainOffset);
      }
    }
  }

  private static void AppendDataBlockPtrs(byte[] data, int sectorBase, List<int> dataBlocks) {
    for (var i = 0; i < HashTableCount; i++) {
      var p = ReadUInt32BE(data, sectorBase + DataBlockPtrsTop - i * 4);
      if (p != 0 && p < TotalSectors) dataBlocks.Add((int)p);
    }
  }

  private static uint ReadUInt32BE(byte[] data, int offset)
    => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
