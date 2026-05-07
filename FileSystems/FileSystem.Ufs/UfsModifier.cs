#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// In-place UFS1 modifier — random-access add/remove against an existing UFS1
/// image. Performs <b>O(touched bytes)</b> I/O: only the superblock fragment,
/// the cylinder-group header (with its inode-used and free-frag bitmaps), the
/// fs_cs summary block, the affected inode slots, the root-directory data
/// block, and the file's own data blocks are read or written.
///
/// <para>Layout reminders (matching <see cref="UfsWriter"/>'s default geometry):
/// <list type="bullet">
///   <item>Block size 8192, fragment 1024, single cylinder group (fs_ncg=1).</item>
///   <item>Superblock at file offset 8192 (magic <c>0x00011954</c> at +1372).</item>
///   <item>CG header at frag 16 (offset 16384) — <c>cg_magic 0x00090255</c> at +4.</item>
///   <item>Inode table at frag 24, root inode is inode 2.</item>
///   <item>Files use direct block pointers only (max 12 blocks = 96 KiB).</item>
/// </list></para>
/// </summary>
public static class UfsModifier {

  // Superblock offsets (mirroring UfsWriter constants).
  private const int SuperblockOffset = UfsWriter.SuperblockOffset;
  private const int SuperblockSize = UfsWriter.SuperblockSize;
  private const int FsMagicOffset = SuperblockSize - 4;
  private const uint Ufs1Magic = (uint)UfsWriter.Ufs1Magic;
  private const int CgMagic = UfsWriter.CgMagic;
  private const int InodeSize = UfsWriter.InodeSize;
  private const int RootIno = UfsWriter.RootIno;
  private const int MaxDirectBlocks = UfsWriter.MaxDirectBlocks;

  // Inode mode bits (POSIX).
  private const ushort InodeModeRegular = 0x8000;
  private const ushort InodeModeDir = 0x4000;
  private const ushort DefaultMode = InodeModeRegular | 0x01A4; // 0644
  private const byte DtReg = 8;

  /// <summary>Cached superblock-derived geometry for a single Add/Remove call.</summary>
  private sealed record class Geometry(
    int BlockSize,
    int FragSize,
    int FragsPerBlock,
    int InodesPerGroup,
    int FragsPerGroup,
    int CgBlockNo,             // fs_cblkno (in frags)
    int InodeTableFragNo,      // fs_iblkno (in frags)
    int FsCsAddr,              // fs_old_csaddr / fs_csaddr (in frags)
    long ImageSize
  );

  /// <summary>
  /// Adds (or fails if a same-name entry exists) a file to a UFS1 image. Touches
  /// only the superblock, CG header, inode slot, root dir block, and the new
  /// data blocks.
  /// </summary>
  /// <exception cref="IOException">No free inode, no free blocks, or the file
  /// requires indirect blocks (>12 direct blocks).</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(name)) throw new ArgumentException("name is empty", nameof(name));

    var geom = ReadGeometry(image);

    // UFS allocates whole blocks for files (a tail fragment is permissible per
    // spec but our writer rounds up to whole-block, so we mirror that).
    var blocksNeeded = data.Length == 0 ? 1 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    if (blocksNeeded > MaxDirectBlocks)
      throw new IOException(
        $"UFS: file '{name}' needs {blocksNeeded} blocks; only direct pointers are supported "
        + $"(max {MaxDirectBlocks * geom.BlockSize} bytes).");

    // Locate root dir's first direct block — that's the dirent table.
    var rootInode = ReadInode(image, geom, RootIno);
    var rootDirFrag = BinaryPrimitives.ReadInt32LittleEndian(rootInode.AsSpan(40, 4));
    if (rootDirFrag == 0)
      throw new IOException("UFS: root directory has no data block.");

    // Read root dir block, ensure name is unique, find slot for new entry.
    var dirBlock = ReadBlock(image, geom, rootDirFrag);
    if (FindEntry(dirBlock, name, out _, out _, out _))
      throw new IOException($"UFS: entry '{name}' already exists; remove it first to replace.");

    var newEntrySize = ComputeDirEntrySize(name);
    if (!TrySplitLastEntryForAppend(dirBlock, newEntrySize, out var insertOffset, out var blockEnd))
      throw new IOException(
        $"UFS: root directory block has no room for entry '{name}' "
        + $"(only single-block root directories are supported).");

    // Read CG header — it carries the bitmaps and free counts.
    var cgBlock = ReadBlock(image, geom, geom.CgBlockNo);
    var (iusedOff, freeOff, clusterOff) = GetCgBitmapOffsets(cgBlock);

    // Mark all inodes that are referenced from the root dir as used (the writer
    // doesn't always set their bitmap bits — it sets only the first N bits).
    // Also mark all blocks they own as used in the free-frag bitmap so we don't
    // re-allocate occupied space.
    SyncBitmapsFromDirectory(image, geom, dirBlock, cgBlock, iusedOff, freeOff, clusterOff);

    // Allocate inode and (whole) blocks.
    var newInode = AllocateInode(cgBlock, iusedOff, geom)
      ?? throw new IOException("UFS: no free inodes available.");
    var allocatedBlocks = new List<int>(blocksNeeded);
    for (var i = 0; i < blocksNeeded; ++i) {
      var blk = AllocateBlock(cgBlock, freeOff, clusterOff, geom);
      if (blk == null) {
        // Roll back: free the blocks we already grabbed + the inode.
        foreach (var rb in allocatedBlocks) FreeBlock(cgBlock, freeOff, clusterOff, rb, geom);
        FreeInodeBit(cgBlock, iusedOff, newInode);
        throw new IOException("UFS: not enough free blocks for file.");
      }
      allocatedBlocks.Add(blk.Value);
    }

    // Write file data blocks. Each block is BlockSize; last block may be partial
    // — we still write the full block to keep on-disk geometry uniform (the
    // unused tail bytes are zeroed by the buffer allocation).
    var written = 0;
    foreach (var b in allocatedBlocks) {
      var toWrite = Math.Min(geom.BlockSize, data.Length - written);
      var buf = new byte[geom.BlockSize];
      if (toWrite > 0) Array.Copy(data, written, buf, 0, toWrite);
      WriteBlock(image, geom, b, buf);
      written += toWrite;
    }

    // Build the new inode (ufs1_dinode, 128 bytes).
    var inode = BuildFileInode((uint)data.Length, allocatedBlocks, geom);
    WriteInode(image, geom, newInode, inode);

    // Splice new dirent into root dir block at insertOffset, rec_len covers
    // remaining slack (UFS convention — last entry's reclen extends to end).
    WriteDirEntry(dirBlock, insertOffset, newInode, name, DtReg, blockEnd - insertOffset);
    WriteBlock(image, geom, rootDirFrag, dirBlock);

    // Persist the CG header (bitmaps + cg_cs).
    UpdateCgFreeCounts(cgBlock, freeBlocksDelta: -allocatedBlocks.Count, freeInodesDelta: -1);
    WriteBlock(image, geom, geom.CgBlockNo, cgBlock);

    // Persist superblock fs_cstotal counts (and the fs_cs summary block).
    AdjustSuperblockFreeCounts(image, geom, freeBlocksDelta: -allocatedBlocks.Count, freeInodesDelta: -1);
    AdjustFsCsSummary(image, geom, freeBlocksDelta: -allocatedBlocks.Count, freeInodesDelta: -1);
  }

  /// <summary>
  /// Removes the named entry from an existing UFS1 image. Returns false if no
  /// entry with that name exists in the root directory. When
  /// <paramref name="wipeData"/> is true the freed data blocks are zeroed so no
  /// forensic trace remains.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var geom = ReadGeometry(image);

    var rootInode = ReadInode(image, geom, RootIno);
    var rootDirFrag = BinaryPrimitives.ReadInt32LittleEndian(rootInode.AsSpan(40, 4));
    if (rootDirFrag == 0) return false;

    var dirBlock = ReadBlock(image, geom, rootDirFrag);
    if (!FindEntry(dirBlock, name, out var entryOffset, out var prevOffset, out var inodeNum))
      return false;

    var inodeBytes = ReadInode(image, geom, inodeNum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes.AsSpan(0, 2));
    if ((mode & 0xF000) == InodeModeDir) return false; // refuse to remove directories.

    var cgBlock = ReadBlock(image, geom, geom.CgBlockNo);
    var (iusedOff, freeOff, clusterOff) = GetCgBitmapOffsets(cgBlock);

    // Walk direct block pointers, free + (optionally) wipe each.
    var freedBlocks = 0;
    for (var i = 0; i < MaxDirectBlocks; ++i) {
      var ptr = BinaryPrimitives.ReadInt32LittleEndian(inodeBytes.AsSpan(40 + i * 4, 4));
      if (ptr == 0) continue;
      if (ptr < 0 || ptr >= geom.FragsPerGroup) continue; // sanity (single-cg)
      FreeBlock(cgBlock, freeOff, clusterOff, ptr, geom);
      if (wipeData) WriteBlock(image, geom, ptr, new byte[geom.BlockSize]);
      ++freedBlocks;
    }

    // Refuse to follow indirect/double/triple pointers — writer never emits
    // them. Clear the slots defensively.
    for (var i = 0; i < MaxDirectBlocks; ++i)
      BinaryPrimitives.WriteInt32LittleEndian(inodeBytes.AsSpan(40 + i * 4, 4), 0);

    // Free the inode in the inode-used bitmap, zero its slot in the table.
    FreeInodeBit(cgBlock, iusedOff, inodeNum);
    WriteInode(image, geom, inodeNum, new byte[InodeSize]);

    // Splice the dirent out: extend prev entry's reclen to absorb this one
    // (or, if first, zero its inode field — readers skip ino==0 entries).
    SpliceOutDirEntry(dirBlock, entryOffset, prevOffset);
    WriteBlock(image, geom, rootDirFrag, dirBlock);

    // Persist CG header + fs_cs/fs_cstotal counts.
    UpdateCgFreeCounts(cgBlock, freeBlocksDelta: freedBlocks, freeInodesDelta: 1);
    WriteBlock(image, geom, geom.CgBlockNo, cgBlock);
    AdjustSuperblockFreeCounts(image, geom, freeBlocksDelta: freedBlocks, freeInodesDelta: 1);
    AdjustFsCsSummary(image, geom, freeBlocksDelta: freedBlocks, freeInodesDelta: 1);
    return true;
  }

  // ── Geometry / superblock IO ──────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    if (image.Length < SuperblockOffset + SuperblockSize)
      throw new InvalidDataException("UFS: image too small to contain a UFS1 superblock.");

    var sb = new byte[SuperblockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(FsMagicOffset, 4));
    if (magic != Ufs1Magic)
      throw new InvalidDataException($"UFS: invalid superblock magic 0x{magic:X8}, expected 0x{Ufs1Magic:X8}.");

    var iblkno = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(16, 4));
    var cblkno = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(12, 4));
    var blockSize = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(48, 4));
    var fragSize = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(52, 4));
    var frag = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(56, 4));
    var ipg = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(184, 4));
    var fpg = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(188, 4));
    var csaddr = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(152, 4));

    if (blockSize <= 0 || fragSize <= 0 || frag <= 0 || ipg <= 0 || fpg <= 0)
      throw new InvalidDataException("UFS: superblock has invalid geometry.");

    return new Geometry(
      BlockSize: blockSize,
      FragSize: fragSize,
      FragsPerBlock: frag,
      InodesPerGroup: ipg,
      FragsPerGroup: fpg,
      CgBlockNo: cblkno,
      InodeTableFragNo: iblkno,
      FsCsAddr: csaddr,
      ImageSize: image.Length);
  }

  /// <summary>Updates fs_old_cstotal (offset 192, 4×i32) and fs_cstotal (offset 1008, 8×i64).</summary>
  private static void AdjustSuperblockFreeCounts(Stream image, Geometry geom, int freeBlocksDelta, int freeInodesDelta) {
    if (freeBlocksDelta == 0 && freeInodesDelta == 0) return;

    // Read+write the superblock's two cstotal regions only.
    var oldCs = new byte[16];
    image.Position = SuperblockOffset + 192;
    image.ReadExactly(oldCs);
    var oldNbfree = BinaryPrimitives.ReadInt32LittleEndian(oldCs.AsSpan(4, 4)) + freeBlocksDelta;
    var oldNifree = BinaryPrimitives.ReadInt32LittleEndian(oldCs.AsSpan(8, 4)) + freeInodesDelta;
    if (oldNbfree < 0) oldNbfree = 0;
    if (oldNifree < 0) oldNifree = 0;
    BinaryPrimitives.WriteInt32LittleEndian(oldCs.AsSpan(4, 4), oldNbfree);
    BinaryPrimitives.WriteInt32LittleEndian(oldCs.AsSpan(8, 4), oldNifree);
    image.Position = SuperblockOffset + 192;
    image.Write(oldCs);

    var cs = new byte[24];
    image.Position = SuperblockOffset + 1008;
    image.ReadExactly(cs);
    var nbfree = BinaryPrimitives.ReadInt64LittleEndian(cs.AsSpan(8, 8)) + freeBlocksDelta;
    var nifree = BinaryPrimitives.ReadInt64LittleEndian(cs.AsSpan(16, 8)) + freeInodesDelta;
    if (nbfree < 0) nbfree = 0;
    if (nifree < 0) nifree = 0;
    BinaryPrimitives.WriteInt64LittleEndian(cs.AsSpan(8, 8), nbfree);
    BinaryPrimitives.WriteInt64LittleEndian(cs.AsSpan(16, 8), nifree);
    image.Position = SuperblockOffset + 1008;
    image.Write(cs);
  }

  /// <summary>Updates the fs_cs summary block (cs_ndir/cs_nbfree/cs_nifree/cs_nffree) at fs_csaddr.</summary>
  private static void AdjustFsCsSummary(Stream image, Geometry geom, int freeBlocksDelta, int freeInodesDelta) {
    if (freeBlocksDelta == 0 && freeInodesDelta == 0) return;
    if (geom.FsCsAddr <= 0) return;

    var csOffset = (long)geom.FsCsAddr * geom.FragSize;
    if (csOffset + 16 > geom.ImageSize) return;

    var cs = new byte[16];
    image.Position = csOffset;
    image.ReadExactly(cs);
    var nbfree = BinaryPrimitives.ReadInt32LittleEndian(cs.AsSpan(4, 4)) + freeBlocksDelta;
    var nifree = BinaryPrimitives.ReadInt32LittleEndian(cs.AsSpan(8, 4)) + freeInodesDelta;
    if (nbfree < 0) nbfree = 0;
    if (nifree < 0) nifree = 0;
    BinaryPrimitives.WriteInt32LittleEndian(cs.AsSpan(4, 4), nbfree);
    BinaryPrimitives.WriteInt32LittleEndian(cs.AsSpan(8, 4), nifree);
    image.Position = csOffset;
    image.Write(cs);
  }

  // ── CG header / bitmap helpers ────────────────────────────────────────────

  private static (int iusedOff, int freeOff, int clusterOff) GetCgBitmapOffsets(byte[] cgBlock) {
    var magic = BinaryPrimitives.ReadInt32LittleEndian(cgBlock.AsSpan(4, 4));
    if (magic != CgMagic)
      throw new InvalidDataException($"UFS: invalid cg_magic 0x{magic:X8}, expected 0x{CgMagic:X8}.");
    var iusedOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cgBlock.AsSpan(92, 4));
    var freeOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cgBlock.AsSpan(96, 4));
    var clusterOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(cgBlock.AsSpan(108, 4));
    return (iusedOff, freeOff, clusterOff);
  }

  /// <summary>Updates cg_cs (offset 24): nbfree (+4), nifree (+8). Frag-free (nffree, +12) is left at zero.</summary>
  private static void UpdateCgFreeCounts(byte[] cgBlock, int freeBlocksDelta, int freeInodesDelta) {
    if (freeBlocksDelta == 0 && freeInodesDelta == 0) return;
    var nbfree = BinaryPrimitives.ReadInt32LittleEndian(cgBlock.AsSpan(28, 4)) + freeBlocksDelta;
    var nifree = BinaryPrimitives.ReadInt32LittleEndian(cgBlock.AsSpan(32, 4)) + freeInodesDelta;
    if (nbfree < 0) nbfree = 0;
    if (nifree < 0) nifree = 0;
    BinaryPrimitives.WriteInt32LittleEndian(cgBlock.AsSpan(28, 4), nbfree);
    BinaryPrimitives.WriteInt32LittleEndian(cgBlock.AsSpan(32, 4), nifree);
  }

  /// <summary>
  /// Allocates the first free user inode (inode &gt;= RootIno+1) by scanning
  /// the cg_iused bitmap. Bit N tracks inode N.
  /// </summary>
  private static int? AllocateInode(byte[] cgBlock, int iusedOff, Geometry geom) {
    // Inodes 0..RootIno are reserved (writer marks them used). Start at RootIno+1.
    for (var ino = RootIno + 1; ino < geom.InodesPerGroup; ++ino) {
      var byteIdx = iusedOff + ino / 8;
      if (byteIdx >= cgBlock.Length) return null;
      var bitMask = (byte)(1 << (ino % 8));
      if ((cgBlock[byteIdx] & bitMask) != 0) continue;
      cgBlock[byteIdx] |= bitMask;
      return ino;
    }
    return null;
  }

  /// <summary>
  /// Walks the root directory's dirent stream and marks every referenced
  /// inode as used in the cg_iused bitmap, plus every block owned by those
  /// inodes as used in the free-frag bitmap. Called before allocation to
  /// compensate for writer-bitmap loose bookkeeping (the writer marks only
  /// the first N inode bits, not the actual file inode numbers).
  /// </summary>
  private static void SyncBitmapsFromDirectory(
    Stream image, Geometry geom, byte[] dirBlock,
    byte[] cgBlock, int iusedOff, int freeOff, int clusterOff
  ) {
    var off = 0;
    while (off + 8 <= dirBlock.Length) {
      var ino = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBlock.AsSpan(off, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBlock.AsSpan(off + 4, 2));
      if (recLen < 8 || off + recLen > dirBlock.Length) break;
      if (ino > 0 && ino < geom.InodesPerGroup) {
        // Mark inode bit used.
        var byteIdx = iusedOff + ino / 8;
        if (byteIdx < cgBlock.Length)
          cgBlock[byteIdx] |= (byte)(1 << (ino % 8));

        // Mark its data blocks used too.
        if (ino != RootIno) {
          var inodeBytes = ReadInode(image, geom, ino);
          for (var i = 0; i < MaxDirectBlocks; ++i) {
            var ptr = BinaryPrimitives.ReadInt32LittleEndian(inodeBytes.AsSpan(40 + i * 4, 4));
            if (ptr <= 0 || ptr >= geom.FragsPerGroup) continue;
            // Mark the whole block (FragsPerBlock frags) starting at ptr as used.
            var blkBase = ptr - (ptr % geom.FragsPerBlock);
            for (var f = 0; f < geom.FragsPerBlock; ++f) {
              var bit = blkBase + f;
              var fByteIdx = freeOff + bit / 8;
              if (fByteIdx < cgBlock.Length) cgBlock[fByteIdx] &= (byte)~(1 << (bit % 8));
            }
            var cBit = blkBase / geom.FragsPerBlock;
            var cByteIdx = clusterOff + cBit / 8;
            if (cByteIdx >= 0 && cByteIdx < cgBlock.Length)
              cgBlock[cByteIdx] &= (byte)~(1 << (cBit % 8));
          }
        }
      }
      off += recLen;
    }
  }

  /// <summary>Frees an inode bit in the cg_iused bitmap.</summary>
  private static void FreeInodeBit(byte[] cgBlock, int iusedOff, int ino) {
    var byteIdx = iusedOff + ino / 8;
    if (byteIdx < cgBlock.Length)
      cgBlock[byteIdx] &= (byte)~(1 << (ino % 8));
  }

  /// <summary>
  /// Allocates a contiguous, block-aligned run of <c>FragsPerBlock</c> frags by
  /// scanning the free-frag bitmap (1 = free). Returns the first frag of the
  /// allocated block, or null if no aligned block is free.
  /// </summary>
  private static int? AllocateBlock(byte[] cgBlock, int freeOff, int clusterOff, Geometry geom) {
    var fragsPerBlock = geom.FragsPerBlock;
    for (var blkBase = 0; blkBase + fragsPerBlock <= geom.FragsPerGroup; blkBase += fragsPerBlock) {
      var allFree = true;
      for (var f = 0; f < fragsPerBlock; ++f) {
        var bit = blkBase + f;
        var byteIdx = freeOff + bit / 8;
        if (byteIdx >= cgBlock.Length || (cgBlock[byteIdx] & (1 << (bit % 8))) == 0) {
          allFree = false;
          break;
        }
      }
      if (!allFree) continue;

      // Mark all frags in the block as used (clear bits).
      for (var f = 0; f < fragsPerBlock; ++f) {
        var bit = blkBase + f;
        cgBlock[freeOff + bit / 8] &= (byte)~(1 << (bit % 8));
      }
      // Clear the cluster bit too (cluster bitmap tracks whole free blocks; 1 = free here).
      var cBit = blkBase / fragsPerBlock;
      var cByteIdx = clusterOff + cBit / 8;
      if (cByteIdx >= 0 && cByteIdx < cgBlock.Length)
        cgBlock[cByteIdx] &= (byte)~(1 << (cBit % 8));
      return blkBase;
    }
    return null;
  }

  /// <summary>Frees a previously-allocated whole block (frags marked free; cluster bit set).</summary>
  private static void FreeBlock(byte[] cgBlock, int freeOff, int clusterOff, int blockFrag, Geometry geom) {
    var fragsPerBlock = geom.FragsPerBlock;
    var blkBase = blockFrag - (blockFrag % fragsPerBlock); // realign defensively
    for (var f = 0; f < fragsPerBlock; ++f) {
      var bit = blkBase + f;
      var byteIdx = freeOff + bit / 8;
      if (byteIdx < cgBlock.Length) cgBlock[byteIdx] |= (byte)(1 << (bit % 8));
    }
    var cBit = blkBase / fragsPerBlock;
    var cByteIdx = clusterOff + cBit / 8;
    if (cByteIdx >= 0 && cByteIdx < cgBlock.Length)
      cgBlock[cByteIdx] |= (byte)(1 << (cBit % 8));
  }

  // ── Block / inode IO ──────────────────────────────────────────────────────

  /// <summary>Reads a whole filesystem block (BlockSize bytes) at the given starting fragment.</summary>
  private static byte[] ReadBlock(Stream image, Geometry geom, int blockFrag) {
    var buf = new byte[geom.BlockSize];
    image.Position = (long)blockFrag * geom.FragSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBlock(Stream image, Geometry geom, int blockFrag, ReadOnlySpan<byte> data) {
    if (data.Length != geom.BlockSize)
      throw new ArgumentException("block payload size mismatch", nameof(data));
    image.Position = (long)blockFrag * geom.FragSize;
    image.Write(data);
  }

  private static byte[] ReadInode(Stream image, Geometry geom, int inodeNum) {
    if (inodeNum <= 0) throw new ArgumentOutOfRangeException(nameof(inodeNum));
    var buf = new byte[InodeSize];
    image.Position = (long)geom.InodeTableFragNo * geom.FragSize + (long)inodeNum * InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, Geometry geom, int inodeNum, ReadOnlySpan<byte> data) {
    if (data.Length != InodeSize)
      throw new ArgumentException("inode size mismatch", nameof(data));
    image.Position = (long)geom.InodeTableFragNo * geom.FragSize + (long)inodeNum * InodeSize;
    image.Write(data);
  }

  private static byte[] BuildFileInode(uint size, IReadOnlyList<int> directBlocks, Geometry geom) {
    var inode = new byte[InodeSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0, 2), DefaultMode);
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(2, 2), 1);              // di_nlink
    BinaryPrimitives.WriteUInt64LittleEndian(inode.AsSpan(8, 8), size);            // di_size
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(16, 4), now);            // di_atime
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(24, 4), now);            // di_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(32, 4), now);            // di_ctime
    for (var i = 0; i < directBlocks.Count && i < MaxDirectBlocks; ++i)
      BinaryPrimitives.WriteInt32LittleEndian(inode.AsSpan(40 + i * 4, 4), directBlocks[i]);
    var blocksUsed512 = (uint)(directBlocks.Count * geom.BlockSize / 512);
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(104, 4), blocksUsed512); // di_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(108, 4), 1);             // di_gen
    return inode;
  }

  // ── Directory helpers ─────────────────────────────────────────────────────

  /// <summary>
  /// UFS dirent: <c>d_ino(4) | d_reclen(2) | d_type(1) | d_namlen(1) | name[] | pad</c>.
  /// (Mirrors UfsWriter.WriteDirEntry layout — type at +6, namlen at +7.)
  /// </summary>
  private static bool FindEntry(byte[] dirData, string name, out int entryOffset, out int prevOffset, out int inodeNum) {
    entryOffset = -1; prevOffset = -1; inodeNum = 0;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var off = 0;
    var prev = -1;
    while (off + 8 <= dirData.Length) {
      var ino = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(off, 4));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      if (recLen < 8 || off + recLen > dirData.Length) return false;
      var namLen = dirData[off + 7];
      if (ino != 0 && namLen == nameBytes.Length &&
          off + 8 + namLen <= dirData.Length &&
          dirData.AsSpan(off + 8, namLen).SequenceEqual(nameBytes)) {
        entryOffset = off;
        prevOffset = prev;
        inodeNum = ino;
        return true;
      }
      prev = off;
      off += recLen;
    }
    return false;
  }

  private static int ComputeDirEntrySize(string name) {
    var nameBytes = Encoding.ASCII.GetByteCount(name);
    return (8 + nameBytes + 3) & ~3;
  }

  /// <summary>
  /// UFS convention: the last in-use dirent's reclen extends to the end of the
  /// dir block. Shrink that reclen to its minimum so the trailing slack becomes
  /// the new entry's space. Returns the offset to write the new entry and the
  /// block-end position so the caller can size the new entry's reclen.
  /// </summary>
  private static bool TrySplitLastEntryForAppend(byte[] dirData, int newEntrySize, out int appendOffset, out int blockEnd) {
    appendOffset = -1; blockEnd = dirData.Length;
    var off = 0;
    var lastOff = -1;
    while (off + 8 <= dirData.Length) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off + 4, 2));
      if (recLen < 8 || off + recLen > dirData.Length) return false;
      lastOff = off;
      off += recLen;
      if (off >= dirData.Length) break;
    }
    if (lastOff < 0) return false;

    var lastNamLen = dirData[lastOff + 7];
    var lastMin = (8 + lastNamLen + 3) & ~3;
    var lastRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2));
    var slack = lastRecLen - lastMin;
    if (slack < newEntrySize) return false;

    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(lastOff + 4, 2), (ushort)lastMin);
    appendOffset = lastOff + lastMin;
    return true;
  }

  /// <summary>Writes a UFS dirent at <paramref name="pos"/> with <paramref name="recLen"/> covering remaining slack.</summary>
  private static void WriteDirEntry(byte[] dirData, int pos, int ino, string name, byte dtype, int recLen) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var minSize = (8 + nameBytes.Length + 3) & ~3;
    if (recLen < minSize)
      throw new IOException("UFS: not enough room for new dirent.");
    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(pos, 4), (uint)ino);
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(pos + 4, 2), (ushort)recLen);
    dirData[pos + 6] = dtype;
    dirData[pos + 7] = (byte)nameBytes.Length;
    nameBytes.CopyTo(dirData, pos + 8);
    // Zero any padding bytes between name end and end-of-entry.
    for (var i = pos + 8 + nameBytes.Length; i < pos + recLen && i < dirData.Length; ++i)
      dirData[i] = 0;
  }

  /// <summary>
  /// Splice a dirent out: extend prev entry's reclen to absorb this one (or, if
  /// first, zero the inode field — readers skip ino==0 entries).
  /// </summary>
  private static void SpliceOutDirEntry(byte[] dirData, int entryOffset, int prevOffset) {
    var thisRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(entryOffset + 4, 2));
    if (prevOffset >= 0) {
      var prevRecLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2));
      var combined = prevRecLen + thisRecLen;
      if (combined > ushort.MaxValue) combined = ushort.MaxValue;
      BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(prevOffset + 4, 2), (ushort)combined);
      Array.Clear(dirData, entryOffset, thisRecLen);
    } else {
      // First slot: zero inode field; readers skip ino==0 entries.
      BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(entryOffset, 4), 0);
      // Wipe namlen + name so its bytes don't leak.
      Array.Clear(dirData, entryOffset + 6, Math.Max(0, thisRecLen - 6));
    }
  }
}
