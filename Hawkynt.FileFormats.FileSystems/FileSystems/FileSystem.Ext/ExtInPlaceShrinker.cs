#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ext;

/// <summary>
/// Genuine in-place ext2/3/4 volume shrink. Frees the blocks above the new boundary,
/// updating the block bitmap, the block group descriptor free count, the superblock
/// <c>s_blocks_count</c> / <c>s_free_blocks_count</c> (and their 64-bit hi halves), the
/// scaled reserved-block count, every sparse_super backup superblock + GDT, and
/// recomputing crc32c/crc16 metadata checksums where the volume enables them — then
/// truncating the image. Work is <c>O(metadata touched + bytes relocated)</c>: surviving
/// blocks stay byte-identical and only the data blocks that sit above the boundary are
/// physically moved, so this is a true in-place edit and not a re-pack.
///
/// <para><b>Two paths.</b> When no referenced block sits at or above the new boundary
/// the shrink is a pure trailing-free trim that relocates nothing. When referenced data
/// blocks do sit above the boundary the shrinker <b>relocates whole runs</b> down into
/// free space below the boundary (via <see cref="ExtBlockMover"/>, which copies the
/// block bytes and patches the owning inode's direct pointers / depth-0 extent leaves +
/// the block bitmap), then applies the same geometry trim.</para>
///
/// <para><b>Supported relocation shapes.</b> A single-block-group volume whose
/// above-boundary files are <b>direct-block-only</b> (no indirect blocks) or use a
/// <b>depth-0 extent tree</b> (extents live inline in the inode; no interior/index
/// nodes). Whole runs move as a unit so an extent's length stays correct.</para>
///
/// <para><b>Refused (→ <see cref="NotSupportedException"/>, caller rebuilds).</b>
/// Multi-group volumes (the mover's inode lookup assumes group 0); any above-boundary
/// file that uses indirect blocks or an extent tree of depth &gt; 0 (the mover cannot
/// relocate those metadata blocks); a target that would drop a whole block group or
/// fall below the metadata floor; and a target with insufficient free space below the
/// boundary to hold every relocated run. Refusing rather than emitting an image the
/// e2fsck oracle rejects keeps correctness over coverage.</para>
///
/// <para><see cref="ShrinkToFit"/> always succeeds (it picks the boundary one past the
/// highest in-use block, so no relocation is ever needed). An explicit over-tight
/// <see cref="ShrinkToBlocks(System.IO.Stream, uint)"/> target is what drives
/// relocation — or a refusal when the shape is out of scope.</para>
/// </summary>
public static class ExtInPlaceShrinker {

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatCsumSeed = 0x2000;
  private const uint RoCompatSparseSuper = 0x0001;
  private const uint RoCompatGdtCsum = 0x0010;
  private const uint RoCompatMetadataCsum = 0x0400;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;
  private const ushort InodeModeDir = 0x4000;
  private const ushort InodeModeRegular = 0x8000;

  /// <summary>Result of an ext shrink attempt: the before/after byte sizes and how much was physically rewritten.</summary>
  public readonly record struct ShrinkResult(long OriginalSize, long NewSize, long BytesRelocated, long BlocksRelocated) {
    /// <summary>True when the image was actually made smaller.</summary>
    public bool WasReduced => this.NewSize < this.OriginalSize;
  }

  private sealed class Geometry {
    public int BlockSize;
    public uint FirstDataBlock;
    public uint BlocksCount;        // low 32 bits
    public uint BlocksCountHi;      // high 32 bits (64bit feature)
    public uint BlocksPerGroup;
    public uint InodesPerGroup;
    public ushort InodeSize;
    public uint FeatureIncompat;
    public uint FeatureRoCompat;
    public int DescSize;
    public uint GroupCount;
    public long BgdtOffset;
    public uint CsumSeed;
    public byte[] Uuid = new byte[16];
    public bool Has64Bit => (FeatureIncompat & Incompat64Bit) != 0;
    public bool HasMetadataCsum => (FeatureRoCompat & RoCompatMetadataCsum) != 0;
    public bool HasGdtCsum => (FeatureRoCompat & RoCompatGdtCsum) != 0;
    public bool HasSparseSuper => (FeatureRoCompat & RoCompatSparseSuper) != 0;
    public ulong TotalBlocks => ((ulong)BlocksCountHi << 32) | BlocksCount;
    public long BgdOffset(uint group) => BgdtOffset + (long)group * DescSize;
  }

  /// <summary>
  /// Shrinks an ext image in place to the smallest block count that still holds the
  /// current allocation (auto-fit), relocating trailing in-use blocks down.
  /// </summary>
  /// <param name="image">A readable/writable/seekable stream over the ext image; it is modified and truncated in place.</param>
  /// <returns>The shrink result.</returns>
  public static ShrinkResult ShrinkToFit(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ReadGeometry(image);
    var highest = HighestUsedBlock(image, geo);
    // One past the highest in-use block, but never below the metadata floor.
    var target = Math.Max(highest + 1, MetadataFloor(geo));
    return ShrinkToBlocks(image, (uint)target);
  }

  /// <summary>
  /// Shrinks an ext image in place to exactly <paramref name="targetBlocks"/> blocks.
  /// </summary>
  /// <param name="image">A readable/writable/seekable stream over the ext image.</param>
  /// <param name="targetBlocks">The desired new total block count.</param>
  /// <returns>The shrink result.</returns>
  /// <exception cref="NotSupportedException">If the shrink would remove a whole block group, fall below the metadata floor, need to relocate an indirect-block / deep-extent file, or cannot fit the relocated runs below the boundary.</exception>
  public static ShrinkResult ShrinkToBlocks(Stream image, uint targetBlocks) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ReadGeometry(image);
    var originalSize = image.Length;

    if (targetBlocks == 0 || targetBlocks >= geo.BlocksCount)
      return new ShrinkResult(originalSize, originalSize, 0, 0);

    // Restriction: the boundary must stay inside the LAST block group (no group
    // removal). The last group starts at firstDataBlock + (groupCount-1)*blocksPerGroup.
    var lastGroupStart = geo.FirstDataBlock + (long)(geo.GroupCount - 1) * geo.BlocksPerGroup;
    if (targetBlocks <= lastGroupStart)
      throw new NotSupportedException(
        $"ext shrink: target {targetBlocks} would drop a whole block group (last group starts at block {lastGroupStart}); rebuild fallback required.");

    var floor = MetadataFloor(geo);
    if (targetBlocks < floor)
      throw new NotSupportedException(
        $"ext shrink: target {targetBlocks} is below the metadata floor {floor}; rebuild fallback required.");

    // If nothing referenced sits at/above the boundary this is a pure trailing-free
    // trim — relocate nothing. Otherwise relocate the above-boundary data runs down
    // into free space below the boundary, then trim.
    var highest = HighestUsedBlock(image, geo);
    long blocksRelocated = 0;
    long bytesRelocated = 0;
    if (highest >= targetBlocks)
      (blocksRelocated, bytesRelocated) = RelocateAboveBoundary(image, geo, targetBlocks, floor);

    // Update the bitmap (clear bits >= target, pad the bitmap-block tail), the group
    // descriptor + superblock free counts and block count, backup SBs/GDTs, and the
    // metadata checksums. ApplyGeometryShrink re-reads the bitmap, so it observes the
    // mover's bit flips before recomputing every free count + checksum.
    ApplyGeometryShrink(image, geo, targetBlocks);

    var newSize = (long)targetBlocks * geo.BlockSize;
    image.SetLength(newSize);

    return new ShrinkResult(originalSize, newSize, bytesRelocated, blocksRelocated);
  }

  // ── Relocation (move referenced runs below the boundary) ───────────────────

  /// <summary>
  /// A contiguous physical block run owned by one inode. <see cref="Inode"/> is the
  /// owning inode number and <see cref="UsesExtents"/> whether its pointers are a depth-0
  /// extent leaf (true) or legacy direct block pointers (false) — both of which this
  /// shrinker can patch in place after moving the run.
  /// </summary>
  private readonly record struct Run(long Start, int Length, uint Inode, bool UsesExtents);

  // Moves every data run that has any block at/above <paramref name="target"/> down into
  // a contiguous free run entirely below the boundary (and above the metadata floor). The
  // bytes are copied by <see cref="ExtBlockMover.MoveExtent"/>; the owning inode's
  // pointers (direct pointers or depth-0 extent leaves — covers regular files AND
  // directories), the block bitmap, and (when metadata_csum) the inode checksum are
  // patched directly. Refuses (NotSupportedException) for shapes that need to move
  // metadata blocks the mover cannot relocate. Returns (blocksRelocated, bytesRelocated).
  private static (long blocks, long bytes) RelocateAboveBoundary(Stream image, Geometry geo, uint target, long floor) {
    // Guard 1: the inode lookup assumes group 0. Multi-group relocation is out of scope.
    if (geo.GroupCount != 1)
      throw new NotSupportedException(
        $"ext shrink: relocation needs a single block group (this volume has {geo.GroupCount}); rebuild fallback required.");

    var lastGroup = geo.GroupCount - 1;
    var desc = ReadBgd(image, geo, lastGroup);
    var inodeTableOffset = (long)BgdInodeTable(desc, geo.DescSize) * geo.BlockSize;

    // Collect every file/dir run and refuse any above-boundary object whose metadata
    // blocks the mover cannot relocate (indirect blocks, or an extent tree of depth > 0).
    var runs = CollectRunsOrRefuse(image, geo, target, inodeTableOffset);

    // Working free bitmap for the single group: a block is a valid relocation target iff
    // it is below `target`, at/above the metadata floor, and currently free. We mutate
    // this as we assign destinations so two runs never share a destination block.
    var bitmapOffset = (long)BgdBlockBitmap(desc, geo.DescSize) * geo.BlockSize;
    var bitmap = new byte[geo.BlockSize];
    image.Position = bitmapOffset;
    image.ReadExactly(bitmap);
    var groupFirst = geo.FirstDataBlock; // single group → group 0 first block

    var mover = new ExtBlockMover();
    image.Position = 0;
    mover.Init(image);

    long blocksMoved = 0;
    long bytesMoved = 0;
    // Move the highest runs first so freed low space is available to lower runs.
    foreach (var run in runs.Where(r => r.Start + r.Length > target).OrderByDescending(r => r.Start)) {
      var dst = FindFreeRun(bitmap, groupFirst, run.Length, target, floor);
      if (dst < 0)
        throw new NotSupportedException(
          $"ext shrink: no free {run.Length}-block run below the {target}-block boundary to hold a relocated run; rebuild fallback required.");

      var srcOff = run.Start * geo.BlockSize;
      var dstOff = dst * geo.BlockSize;
      var len = (long)run.Length * geo.BlockSize;

      // 1) Copy the block bytes down + zero the vacated source (so freed space reads as
      //    sparse zero and the old data leaves no trace).
      mover.MoveExtent(image, srcOff, dstOff, len, zeroSource: true);

      // 2) Patch the owning inode's pointer(s) for this run, then (metadata_csum) its
      //    checksum. We hold the exact inode, so no name-based dir walk is needed and
      //    directories are handled identically to files.
      RepointInodeRun(image, geo, inodeTableOffset, run, dst);

      // 3) Flip the bitmap: destination used, source free. We update both the working
      //    copy (so later runs see it) and the on-disk bitmap; ApplyGeometryShrink later
      //    re-reads the bitmap and recomputes its checksum from the final state.
      for (var i = 0; i < run.Length; i++) {
        SetBlockUsed(bitmap, (int)(dst + i - groupFirst));
        ClearBlockBit(bitmap, (int)(run.Start + i - groupFirst));
      }
      image.Position = bitmapOffset;
      image.Write(bitmap, 0, geo.BlockSize);
      image.Flush();

      blocksMoved += run.Length;
      bytesMoved += len;
    }
    return (blocksMoved, bytesMoved);
  }

  // Patches the run's owning inode so its pointer(s) for the moved run point at the new
  // start, then recomputes the inode checksum when metadata_csum is enabled.
  private static void RepointInodeRun(Stream image, Geometry geo, long inodeTableOffset, Run run, long newStart) {
    var inode = ReadInode(image, geo, inodeTableOffset, run.Inode);
    if (run.UsesExtents) {
      var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
      for (var i = 0; i < entries; i++) {
        var off = 40 + 12 + i * 12;
        if (off + 12 > 40 + 60) break;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 4)) & 0x7FFF;
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 8));
        var start = ((long)startHi << 32) | startLo;
        if (start == run.Start && len == run.Length) {
          BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(off + 6), (ushort)(newStart >> 32));
          BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(off + 8), (uint)(newStart & 0xFFFFFFFF));
          break;
        }
      }
    } else {
      for (var i = 0; i < 12; i++) {
        var ptr = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
        if (ptr == 0) break;
        if (ptr >= run.Start && ptr < run.Start + run.Length)
          BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(40 + i * 4), (uint)(newStart + (ptr - run.Start)));
      }
    }
    WriteInode(image, geo, inodeTableOffset, run.Inode, inode);
    if (geo.HasMetadataCsum) WriteInodeChecksum(image, geo, inodeTableOffset, run.Inode);
  }

  private static void WriteInode(Stream image, Geometry geo, long inodeTableOffset, uint inodeNum, byte[] inode) {
    var index = (inodeNum - 1) % geo.InodesPerGroup;
    var offset = inodeTableOffset + (long)index * geo.InodeSize;
    image.Position = offset;
    image.Write(inode, 0, geo.InodeSize);
    image.Flush();
  }

  // Recomputes the per-inode crc32c (l_i_checksum_lo @ 0x7C, i_checksum_hi @ 0x82 when
  // the inode is large enough) — mirrors the e2fsprogs algorithm: seed with
  // crc32c(fs_seed, inode_index_le32) then crc32c(., i_generation_le32), zero the csum
  // fields, crc32c over the whole inode, split the result back into lo/hi.
  private static void WriteInodeChecksum(Stream image, Geometry geo, long inodeTableOffset, uint inodeNum) {
    var inode = ReadInode(image, geo, inodeTableOffset, inodeNum);
    var idxLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(idxLe, inodeNum);
    var crc = Crc32c(geo.CsumSeed, idxLe);
    var genLe = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(genLe, BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(100, 4)));
    crc = Crc32c(crc, genLe);

    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x7C, 2), 0);
    var hasHi = geo.InodeSize > 128 && BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(128, 2)) >= 4;
    if (hasHi) BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x82, 2), 0);

    crc = Crc32c(crc, inode);
    BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x7C, 2), (ushort)(crc & 0xFFFF));
    if (hasHi) BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(0x82, 2), (ushort)(crc >> 16));
    WriteInode(image, geo, inodeTableOffset, inodeNum, inode);
  }

  // Finds the first contiguous free run of `length` blocks in [floor, limit) within the
  // single-group block bitmap (block numbers are group-relative + groupFirst). Returns
  // the absolute starting block, or -1 if none fits.
  private static long FindFreeRun(byte[] bitmap, long groupFirst, int length, uint limit, long floor) {
    var runStart = -1L;
    for (long b = floor; b < limit; b++) {
      var idx = (int)(b - groupFirst);
      if (!IsBlockUsed(bitmap, idx)) {
        if (runStart < 0) runStart = b;
        if (b - runStart + 1 >= length) return runStart;
      } else {
        runStart = -1;
      }
    }
    return -1;
  }

  // Walks the directory tree (group 0 only — guarded by the single-group check) and
  // returns the physical data runs of every regular file AND every directory (both can
  // have their pointers patched after a move). Refuses (NotSupportedException) if any
  // object with a block at/above `target` uses indirect blocks or a depth>0 extent tree —
  // metadata blocks this shrinker cannot relocate.
  private static List<Run> CollectRunsOrRefuse(Stream image, Geometry geo, uint target, long inodeTableOffset) {
    var runs = new List<Run>();

    foreach (var inodeNum in EnumerateInodes(image, geo, inodeTableOffset)) {
      var inode = ReadInode(image, geo, inodeTableOffset, inodeNum);
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
      var usesExtents = (flags & ExtentsFlag) != 0;
      var objRuns = new List<Run>();
      // True when this object's data lives in blocks the shrinker cannot relocate
      // together with the data: indirect blocks (the indirect pointer blocks themselves)
      // or an extent tree of depth > 0 (interior/index nodes).
      var hasUnmovableMetadata = false;
      // True when any of this object's data blocks sit at/above the boundary.
      var reachesAbove = false;

      if (usesExtents) {
        var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
        if (ehMagic != ExtentMagic) continue; // inline-data / empty
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
        if (depth > 0) {
          // Interior extent tree: walk the leaves to learn whether any data block is
          // above the boundary, but never relocate (the index nodes are unmovable here).
          hasUnmovableMetadata = true;
          reachesAbove = ExtentTreeReachesAbove(image, geo, inode, depth, entries, target);
        } else {
          for (var i = 0; i < entries; i++) {
            var off = 40 + 12 + i * 12;
            if (off + 12 > 40 + 60) break;
            var len = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 4)) & 0x7FFF;
            var startHi = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 6));
            var startLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 8));
            var start = ((long)startHi << 32) | startLo;
            if (len > 0) objRuns.Add(new Run(start, len, inodeNum, UsesExtents: true));
          }
          reachesAbove = objRuns.Any(r => r.Start + r.Length > target);
        }
      } else {
        // Direct blocks: i_block[0..11]. i_block[12..14] (offsets 88/92/96) are the
        // single/double/triple indirect pointers — non-zero means indirect blocks exist.
        long runStart = -1, runEnd = -1;
        for (var i = 0; i < 12; i++) {
          var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
          if (bn == 0) break;
          if (runStart < 0) { runStart = runEnd = bn; }
          else if (bn == runEnd + 1) { runEnd = bn; }
          else { objRuns.Add(new Run(runStart, (int)(runEnd - runStart + 1), inodeNum, UsesExtents: false)); runStart = runEnd = bn; }
        }
        if (runStart >= 0) objRuns.Add(new Run(runStart, (int)(runEnd - runStart + 1), inodeNum, UsesExtents: false));
        reachesAbove = objRuns.Any(r => r.Start + r.Length > target);

        for (var lvl = 0; lvl < 3; lvl++) {
          var ind = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88 + lvl * 4));
          if (ind == 0) continue;
          hasUnmovableMetadata = true;
          if (IndirectReachesAbove(image, geo, ind, lvl + 1, target)) reachesAbove = true;
        }
      }

      // An object only constrains us when its data actually reaches above the boundary.
      // One that does AND has unmovable metadata cannot be relocated correctly → refuse.
      if (!reachesAbove) continue;
      if (hasUnmovableMetadata)
        throw new NotSupportedException(
          "ext shrink: an above-boundary file/dir uses indirect blocks or a depth>0 extent tree; " +
          "the shrinker cannot relocate those metadata blocks. Rebuild fallback required.");
      runs.AddRange(objRuns);
    }
    return runs;
  }

  // Enumerates the inode numbers of every regular file and directory reachable from the
  // root inode. Single-group only (inode table lives in group 0). Directories are
  // included because their data blocks must relocate too (and their pointers are
  // patchable exactly like a file's).
  private static IEnumerable<uint> EnumerateInodes(Stream image, Geometry geo, long inodeTableOffset) {
    var seen = new HashSet<uint>();
    var result = new List<uint> { RootInode };
    WalkDir(image, geo, inodeTableOffset, RootInode, result, seen);
    return result;
  }

  private static void WalkDir(Stream image, Geometry geo, long inodeTableOffset, uint dirInode,
      List<uint> objects, HashSet<uint> seen) {
    if (!seen.Add(dirInode)) return;
    var inode = ReadInode(image, geo, inodeTableOffset, dirInode);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(0));
    if ((mode & InodeModeDir) == 0) return;
    var dirBytes = ReadDirData(image, geo, inode);

    var off = 0;
    while (off + 8 <= dirBytes.Length) {
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 4));
      var nameLen = dirBytes[off + 6];
      if (recLen == 0) break;
      if (off + 8 + nameLen > dirBytes.Length) break;
      if (ino != 0 && nameLen > 0) {
        var name = System.Text.Encoding.UTF8.GetString(dirBytes, off + 8, nameLen);
        if (name is not ("." or "..")) {
          var child = ReadInode(image, geo, inodeTableOffset, ino);
          var m = BinaryPrimitives.ReadUInt16LittleEndian(child.AsSpan(0));
          if ((m & InodeModeDir) != 0) {
            // Record the directory inode (its data blocks may also need relocating), then
            // recurse — WalkDir's own `seen` guard prevents re-entry. Do NOT add to `seen`
            // here, or the recursive call would bail before reading the dir's contents.
            if (!objects.Contains(ino)) objects.Add(ino);
            WalkDir(image, geo, inodeTableOffset, ino, objects, seen);
          } else if (((m & 0xF000) == InodeModeRegular) && seen.Add(ino)) {
            objects.Add(ino);
          }
        }
      }
      off += recLen;
    }
  }

  // Reads a directory's raw entry bytes (direct or depth-0 extents — directories created
  // by mkfs/debugfs in our single-group images are small and fit this profile).
  private static byte[] ReadDirData(Stream image, Geometry geo, byte[] inode) {
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));
    var usesExtents = (flags & ExtentsFlag) != 0;
    using var ms = new MemoryStream();
    long remaining = size;
    if (usesExtents) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40)) != ExtentMagic) return ms.ToArray();
      var depth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));
      if (depth != 0) return ms.ToArray(); // deep dir tree — not in our single-group scope
      var entries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
      for (var i = 0; i < entries && remaining > 0; i++) {
        var off = 40 + 12 + i * 12;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 4)) & 0x7FFF;
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 8));
        var start = ((long)startHi << 32) | startLo;
        for (var b = 0; b < len && remaining > 0; b++)
          remaining -= AppendBlock(image, geo, start + b, remaining, ms);
      }
    } else {
      for (var i = 0; i < 12 && remaining > 0; i++) {
        var bn = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
        if (bn == 0) break;
        remaining -= AppendBlock(image, geo, bn, remaining, ms);
      }
    }
    return ms.ToArray();
  }

  private static long AppendBlock(Stream image, Geometry geo, long block, long remaining, MemoryStream ms) {
    var off = block * geo.BlockSize;
    if (off + geo.BlockSize > image.Length) return remaining;
    var toRead = (int)Math.Min(remaining, geo.BlockSize);
    var buf = new byte[toRead];
    image.Position = off;
    image.ReadExactly(buf);
    ms.Write(buf, 0, toRead);
    return toRead;
  }

  private static byte[] ReadInode(Stream image, Geometry geo, long inodeTableOffset, uint inodeNum) {
    var index = (inodeNum - 1) % geo.InodesPerGroup;
    var offset = inodeTableOffset + (long)index * geo.InodeSize;
    var buf = new byte[geo.InodeSize];
    image.Position = offset;
    image.ReadExactly(buf);
    return buf;
  }

  private static ulong BgdInodeTable(byte[] b, int descSize) {
    ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8, 4));
    if (descSize >= 64) lo |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40, 4)) << 32;
    return lo;
  }

  // True if any leaf extent of a depth>0 extent tree (rooted in the inode) has a data
  // block at/above the boundary. We only need the answer, not the runs (deep trees are
  // refused regardless).
  private static bool ExtentTreeReachesAbove(Stream image, Geometry geo, byte[] inode, int depth, int entries, uint target) {
    for (var i = 0; i < entries; i++) {
      var off = 40 + 12 + i * 12;
      if (off + 12 > 40 + 60) break;
      var childLo = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(off + 4));
      var childHi = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(off + 8));
      var child = ((long)childHi << 32) | childLo;
      if (ExtentNodeReachesAbove(image, geo, child, depth - 1, target)) return true;
    }
    return false;
  }

  private static bool ExtentNodeReachesAbove(Stream image, Geometry geo, long block, int depth, uint target) {
    var off = block * geo.BlockSize;
    if (off + geo.BlockSize > image.Length) return false;
    var node = new byte[geo.BlockSize];
    image.Position = off;
    image.ReadExactly(node);
    if (BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(0)) != ExtentMagic) return false;
    var entries = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
    for (var i = 0; i < entries; i++) {
      var eo = 12 + i * 12;
      if (eo + 12 > node.Length) break;
      if (depth == 0) {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(eo + 4)) & 0x7FFF;
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(eo + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(eo + 8));
        var start = ((long)startHi << 32) | startLo;
        if (len > 0 && start + len > target) return true;
      } else {
        var childLo = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(eo + 4));
        var childHi = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(eo + 8));
        var child = ((long)childHi << 32) | childLo;
        if (ExtentNodeReachesAbove(image, geo, child, depth - 1, target)) return true;
      }
    }
    return false;
  }

  // True if any data block reached through an indirect pointer block (single/double/
  // triple, per <paramref name="level"/>) is at/above the boundary.
  private static bool IndirectReachesAbove(Stream image, Geometry geo, uint block, int level, uint target) {
    if (block >= target) return true; // the indirect block itself lives above the boundary
    var off = (long)block * geo.BlockSize;
    if (off + geo.BlockSize > image.Length) return false;
    var buf = new byte[geo.BlockSize];
    image.Position = off;
    image.ReadExactly(buf);
    var per = geo.BlockSize / 4;
    for (var i = 0; i < per; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
      if (ptr == 0) continue;
      if (level == 1) { if (ptr >= target) return true; }
      else if (IndirectReachesAbove(image, geo, ptr, level - 1, target)) return true;
    }
    return false;
  }

  // ── Geometry / bitmap / superblock + checksum updates ───────────────────────

  // Clears block bitmap bits >= target, sets the bitmap-block padding tail, recomputes
  // the last group's free count + the superblock counts, rewrites all backup SBs/GDTs,
  // and recomputes metadata checksums. Returns the number of freed (trimmed) blocks.
  private static long ApplyGeometryShrink(Stream image, Geometry geo, uint target) {
    var lastGroup = geo.GroupCount - 1;
    var desc = ReadBgd(image, geo, lastGroup);
    var blockBitmapBlock = BgdBlockBitmap(desc, geo.DescSize);
    var bitmapOffset = (long)blockBitmapBlock * geo.BlockSize;
    var groupFirstBlock = geo.FirstDataBlock + (long)lastGroup * geo.BlocksPerGroup;

    var bitmap = new byte[geo.BlockSize];
    image.Position = bitmapOffset;
    image.ReadExactly(bitmap);

    // Count current free blocks in [target, oldBlocksCount) that we are removing (so
    // the surviving free count is correct) and clear those bits.
    long removedFree = 0;
    for (long b = target; b < geo.BlocksCount; b++) {
      var idx = (int)(b - groupFirstBlock);
      if (!IsBlockUsed(bitmap, idx)) removedFree++;
      ClearBlockBit(bitmap, idx);
    }

    // Pad the block bitmap: bits for blocks that no longer exist (>= newBlocksInGroup)
    // up to the end of the bitmap block must read as used (mkfs/e2fsck convention).
    var newBlocksInGroup = (int)(target - groupFirstBlock);
    for (var bit = newBlocksInGroup; bit < geo.BlockSize * 8; bit++)
      SetBlockUsed(bitmap, bit);
    image.Position = bitmapOffset;
    image.Write(bitmap, 0, geo.BlockSize);

    // Group descriptor free-block count loses the removed free blocks.
    var descFree = BgdFreeBlocks(desc, geo.DescSize);
    SetBgdFreeBlocks(desc, geo.DescSize, (uint)(descFree - removedFree));
    if (geo.HasMetadataCsum || geo.HasGdtCsum) {
      WriteBitmapCsumIntoDesc(geo, desc, bitmap);
      WriteGroupDescChecksum(geo, desc, lastGroup);
    }
    WriteBgd(image, geo, lastGroup, desc);

    // Superblock: new block count + reduced free count, and (when metadata_csum) its
    // own crc. Reserved blocks are scaled down proportionally like resize2fs does.
    UpdateSuperblock(image, geo, target, removedFree);

    // Mirror the primary superblock + GDT to every sparse_super backup that still
    // exists in the shrunk volume so e2fsck's backup comparison stays clean.
    SyncBackups(image, geo, target);

    return removedFree;
  }

  private static void UpdateSuperblock(Stream image, Geometry geo, uint target, long removedFree) {
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);

    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(4), target);                 // s_blocks_count_lo
    if (geo.Has64Bit) BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x150), 0); // s_blocks_count_hi

    var free = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(12));
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(12), (uint)(free - removedFree));
    if (geo.Has64Bit) {
      var freeHi = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x158));
      // removedFree fits in the low 32 bits for any realistic image; clamp safely.
      if (freeHi != 0 && (free - removedFree) > free) BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x158), freeHi - 1);
    }

    // Reserved block count (s_r_blocks_count_lo @ 8): scale proportionally to the new
    // size so the reserved fraction is preserved (matches resize2fs behaviour).
    var reserved = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(8));
    if (geo.BlocksCount > 0) {
      var scaled = (uint)((ulong)reserved * target / geo.BlocksCount);
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(8), scaled);
    }

    if (geo.HasMetadataCsum) {
      var crc = Crc32c(0xFFFFFFFFu, sb.AsSpan(0, 0x3FC));
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(0x3FC), crc);
    }

    image.Position = SuperblockOffset;
    image.Write(sb, 0, 1024);
    image.Flush();
  }

  // Copies the (already-updated) primary superblock + GDT to each surviving
  // sparse_super backup. Without sparse_super, every group holds a backup.
  private static void SyncBackups(Stream image, Geometry geo, uint target) {
    if (geo.GroupCount <= 1) return;

    var primarySb = new byte[1024];
    image.Position = SuperblockOffset;
    image.ReadExactly(primarySb);

    var gdtBytes = (int)geo.GroupCount * geo.DescSize;
    var gdt = new byte[gdtBytes];
    image.Position = geo.BgdtOffset;
    image.ReadExactly(gdt);

    for (uint g = 1; g < geo.GroupCount; g++) {
      if (geo.HasSparseSuper && !HasSuperBackup(g)) continue;
      var groupStartBlock = geo.FirstDataBlock + (long)g * geo.BlocksPerGroup;
      if (groupStartBlock >= target) continue; // backup lives in a removed region

      // Backup superblock sits at the first block of the group; the backup GDT in the
      // following block(s). s_block_group_nr (@0x5A) must carry the group number.
      var sbCopy = (byte[])primarySb.Clone();
      BinaryPrimitives.WriteUInt16LittleEndian(sbCopy.AsSpan(0x5A), (ushort)g);
      if (geo.HasMetadataCsum) {
        var crc = Crc32c(0xFFFFFFFFu, sbCopy.AsSpan(0, 0x3FC));
        BinaryPrimitives.WriteUInt32LittleEndian(sbCopy.AsSpan(0x3FC), crc);
      }
      var sbOffset = groupStartBlock * geo.BlockSize;
      // For 1 KiB blocks the primary SB is at byte 1024 inside block 1; backups sit at
      // the very start of their group's first block.
      if (sbOffset + 1024 <= image.Length) {
        image.Position = sbOffset;
        image.Write(sbCopy, 0, 1024);
      }
      var gdtOffset = (groupStartBlock + 1) * geo.BlockSize;
      if (gdtOffset + gdtBytes <= image.Length) {
        image.Position = gdtOffset;
        image.Write(gdt, 0, gdtBytes);
      }
    }
    image.Flush();
  }

  // sparse_super: a group has a SB/GDT backup iff it is 0, 1, or a power of 3, 5, 7.
  private static bool HasSuperBackup(uint group) {
    if (group is 0 or 1) return true;
    foreach (var p in new uint[] { 3, 5, 7 }) {
      var n = p;
      while (n < group) n *= p;
      if (n == group) return true;
    }
    return false;
  }

  // ── Block bitmap helpers ─────────────────────────────────────────────────

  private static bool IsBlockUsed(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return true;
    return (bitmap[idx / 8] & (1 << (idx % 8))) != 0;
  }
  private static void SetBlockUsed(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return;
    bitmap[idx / 8] |= (byte)(1 << (idx % 8));
  }
  private static void ClearBlockBit(byte[] bitmap, int idx) {
    if (idx < 0 || idx / 8 >= bitmap.Length) return;
    bitmap[idx / 8] &= (byte)~(1 << (idx % 8));
  }

  private static long HighestUsedBlock(Stream image, Geometry geo) {
    // Scan every group's block bitmap for the highest set bit. Streaming: one
    // bitmap block per group.
    long highest = geo.FirstDataBlock;
    var bitmap = new byte[geo.BlockSize];
    for (uint g = 0; g < geo.GroupCount; g++) {
      var desc = ReadBgd(image, geo, g);
      var bb = BgdBlockBitmap(desc, geo.DescSize);
      image.Position = (long)bb * geo.BlockSize;
      image.ReadExactly(bitmap);
      var groupFirst = geo.FirstDataBlock + (long)g * geo.BlocksPerGroup;
      var blocksInGroup = (int)Math.Min(geo.BlocksPerGroup, geo.BlocksCount - groupFirst);
      for (var i = blocksInGroup - 1; i >= 0; i--) {
        if (IsBlockUsed(bitmap, i)) { highest = Math.Max(highest, groupFirst + i); break; }
      }
    }
    return highest;
  }

  // Metadata floor for the LAST group: blocks up to and including its inode table
  // must survive (block bitmap, inode bitmap, inode table). Returns first block index
  // strictly after the last group's fixed metadata.
  private static long MetadataFloor(Geometry geo) {
    var lastGroup = geo.GroupCount - 1;
    var groupFirst = geo.FirstDataBlock + (long)lastGroup * geo.BlocksPerGroup;
    // Reserve the last group's fixed metadata span: block bitmap (1) + inode bitmap
    // (1) + inode table, plus a small headroom. The boundary must not drop into it.
    var inodeTableBlocks = (int)((geo.InodesPerGroup * (uint)geo.InodeSize + (uint)geo.BlockSize - 1) / (uint)geo.BlockSize);
    return groupFirst + 2 + inodeTableBlocks + 4;
  }

  // ── Group descriptor field access (folds 64-bit hi halves) ──────────────────

  private static byte[] ReadBgd(Stream image, Geometry g, uint group) {
    var buf = new byte[g.DescSize];
    image.Position = g.BgdOffset(group);
    image.ReadExactly(buf);
    return buf;
  }
  private static void WriteBgd(Stream image, Geometry g, uint group, byte[] buf) {
    image.Position = g.BgdOffset(group);
    image.Write(buf, 0, g.DescSize);
  }
  private static ulong BgdBlockBitmap(byte[] b, int descSize) {
    ulong lo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4));
    if (descSize >= 64) lo |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32, 4)) << 32;
    return lo;
  }
  private static uint BgdFreeBlocks(byte[] b, int descSize) {
    uint lo = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12, 2));
    if (descSize >= 64) lo |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(44, 2)) << 16;
    return lo;
  }
  private static void SetBgdFreeBlocks(byte[] b, int descSize, uint v) {
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12, 2), (ushort)(v & 0xFFFF));
    if (descSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(44, 2), (ushort)(v >> 16));
  }

  // ── Checksums (mirrors ExtModifier conventions) ─────────────────────────────

  private static void WriteBitmapCsumIntoDesc(Geometry g, byte[] desc, byte[] bitmap) {
    if (!g.HasMetadataCsum) return;
    var bytes = (int)((g.BlocksPerGroup + 7) / 8);
    if (bytes > g.BlockSize) bytes = g.BlockSize;
    var csum = Crc32c(g.CsumSeed, bitmap.AsSpan(0, bytes));
    BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(24, 2), (ushort)(csum & 0xFFFF));
    if (g.DescSize >= 64) BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(56, 2), (ushort)(csum >> 16));
  }

  private static void WriteGroupDescChecksum(Geometry g, byte[] desc, uint group) {
    if (g.HasMetadataCsum) {
      var groupLe = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
      var crc = Crc32c(g.CsumSeed, groupLe);
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), 0);
      crc = Crc32c(crc, desc.AsSpan(0, g.DescSize));
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), (ushort)(crc & 0xFFFF));
    } else if (g.HasGdtCsum) {
      ushort crc = 0xFFFF;
      crc = Crc16(crc, g.Uuid);
      var groupLe = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(groupLe, group);
      crc = Crc16(crc, groupLe);
      crc = Crc16(crc, desc.AsSpan(0, 0x1E).ToArray());
      if (g.DescSize > 0x20) crc = Crc16(crc, desc.AsSpan(0x20, g.DescSize - 0x20).ToArray());
      BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(0x1E, 2), crc);
    }
  }

  private static uint Crc32c(uint seed, ReadOnlySpan<byte> data) {
    const uint poly = 0x82F63B78u;
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
    }
    return crc;
  }

  private static ushort Crc16(ushort crc, byte[] data) {
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
    }
    return crc;
  }

  // ── Superblock parse ────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[1024];
    image.Position = SuperblockOffset;
    if (image.Length < SuperblockOffset + 1024)
      throw new InvalidDataException("ext shrink: image too small for superblock.");
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext shrink: invalid magic 0x{magic:X4}.");

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(4));
    var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24));
    var blockSize = 1024 << (int)logBlock;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(32));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(76));
    var inodeSize = revLevel >= 1 ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88)) : (ushort)128;
    if (inodeSize == 0) inodeSize = 128;
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
    var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100));
    var uuid = sb.AsSpan(104, 16).ToArray();
    var blocksCountHi = (featureIncompat & Incompat64Bit) != 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x150)) : 0u;

    var descSize = 32;
    if ((featureIncompat & Incompat64Bit) != 0) {
      descSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(254));
      if (descSize < 32) descSize = 32;
    }

    if (blocksPerGroup == 0) throw new InvalidDataException("ext shrink: blocks-per-group is zero.");
    var groupCount = (uint)(((ulong)blocksCount - firstData + blocksPerGroup - 1) / blocksPerGroup);
    var bgdtOffset = (long)(firstData + 1) * blockSize;

    uint csumSeed;
    if ((featureIncompat & IncompatCsumSeed) != 0)
      csumSeed = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x270));
    else
      csumSeed = Crc32c(0xFFFFFFFFu, uuid);

    return new Geometry {
      BlockSize = blockSize,
      FirstDataBlock = firstData,
      BlocksCount = blocksCount,
      BlocksCountHi = blocksCountHi,
      BlocksPerGroup = blocksPerGroup,
      InodesPerGroup = inodesPerGroup,
      InodeSize = inodeSize,
      FeatureIncompat = featureIncompat,
      FeatureRoCompat = featureRoCompat,
      DescSize = descSize,
      GroupCount = groupCount,
      BgdtOffset = bgdtOffset,
      CsumSeed = csumSeed,
      Uuid = uuid,
    };
  }
}
