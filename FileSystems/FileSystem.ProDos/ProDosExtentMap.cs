#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.ProDos;

/// <summary>
/// Walks an Apple ProDOS image (.po / .2mg, 512-byte blocks) and yields
/// the actual on-disk byte layout — boot (blocks 0-1), volume directory
/// chain, volume bitmap, and per-file storage as Used (data blocks +
/// index/master-index blocks attributed to the file). Storage tiers
/// 1 (seedling), 2 (sapling), 3 (tree), and 0xD (subdirectory) are all
/// walked.
/// </summary>
public static class ProDosExtentMap {

  private const int BlockSize = 512;
  private const int VolumeDirStartBlock = 2;
  private const int EntriesPerBlock = 13;
  private const int EntrySize = 39;
  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < 64) yield break;

    var imageStart = (data.Length >= 64 && data.AsSpan(0, 4).SequenceEqual(TwoImgMagic)) ? 64 : 0;
    if (data.Length - imageStart < BlockSize * 7) yield break;
    var totalBlocks = (data.Length - imageStart) / BlockSize;
    var owned = new bool[totalBlocks];

    long BlockOffset(int b) => imageStart + (long)b * BlockSize;

    // Boot blocks 0..1 — always reserved.
    yield return new DefragBlockInfo(BlockOffset(0), 2L * BlockSize,
      DefragBlockKind.MetadataReserved, FileName: "ProDOS boot (blocks 0-1)");
    owned[0] = true;
    if (totalBlocks > 1) owned[1] = true;

    // Volume directory: chain starting at block 2. Walk through next-block
    // pointer at offset 2 of each dir block and mark sequential dir blocks
    // as one metadata extent.
    var bitmapPointer = 0;
    var bitmapBlockCount = 0;
    {
      var visited = new HashSet<int>();
      var block = VolumeDirStartBlock;
      var firstBlock = true;
      var dirRunStart = -1;
      var dirRunEnd = -1;
      while (block != 0 && visited.Add(block)) {
        if (block < 0 || block >= totalBlocks) break;
        var off = (int)BlockOffset(block);
        if (off + BlockSize > data.Length) break;
        owned[block] = true;
        if (dirRunStart < 0) { dirRunStart = block; dirRunEnd = block; }
        else if (block == dirRunEnd + 1) dirRunEnd = block;
        else {
          yield return new DefragBlockInfo(BlockOffset(dirRunStart),
            (long)(dirRunEnd - dirRunStart + 1) * BlockSize,
            DefragBlockKind.MetadataReserved, FileName: "ProDOS volume directory");
          dirRunStart = block;
          dirRunEnd = block;
        }

        var blockSpan = data.AsSpan(off, BlockSize);
        var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(blockSpan.Slice(2, 2));

        if (firstBlock) {
          // Volume Directory Header is the first entry — capture bitmap pointer
          // at 0x26 (matches the writer's offset; see ProDosWriter.WriteVolumeDirectory).
          bitmapPointer = BinaryPrimitives.ReadUInt16LittleEndian(blockSpan.Slice(4 + 0x26, 2));
        }
        firstBlock = false;
        block = nextBlock;
      }
      if (dirRunStart >= 0)
        yield return new DefragBlockInfo(BlockOffset(dirRunStart),
          (long)(dirRunEnd - dirRunStart + 1) * BlockSize,
          DefragBlockKind.MetadataReserved, FileName: "ProDOS volume directory");
    }

    // Volume bitmap blocks — typically block 6 for floppy (1 block per
    // 4096 blocks of disk).
    bitmapBlockCount = (totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
    if (bitmapPointer == 0) bitmapPointer = 6;
    if (bitmapPointer >= 0 && bitmapPointer + bitmapBlockCount <= totalBlocks) {
      yield return new DefragBlockInfo(BlockOffset(bitmapPointer),
        (long)bitmapBlockCount * BlockSize, DefragBlockKind.MetadataReserved,
        FileName: "ProDOS volume bitmap");
      for (var i = 0; i < bitmapBlockCount; i++)
        owned[bitmapPointer + i] = true;
    }

    // Walk the directory tree to gather files (and recurse into subdirs).
    var files = new List<(string path, int storageType, int keyPointer, int eof)>();
    var subdirs = new List<(string path, int firstBlock)>();
    WalkDir(data, imageStart, totalBlocks, VolumeDirStartBlock, "", files, subdirs, new HashSet<int>());

    // Mark subdirectory blocks as Used+Directory so the block visualiser tints
    // them gold rather than treating them as gray volume-level metadata.
    foreach (var (path, firstBlock) in subdirs) {
      var visited = new HashSet<int>();
      var block = firstBlock;
      var runStart = -1;
      var runEnd = -1;
      while (block != 0 && visited.Add(block) && block < totalBlocks) {
        var off = (int)BlockOffset(block);
        if (off + BlockSize > data.Length) break;
        if (!owned[block]) {
          owned[block] = true;
          if (runStart < 0) { runStart = block; runEnd = block; }
          else if (block == runEnd + 1) runEnd = block;
          else {
            yield return new DefragBlockInfo(BlockOffset(runStart),
              (long)(runEnd - runStart + 1) * BlockSize,
              DefragBlockKind.Used, FileName: $"ProDOS subdirectory: {path}",
              Classification: DefragBlockClass.Directory);
            runStart = block; runEnd = block;
          }
        }
        var blockSpan = data.AsSpan(off, BlockSize);
        block = BinaryPrimitives.ReadUInt16LittleEndian(blockSpan.Slice(2, 2));
      }
      if (runStart >= 0)
        yield return new DefragBlockInfo(BlockOffset(runStart),
          (long)(runEnd - runStart + 1) * BlockSize,
          DefragBlockKind.Used, FileName: $"ProDOS subdirectory: {path}",
          Classification: DefragBlockClass.Directory);
    }

    // Per-file extents.
    foreach (var (path, storageType, keyPointer, eof) in files) {
      var blocks = new List<int>();
      switch (storageType) {
        case 1: // seedling
          if (keyPointer > 0 && keyPointer < totalBlocks) blocks.Add(keyPointer);
          break;
        case 2: // sapling — keyPointer is index block; index has 256 ptrs
          if (keyPointer > 0 && keyPointer < totalBlocks) {
            blocks.Add(keyPointer); // index block itself
            CollectIndexBlock(data, imageStart, totalBlocks, keyPointer, blocks);
          }
          break;
        case 3: // tree — keyPointer is master index block
          if (keyPointer > 0 && keyPointer < totalBlocks) {
            blocks.Add(keyPointer); // master index
            var masterOff = (int)BlockOffset(keyPointer);
            if (masterOff + BlockSize <= data.Length) {
              for (var i = 0; i < 256; i++) {
                var idx = data[masterOff + i] | (data[masterOff + 256 + i] << 8);
                if (idx == 0) continue;
                if (idx >= totalBlocks) continue;
                blocks.Add(idx); // index block
                CollectIndexBlock(data, imageStart, totalBlocks, idx, blocks);
              }
            }
          }
          break;
        default:
          continue;
      }

      // Coalesce.
      blocks.Sort();
      var distinct = new List<int>();
      var prev = -1;
      foreach (var b in blocks) {
        if (b == prev) continue;
        if (b < 0 || b >= totalBlocks) continue;
        distinct.Add(b);
        prev = b;
      }
      var runStart = -1;
      var runEnd = -1;
      foreach (var b in distinct) {
        owned[b] = true;
        if (runStart < 0) { runStart = b; runEnd = b; }
        else if (b == runEnd + 1) runEnd = b;
        else {
          yield return new DefragBlockInfo(BlockOffset(runStart),
            (long)(runEnd - runStart + 1) * BlockSize, DefragBlockKind.Used, path);
          runStart = b; runEnd = b;
        }
      }
      if (runStart >= 0)
        yield return new DefragBlockInfo(BlockOffset(runStart),
          (long)(runEnd - runStart + 1) * BlockSize, DefragBlockKind.Used, path);
    }

    // Free runs.
    {
      var freeStart = -1;
      for (var b = 0; b < totalBlocks; b++) {
        if (!owned[b]) {
          if (freeStart < 0) freeStart = b;
        } else if (freeStart >= 0) {
          yield return new DefragBlockInfo(BlockOffset(freeStart),
            (long)(b - freeStart) * BlockSize, DefragBlockKind.Free);
          freeStart = -1;
        }
      }
      if (freeStart >= 0)
        yield return new DefragBlockInfo(BlockOffset(freeStart),
          (long)(totalBlocks - freeStart) * BlockSize, DefragBlockKind.Free);
    }
  }

  private static void CollectIndexBlock(byte[] data, int imageStart, int totalBlocks,
      int indexBlock, List<int> blocks) {
    var off = imageStart + indexBlock * BlockSize;
    if (off + BlockSize > data.Length) return;
    for (var i = 0; i < 256; i++) {
      var b = data[off + i] | (data[off + 256 + i] << 8);
      if (b == 0) continue;
      if (b < 0 || b >= totalBlocks) continue;
      blocks.Add(b);
    }
  }

  private static void WalkDir(byte[] data, int imageStart, int totalBlocks,
      int startBlock, string parentPath,
      List<(string, int, int, int)> files, List<(string, int)> subdirs, HashSet<int> seenDirs) {
    if (!seenDirs.Add(startBlock)) return;
    var visited = new HashSet<int>();
    var block = startBlock;
    var firstBlock = true;
    while (block != 0 && visited.Add(block)) {
      if (block < 0 || block >= totalBlocks) break;
      var off = imageStart + block * BlockSize;
      if (off + BlockSize > data.Length) break;
      var blockSpan = data.AsSpan(off, BlockSize);
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(blockSpan.Slice(2, 2));

      var slotsHere = ProDosReader.SlotsInBlock(firstBlock);
      for (var i = 0; i < slotsHere; i++) {
        var eo = ProDosReader.EntryOffsetInBlock(firstBlock, i);
        if (firstBlock && i == 0) continue; // skip volume / subdir header
        var storageNibble = (blockSpan[eo + 0] >> 4) & 0x0F;
        var nameLen = blockSpan[eo + 0] & 0x0F;
        if (storageNibble == 0 || nameLen == 0) continue;
        var name = Encoding.ASCII.GetString(blockSpan.Slice(eo + 1, nameLen));
        var keyPointer = BinaryPrimitives.ReadUInt16LittleEndian(blockSpan.Slice(eo + 0x11, 2));
        var eof = blockSpan[eo + 0x15] | (blockSpan[eo + 0x16] << 8) | (blockSpan[eo + 0x17] << 16);
        var fullPath = parentPath.Length == 0 ? name : parentPath + "/" + name;
        if (storageNibble == 0xD) {
          subdirs.Add((fullPath, keyPointer));
          WalkDir(data, imageStart, totalBlocks, keyPointer, fullPath, files, subdirs, seenDirs);
        } else if (storageNibble is 1 or 2 or 3) {
          files.Add((fullPath, storageNibble, keyPointer, eof));
        }
      }
      firstBlock = false;
      block = nextBlock;
    }
  }
}
