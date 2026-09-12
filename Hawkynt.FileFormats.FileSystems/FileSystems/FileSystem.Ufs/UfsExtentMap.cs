#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ufs;

/// <summary>
/// Walks a UFS1 (FreeBSD/BSD FFS) image and yields the actual on-disk byte
/// layout — superblock + cylinder-group inode table as
/// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file direct-block
/// pointer run (coalesced) as a <see cref="DefragBlockKind.Used"/> extent.
/// Mirrors the single-CG profile <see cref="UfsReader"/> understands. Indirect
/// blocks are not followed (our writer doesn't emit them).
/// <para>
/// Streaming: never loads the whole image. All reads flow through a
/// <see cref="SectorCache"/> so multi-GB UFS images work without OOM.
/// </para>
/// </summary>
public static class UfsExtentMap {

  private const int SuperblockOffset = 8192;
  private const int SuperblockSize = 1376;
  private const int FsMagicOffset = SuperblockSize - 4;
  private const uint Ufs1Magic = 0x00011954;
  private const int InodeSize = 128;
  private const int RootInode = 2;
  private const int MaxDirectBlocks = 12;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < SuperblockOffset + SuperblockSize) yield break;

    // Read only the superblock (1376 bytes at offset 8192).
    var sbBuf = new byte[SuperblockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(sbBuf);

    var sb = sbBuf.AsSpan();
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb[FsMagicOffset..]);
    if (magic != Ufs1Magic) yield break;

    var cblkno = BinaryPrimitives.ReadInt32LittleEndian(sb[12..]);   // fs_old_cblkno (cg header, in frags)
    var iblkno = BinaryPrimitives.ReadInt32LittleEndian(sb[16..]);
    var blockSize = BinaryPrimitives.ReadInt32LittleEndian(sb[48..]);
    var fragSize = BinaryPrimitives.ReadInt32LittleEndian(sb[52..]);
    var csaddr = BinaryPrimitives.ReadInt32LittleEndian(sb[152..]);  // fs_old_csaddr (fs_cs summary, in frags)
    var inodesPerBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[120..]);
    var inodesPerGroup = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[184..]);
    var fpg = BinaryPrimitives.ReadInt32LittleEndian(sb[188..]);

    if (fragSize <= 0) fragSize = 1024;
    if (blockSize <= 0) blockSize = 8192;
    if (fpg <= 0) fpg = 16384;
    if (inodesPerGroup <= 0) inodesPerGroup = 2048;
    if (inodesPerBlock <= 0) inodesPerBlock = blockSize / InodeSize;

    using var cache = new SectorCache(image);

    // Superblock: 1376 bytes at offset 8192 — emit the whole 8 KB region for
    // simplicity (covers SB + alignment).
    yield return new DefragBlockInfo(SuperblockOffset, blockSize,
      DefragBlockKind.MetadataReserved, FileName: "UFS superblock");

    // Cylinder-group header block for CG 0 — holds cg_magic plus the free-frag,
    // inode-used and cluster bitmaps. It sits between the superblock and the
    // inode table and is NOT covered by either; without an explicit metadata
    // extent the generic unused-space wiper would zero the allocation bitmaps
    // and destroy the filesystem.
    if (cblkno > 0) {
      var cgOff = (long)cblkno * fragSize;
      if (cgOff + blockSize <= image.Length)
        yield return new DefragBlockInfo(cgOff, blockSize,
          DefragBlockKind.MetadataReserved, FileName: "UFS cylinder-group header (CG 0)");
    }

    // Inode table for CG 0 (single-CG profile our writer emits).
    var inodeTableOff = (long)iblkno * fragSize;
    var inodeTableBytes = (long)inodesPerGroup * InodeSize;
    if (inodeTableOff > 0 && inodeTableOff + inodeTableBytes <= image.Length) {
      yield return new DefragBlockInfo(inodeTableOff, inodeTableBytes,
        DefragBlockKind.MetadataReserved, FileName: "UFS inode table (CG 0)");
    }

    // fs_cs summary block (first data block, referenced by fs_csaddr). It lives
    // in the data region but is filesystem metadata — reserve it so the wiper
    // never zeros the cylinder-summary counts.
    if (csaddr > 0) {
      var csOff = (long)csaddr * fragSize;
      if (csOff + blockSize <= image.Length)
        yield return new DefragBlockInfo(csOff, blockSize,
          DefragBlockKind.MetadataReserved, FileName: "UFS cylinder-summary block");
    }

    // Root directory data block(s). The root inode (2) is the anchor of the
    // whole tree; its direct-block run is emitted as MetadataReserved rather
    // than a movable Used+Directory extent because the block mover repatches
    // directory pointers by entry name and the root has no name to look up —
    // relocating it would orphan the entire filesystem. Reserving it also keeps
    // the unused-space wiper from zeroing the root's File Identifier table.
    {
      var rootInodeOff = InodeOffset(RootInode, fpg, fragSize, iblkno, inodesPerGroup);
      if (rootInodeOff + InodeSize <= cache.Length) {
        var rootInode = cache.Read(rootInodeOff, InodeSize);
        var rootSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(rootInode.AsSpan(8));
        var remaining = rootSize;
        for (var i = 0; i < MaxDirectBlocks && remaining > 0; i++) {
          var blk = BinaryPrimitives.ReadInt32LittleEndian(rootInode.AsSpan(40 + i * 4));
          if (blk == 0) { remaining -= blockSize; continue; }
          var byteOff = (long)blk * fragSize;
          var byteLen = Math.Min((long)blockSize, remaining);
          if (byteOff + byteLen > cache.Length) byteLen = Math.Max(0, cache.Length - byteOff);
          if (byteLen > 0)
            yield return new DefragBlockInfo(byteOff, byteLen,
              DefragBlockKind.MetadataReserved, FileName: "UFS root directory");
          remaining -= blockSize;
        }
      }
    }

    // Walk root directory and collect (inode, name).
    var files = new List<(int inode, string name, bool isDir, long size)>();
    WalkDir(cache, RootInode, "", files,
      iblkno, fragSize, blockSize, fpg, inodesPerGroup, new HashSet<int>());

    // Emit Used extents for files AND for directory data blocks. Directories
    // are named with a trailing "/" so the defrag planner recognises them as
    // metadata-zone candidates while still allowing them to be moved. Without
    // this, the planner would treat directory blocks as Free space and could
    // overwrite the dir-entry table during a "pack at start" pass.
    var inodeBuf = new byte[InodeSize];
    foreach (var (inode, name, isDir, size) in files) {
      var inodeOff = InodeOffset(inode, fpg, fragSize, iblkno, inodesPerGroup);
      if (inodeOff + InodeSize > cache.Length) continue;
      cache.Read(inodeOff, inodeBuf);
      var emitName = isDir ? name + "/" : name;
      // Directories carry an honest size in UFS inode (unlike FAT); treat
      // them the same as files for the block-pointer walk.

      // Coalesce contiguous direct-block pointers.
      long? runStartByte = null;
      var runByteLen = 0L;
      var remaining = size;
      for (var i = 0; i < MaxDirectBlocks && remaining > 0; i++) {
        var blk = BinaryPrimitives.ReadInt32LittleEndian(inodeBuf.AsSpan(40 + i * 4));
        if (blk == 0) {
          // Hole (or end). Flush.
          if (runStartByte.HasValue) {
            yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
              Classification: isDir ? DefragBlockClass.Directory : null);
            runStartByte = null;
            runByteLen = 0;
          }
          if (remaining > 0 && i < MaxDirectBlocks - 1) {
            remaining -= blockSize;
            continue;
          }
          break;
        }
        var byteOff = (long)blk * fragSize;
        var byteLen = Math.Min((long)blockSize, remaining);
        if (byteOff + byteLen > cache.Length) byteLen = Math.Max(0, cache.Length - byteOff);
        if (byteLen <= 0) {
          remaining -= blockSize;
          continue;
        }

        if (runStartByte == null) {
          runStartByte = byteOff;
          runByteLen = byteLen;
        } else if (byteOff == runStartByte.Value + runByteLen) {
          runByteLen += byteLen;
        } else {
          yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
            Classification: isDir ? DefragBlockClass.Directory : null);
          runStartByte = byteOff;
          runByteLen = byteLen;
        }
        remaining -= blockSize;
      }
      if (runStartByte.HasValue) {
        yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
          Classification: isDir ? DefragBlockClass.Directory : null);
        runStartByte = null;
        runByteLen = 0;
      }

      // Anything past the twelve direct pointers hangs off di_ib[]: a single-,
      // double- and triple-indirect block, each holding fragSize/4 pointers to the
      // level below. Reporting only the direct blocks left the rest looking free,
      // and the wiper then zeroed live file data.
      var pointersPerBlock = blockSize / 4;
      for (var level = 1; level <= 3 && remaining > 0; ++level) {
        var root = BinaryPrimitives.ReadInt32LittleEndian(
          inodeBuf.AsSpan(40 + MaxDirectBlocks * 4 + (level - 1) * 4));
        if (root == 0) continue;

        foreach (var (blk, isPointerBlock) in WalkIndirect(cache, root, level, blockSize, fragSize, pointersPerBlock)) {
          if (remaining <= 0) break;
          var byteOff = (long)blk * fragSize;
          if (isPointerBlock) {
            // The pointer block itself is metadata, not file content.
            if (runStartByte.HasValue) {
              yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
                Classification: isDir ? DefragBlockClass.Directory : null);
              runStartByte = null;
              runByteLen = 0;
            }
            yield return new DefragBlockInfo(byteOff, blockSize, DefragBlockKind.MetadataReserved,
              $"Indirect: {emitName}");
            continue;
          }

          var byteLen = Math.Min((long)blockSize, remaining);
          if (byteOff + byteLen > cache.Length) byteLen = Math.Max(0, cache.Length - byteOff);
          if (byteLen <= 0) { remaining -= blockSize; continue; }

          if (runStartByte == null) {
            runStartByte = byteOff;
            runByteLen = byteLen;
          } else if (byteOff == runStartByte.Value + runByteLen) {
            runByteLen += byteLen;
          } else {
            yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
              Classification: isDir ? DefragBlockClass.Directory : null);
            runStartByte = byteOff;
            runByteLen = byteLen;
          }
          remaining -= blockSize;
        }
      }

      if (runStartByte.HasValue)
        yield return new DefragBlockInfo(runStartByte.Value, runByteLen, DefragBlockKind.Used, emitName,
          Classification: isDir ? DefragBlockClass.Directory : null);
    }
  }

  /// <summary>
  /// Walks an indirect block tree <paramref name="level" /> deep, yielding every
  /// block it touches: the pointer blocks it descends through and, at the bottom,
  /// the data blocks they address.
  /// </summary>
  private static IEnumerable<(int Block, bool IsPointerBlock)> WalkIndirect(
      SectorCache cache, int root, int level, int blockSize, int fragSize, int pointersPerBlock) {
    yield return (root, true);
    var offset = (long)root * fragSize;
    if (offset < 0 || offset + blockSize > cache.Length) yield break;
    var table = cache.Read(offset, blockSize);

    for (var i = 0; i < pointersPerBlock; ++i) {
      var child = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
      if (child == 0) continue;
      if (level <= 1) {
        yield return (child, false);
        continue;
      }
      foreach (var entry in WalkIndirect(cache, child, level - 1, blockSize, fragSize, pointersPerBlock))
        yield return entry;
    }
  }

  private static long InodeOffset(int ino, int fpg, int fragSize, int iblkno, int inodesPerGroup) {
    var cg = ino / inodesPerGroup;
    var idx = ino % inodesPerGroup;
    var cgStart = (long)cg * fpg * fragSize;
    return cgStart + (long)iblkno * fragSize + (long)idx * InodeSize;
  }

  private static void WalkDir(SectorCache cache, int dirInode, string basePath,
      List<(int, string, bool, long)> files,
      int iblkno, int fragSize, int blockSize, int fpg, int inodesPerGroup,
      HashSet<int> seen) {
    if (!seen.Add(dirInode)) return;
    var inodeOff = InodeOffset(dirInode, fpg, fragSize, iblkno, inodesPerGroup);
    if (inodeOff + InodeSize > cache.Length) return;
    var inodeBuf = cache.Read(inodeOff, InodeSize);
    var dirBytes = ReadInodeData(cache, inodeBuf, fragSize, blockSize);
    if (dirBytes == null) return;

    var pos = 0;
    while (pos + 8 <= dirBytes.Length) {
      var dino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos));
      var reclen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 4));
      if (reclen < 8 || pos + reclen > dirBytes.Length) break;
      var namlen = dirBytes[pos + 7];
      if (dino != 0 && namlen > 0 && pos + 8 + namlen <= dirBytes.Length) {
        var name = Encoding.ASCII.GetString(dirBytes, pos + 8, namlen);
        if (name != "." && name != "..") {
          var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
          var childOff = InodeOffset((int)dino, fpg, fragSize, iblkno, inodesPerGroup);
          if (childOff + InodeSize <= cache.Length) {
            var childInode = cache.Read(childOff, InodeSize);
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(childInode.AsSpan());
            var isDir = (mode & 0xF000) == 0x4000;
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(childInode.AsSpan(8));
            files.Add(((int)dino, fullPath, isDir, size));
            if (isDir) WalkDir(cache, (int)dino, fullPath, files,
                iblkno, fragSize, blockSize, fpg, inodesPerGroup, seen);
          }
        }
      }
      pos += reclen;
    }
  }

  private static byte[]? ReadInodeData(SectorCache cache, byte[] inode, int fragSize, int blockSize) {
    if (inode.Length < InodeSize) return null;
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(inode.AsSpan(8));
    if (size <= 0 || size > 64L * 1024 * 1024) return null;
    using var ms = new MemoryStream();
    for (var i = 0; i < MaxDirectBlocks && ms.Length < size; i++) {
      var blk = BinaryPrimitives.ReadInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (blk == 0) continue;
      var off = (long)blk * fragSize;
      var remaining = size - ms.Length;
      var chunk = (int)Math.Min(blockSize, remaining);
      if (off + chunk <= cache.Length) {
        var data = cache.Read(off, chunk);
        ms.Write(data, 0, chunk);
      }
    }
    return ms.ToArray();
  }
}
