#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Xenix;

/// <summary>
/// In-place modifier for Microsoft / SCO Xenix V (s5fs-compatible) filesystem
/// images emitted by <see cref="XenixWriter"/>. Performs Add / Remove with
/// O(touched bytes) random-access I/O against the existing image — only the
/// superblock, the affected inode slots, the root directory's first zone, and
/// the file's data zones are read or written.
///
/// <para><b>s5fs free-list caches.</b> The on-disk superblock carries an inode
/// free-list cache (<c>s_ninode</c> + <c>s_inode[NICINOD]</c>) and a data-zone
/// free-list cache (<c>s_nfree</c> + <c>s_free[NICFREE]</c>). On
/// <see cref="AddFile"/> we pop from the cache (refilling from a full
/// inode-table or zone-usage scan when empty); on <see cref="RemoveFile"/> we
/// push back into the cache (with the classic s5fs "indirect free list" spill
/// into a freed block when the cache fills, or silently dropping the freed
/// entry when even the spill is full — it'll be discovered by the next refill
/// scan). The WORM writer leaves the caches empty (s_ninode == s_nfree == 0),
/// so the first mutating operation always seeds both caches from scratch.</para>
///
/// <para><b>Scope.</b> Files are addressed through the inode's 10 direct zone
/// slots only — same budget as the WORM writer. Files larger than
/// <c>10 * blockSize</c> bytes are rejected with
/// <see cref="NotSupportedException"/> (indirect blocks are out of scope here
/// the same way they are for WORM). Additions extend the image past its current
/// length when new zones are needed. Inode allocation reuses unused slots
/// inside the existing inode table — when the table is exhausted the operation
/// throws cleanly; growing the inode table would require shifting every data
/// zone and is left to a full rebuild.</para>
///
/// <para><b>Root-only.</b> The modifier wires new dir-ents into the root
/// directory (inode 2). Nested paths flatten to their leaf names (matching the
/// flat-root scope of the IArchiveModifiable contract for s5fs-style writers).</para>
/// </summary>
public static class XenixModifier {

  // ── On-disk constants (mirror XenixWriter) ──────────────────────────────
  private const int BootBlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int BlockSize = 1024;          // we always emit type-code 2 (1024 B blocks)
  private const int InodeSize = 64;
  private const int InodesPerBlock = BlockSize / InodeSize; // 16
  internal const uint MagicXenix = 0xFD187E20;
  private const int RootInode = 2;
  private const int DirectZones = 10;
  private const int DirEntrySize = 16;
  private const int MaxNameLength = 14;
  private const int EntriesPerZone = BlockSize / DirEntrySize; // 64

  // S_IFREG | 0644
  private const ushort ModeRegularFile = 0x81A4;
  private const ushort InodeModeDir = 0x4000;

  // s5fs superblock cache sizes (Xenix V tunables).
  //
  // Layout (LE) we maintain inside the superblock at +SuperblockOffset:
  //   sb +  0  u16 s_isize          (zones in the inode list — kept zero,
  //                                  derived from inode table block count)
  //   sb +  2  u32 s_fsize          (total blocks in the filesystem)
  //   sb +  6  u16 s_nfree          (count of free-zone entries in s_free[])
  //   sb +  8  u32 s_free[NICFREE]  (cached free-zone block numbers)
  //   sb +  8 + 4*NICFREE  u16 s_ninode       (count of free-inode entries)
  //   sb + 10 + 4*NICFREE  u16 s_inode[NICINOD] (cached free-inode numbers)
  //
  // s5fs ties s_nfree/s_ninode and the cache arrays together; we keep both
  // caches inside the existing 1024-byte superblock and pick sizes that fit
  // comfortably below the magic at +504.
  //
  //   NICFREE = 50 zones (4 B each = 200 B) — historical Xenix tunable.
  //   NICINOD = 100 inodes (2 B each = 200 B) — historical Xenix tunable.
  //
  // The cache lives at sb+0..sb+412, well clear of the magic at sb+504.
  private const int NicFree = 50;
  private const int NicInod = 100;

  private const int OffSIsize = 0;
  private const int OffSFsize = 2;
  private const int OffSNfree = 6;
  private const int OffSFree = 8;
  private const int OffSNinode = OffSFree + 4 * NicFree;          // 208
  private const int OffSInode = OffSNinode + 2;                   // 210
  private const int CacheEndOff = OffSInode + 2 * NicInod;        // 410
  private const int OffSMagic = 504;
  private const int OffSType = 508;

  // ── Public API ──────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a regular file under the root directory of an existing Xenix V image
  /// with name <paramref name="name"/> and body <paramref name="data"/>.
  /// Throws <see cref="NotSupportedException"/> for files larger than
  /// <c>10 * 1024</c> bytes (only direct zones are addressed), and throws
  /// <see cref="IOException"/> if the inode table or root directory has no
  /// remaining slot.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (data.Length > DirectZones * BlockSize)
      throw new NotSupportedException(
        $"Xenix R/W: only direct zones are addressed (max {DirectZones * BlockSize} bytes per file); "
        + $"got {data.Length} bytes.");

    var sb = ReadSuperblock(image);
    VerifyMagic(sb);

    // Refill caches lazily from a full image scan when empty.
    if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNinode)) == 0)
      RefillInodeCache(image, sb);
    if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNfree)) == 0)
      RefillZoneCache(image, sb);

    // 1. Allocate inode.
    var newInodeNum = PopInode(sb)
      ?? throw new IOException("Xenix R/W: no free inodes available in the inode table.");

    // 2. Allocate zones (extending the image past the current end is fine —
    //    Zone numbers come from s_free[] which the refill seeded with every
    //    free block at or above firstDataBlock).
    var zonesNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
    var allocatedZones = new List<uint>(zonesNeeded);
    try {
      for (var i = 0; i < zonesNeeded; i++) {
        var z = PopZone(image, sb)
          ?? throw new IOException(
            "Xenix R/W: no free data zones available (image freelist + refill scan exhausted).");
        allocatedZones.Add(z);
      }
    } catch {
      // Roll allocated zones back into the cache (best-effort; if the cache
      // overflows on push, the next refill scan will pick them up).
      foreach (var z in allocatedZones)
        PushZone(image, sb, z);
      // Roll the inode back onto the cache as well.
      PushInode(sb, (uint)newInodeNum);
      WriteSuperblock(image, sb);
      throw;
    }

    // 3. Write the file body into the allocated zones.
    var written = 0;
    foreach (var zone in allocatedZones) {
      var toWrite = Math.Min(BlockSize, data.Length - written);
      var blockBytes = new byte[BlockSize];
      if (toWrite > 0) Array.Copy(data, written, blockBytes, 0, toWrite);
      WriteAt(image, (long)zone * BlockSize, blockBytes);
      written += toWrite;
    }

    // 4. Build and write the new inode.
    var inodeBytes = new byte[InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes, ModeRegularFile);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(2), 1); // nlinks
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8), (uint)data.Length);
    for (var i = 0; i < allocatedZones.Count; i++)
      Write24(inodeBytes.AsSpan(12 + i * 3), allocatedZones[i]);
    // mtime/atime/ctime past +40 — we leave zeroed for parity with WORM.
    WriteInode(image, (uint)newInodeNum, inodeBytes);

    // 5. Append a dir-ent inside the root directory.
    AppendRootDirEntry(image, sb, (uint)newInodeNum, name);

    // 6. Persist the superblock.
    WriteSuperblock(image, sb);
  }

  /// <summary>
  /// Removes the named regular file from the root directory of an existing
  /// Xenix V image. Returns <c>false</c> if no such entry exists; refuses to
  /// remove directories.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var sb = ReadSuperblock(image);
    VerifyMagic(sb);

    var truncatedName = TruncateName(name);

    // Walk the root directory's direct zones for the matching entry.
    var rootInode = ReadInode(image, RootInode);
    var (rootMode, rootSize, rootZones) = ParseInode(rootInode);
    if ((rootMode & 0xF000) != InodeModeDir)
      throw new InvalidDataException("Xenix R/W: root inode is not a directory.");

    for (var zi = 0; zi < DirectZones; zi++) {
      var zone = rootZones[zi];
      if (zone == 0) break;
      var dirData = ReadBlock(image, (long)zone * BlockSize);
      for (var off = 0; off + DirEntrySize <= dirData.Length; off += DirEntrySize) {
        var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
        if (ino == 0) continue;
        var entryName = ReadNullTermString(dirData, off + 2, MaxNameLength);
        if (!entryName.Equals(truncatedName, StringComparison.OrdinalIgnoreCase)) continue;

        // Read the target inode + refuse directories.
        var tgt = ReadInode(image, ino);
        var (tgtMode, _, tgtZones) = ParseInode(tgt);
        if ((tgtMode & 0xF000) == InodeModeDir)
          throw new InvalidOperationException(
            $"Xenix R/W: refusing to remove directory '{name}'.");

        // 1. Zero the dirent (16 bytes) — entry slot becomes reusable.
        Array.Clear(dirData, off, DirEntrySize);
        WriteAt(image, (long)zone * BlockSize, dirData);

        // 2. Free target's data zones back into the s_free[] cache.
        for (var i = 0; i < DirectZones; i++) {
          var fz = tgtZones[i];
          if (fz == 0) break;
          if (wipeData)
            WriteAt(image, (long)fz * BlockSize, new byte[BlockSize]);
          PushZone(image, sb, fz);
        }

        // 3. Zero the inode (64 bytes) — slot becomes reusable.
        WriteInode(image, ino, new byte[InodeSize]);

        // 4. Push freed inode number onto the s_inode[] cache.
        PushInode(sb, ino);

        WriteSuperblock(image, sb);
        return true;
      }
    }

    return false;
  }

  // ── s5fs cache pop / push (inode + zone) ────────────────────────────────

  private static int? PopInode(byte[] sb) {
    var n = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNinode));
    if (n == 0) return null;
    // LIFO: pop from the top.
    n--;
    var inum = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSInode + n * 2));
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNinode), n);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSInode + n * 2), 0);
    return inum;
  }

  private static void PushInode(byte[] sb, uint inodeNumber) {
    var n = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNinode));
    if (n >= NicInod) {
      // Cache full — drop the entry. Next refill scan will recover it.
      return;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSInode + n * 2), (ushort)inodeNumber);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNinode), (ushort)(n + 1));
  }

  /// <summary>
  /// Pops one zone off the s_free[] cache. When the cache empties and the top
  /// of the cache pointed at an indirect-list block (the classic s5fs spill
  /// chain), pull the next NICFREE addresses out of that block before
  /// returning. Returns null only when truly out of free zones.
  /// </summary>
  private static uint? PopZone(Stream image, byte[] sb) {
    var n = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNfree));
    if (n == 0) return null;
    // LIFO pop from the top.
    n--;
    var zone = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSFree + n * 4));
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + n * 4), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNfree), n);

    // Classic s5fs spill: s_free[0] of an empty cache points at the next
    // indirect block holding the next NICFREE zones. The WORM writer never
    // emits a chain, but Remove may have spilled one when the cache filled.
    if (n == 0) {
      var chainHead = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSFree));
      if (chainHead != 0 && zone == chainHead) {
        // The block we just popped *is* the chain head — refill the cache
        // from its contents before reusing the block itself as a data zone.
        RefillFromIndirectBlock(image, sb, chainHead);
      }
    }
    return zone;
  }

  /// <summary>
  /// Pushes a freed zone onto the s_free[] cache. If the cache is full we
  /// spill it into the freed block (classic s5fs indirect free list): the
  /// freed block becomes the new chain head holding the current NICFREE
  /// cached entries, and the cache restarts with the chain head as its only
  /// entry. If even that fails we drop the entry — the next refill scan will
  /// recover it.
  /// </summary>
  private static void PushZone(Stream image, byte[] sb, uint zone) {
    var n = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNfree));
    if (n >= NicFree) {
      // Spill: write the current cache into `zone`, then restart the cache
      // with `zone` as its single entry (and the chain head).
      var indirect = new byte[BlockSize];
      // Layout of the indirect block: u16 count + u32 free[count] padded out
      // to NICFREE entries. This matches sysv/sysv-fs conventions for the
      // header-prefixed flavour; we keep `count = NicFree` always so the
      // next refill knows exactly how many addresses to pull.
      BinaryPrimitives.WriteUInt16LittleEndian(indirect.AsSpan(0), (ushort)NicFree);
      for (var i = 0; i < NicFree; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(
          indirect.AsSpan(2 + i * 4),
          BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSFree + i * 4)));
      WriteAt(image, (long)zone * BlockSize, indirect);
      // Reset cache with `zone` as the single live entry doubling as the
      // chain head, exactly as classic s5fs maintains it.
      for (var i = 0; i < NicFree; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + i * 4), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree), zone);
      BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNfree), 1);
      return;
    }
    BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + n * 4), zone);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNfree), (ushort)(n + 1));
  }

  // ── Cache refill from full scan ─────────────────────────────────────────

  /// <summary>
  /// Refills the inode cache by scanning the inode table for unused slots
  /// (<c>mode == 0</c>). The 1-based "reserved" inode 1 is skipped (Xenix
  /// reserves it for boot/bad-block bookkeeping). Stops at NICINOD entries.
  /// </summary>
  private static void RefillInodeCache(Stream image, byte[] sb) {
    var inodeTableBlocks = InodeTableBlocks(image);
    var totalInodesInTable = inodeTableBlocks * InodesPerBlock;
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNinode), 0);

    var inodeTableStart = 2L * BlockSize;
    // Walk inodes 2..N — inode 1 is reserved, root is 2 (always allocated,
    // so a zero-mode at inode 2 means a malformed image, which we ignore).
    for (var ino = 3; ino <= totalInodesInTable; ino++) {
      var off = inodeTableStart + (long)(ino - 1) * InodeSize;
      if (off + InodeSize > image.Length) break;
      image.Position = off;
      var modeBuf = new byte[2];
      image.ReadExactly(modeBuf);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(modeBuf);
      if (mode != 0) continue;
      PushInode(sb, (uint)ino);
      if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNinode)) >= NicInod) break;
    }
  }

  /// <summary>
  /// Refills the zone cache by walking every live inode's zone pointers to
  /// derive the set of in-use blocks, then pushing every other block at or
  /// above <c>firstDataBlock</c> onto the cache (up to NICFREE). Blocks past
  /// the image end are also eligible — pushing them tells <see cref="PopZone"/>
  /// it may extend the stream when writing.
  /// </summary>
  private static void RefillZoneCache(Stream image, byte[] sb) {
    var inodeTableBlocks = InodeTableBlocks(image);
    var firstDataBlock = (uint)(2 + inodeTableBlocks);

    var used = new HashSet<uint>();
    var totalInodesInTable = inodeTableBlocks * InodesPerBlock;
    var inodeTableStart = 2L * BlockSize;
    for (var ino = 1; ino <= totalInodesInTable; ino++) {
      var off = inodeTableStart + (long)(ino - 1) * InodeSize;
      if (off + InodeSize > image.Length) break;
      image.Position = off;
      var inodeBuf = new byte[InodeSize];
      image.ReadExactly(inodeBuf);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBuf);
      if (mode == 0) continue;
      for (var i = 0; i < 13; i++) {
        var z = Read24(inodeBuf.AsSpan(12 + i * 3));
        if (z != 0) used.Add(z);
      }
    }

    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNfree), 0);
    for (var i = 0; i < NicFree; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + i * 4), 0);

    // First, harvest unused blocks within the current image.
    var imageBlocks = (uint)((image.Length + BlockSize - 1) / BlockSize);
    var zone = firstDataBlock;
    while (zone < imageBlocks
        && BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNfree)) < NicFree) {
      if (!used.Contains(zone))
        PushZone(image, sb, zone);
      zone++;
    }
    // Then, seed the cache with addresses past the image end so Add can grow
    // the file. PushZone never spills here because the cache only refilled to
    // < NicFree above and we only top it up to NicFree.
    var tail = imageBlocks;
    while (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(OffSNfree)) < NicFree) {
      PushZone(image, sb, tail);
      tail++;
    }
  }

  /// <summary>
  /// Pulls NICFREE addresses out of an indirect free-list block (the classic
  /// s5fs spill format produced by <see cref="PushZone"/> on overflow) and
  /// rehydrates the cache from them.
  /// </summary>
  private static void RefillFromIndirectBlock(Stream image, byte[] sb, uint indirectBlock) {
    if (indirectBlock == 0) return;
    var off = (long)indirectBlock * BlockSize;
    if (off + BlockSize > image.Length) return;
    image.Position = off;
    var buf = new byte[BlockSize];
    image.ReadExactly(buf);
    var count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0));
    if (count > NicFree) count = NicFree;
    for (var i = 0; i < NicFree; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + i * 4), 0);
    for (var i = 0; i < count; i++) {
      var z = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(2 + i * 4));
      BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(OffSFree + i * 4), z);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(OffSNfree), count);
  }

  // ── Root directory dirent append ────────────────────────────────────────

  private static void AppendRootDirEntry(Stream image, byte[] sb, uint inodeNumber, string name) {
    var truncated = TruncateName(name);
    var rootInode = ReadInode(image, RootInode);
    var (mode, size, zones) = ParseInode(rootInode);
    if ((mode & 0xF000) != InodeModeDir)
      throw new InvalidDataException("Xenix R/W: root inode is not a directory.");

    // Walk the existing direct zones for a free slot.
    for (var zi = 0; zi < DirectZones; zi++) {
      var zone = zones[zi];
      if (zone == 0) {
        // No more zones — allocate a fresh one, link it onto the inode, and
        // place the entry at offset 0.
        var newZone = PopZone(image, sb)
          ?? throw new IOException("Xenix R/W: out of zones while growing the root directory.");
        zones[zi] = newZone;
        var dirData = new byte[BlockSize];
        WriteDirEntry(dirData, 0, inodeNumber, truncated);
        WriteAt(image, (long)newZone * BlockSize, dirData);
        UpdateInodeZones(image, RootInode, mode, size + BlockSize, zones);
        return;
      }
      var existing = ReadBlock(image, (long)zone * BlockSize);
      for (var off = 0; off + DirEntrySize <= existing.Length; off += DirEntrySize) {
        var ino = BinaryPrimitives.ReadUInt16LittleEndian(existing.AsSpan(off));
        if (ino != 0) continue;
        WriteDirEntry(existing, off, inodeNumber, truncated);
        WriteAt(image, (long)zone * BlockSize, existing);
        // Size grows only when we append past current EOF inside this zone.
        var endOfThisSlot = (long)zi * BlockSize + off + DirEntrySize;
        if (endOfThisSlot > size)
          UpdateInodeSize(image, RootInode, (uint)endOfThisSlot);
        return;
      }
    }

    throw new IOException("Xenix R/W: root directory full (10 direct zones exhausted).");
  }

  private static void WriteDirEntry(byte[] disk, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset), (ushort)inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    Array.Clear(disk, offset + 2, MaxNameLength);
    Array.Copy(nameBytes, 0, disk, offset + 2, copyLen);
  }

  // ── Superblock / inode read-write ───────────────────────────────────────

  private static byte[] ReadSuperblock(Stream image) {
    if (image.Length < SuperblockOffset + BlockSize)
      throw new InvalidDataException("Xenix R/W: image too small for superblock.");
    image.Position = SuperblockOffset;
    var sb = new byte[BlockSize];
    image.ReadExactly(sb);
    return sb;
  }

  private static void WriteSuperblock(Stream image, byte[] sb) =>
    WriteAt(image, SuperblockOffset, sb);

  private static void VerifyMagic(byte[] sb) {
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSMagic));
    if (magic != MagicXenix)
      throw new InvalidDataException(
        $"Xenix R/W: invalid magic 0x{magic:X8} (expected 0x{MagicXenix:X8}).");
    var type = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSType));
    if (type != 2)
      throw new NotSupportedException(
        $"Xenix R/W: only 1024-byte blocks (type=2) are supported; image type={type}.");
  }

  private static int InodeTableBlocks(Stream image) {
    // The WORM writer's layout is boot|sb|inode-table|data; we don't keep an
    // s_isize back-pointer (the WORM writer leaves it zero), so we recover
    // the inode-table extent by scanning for the first inode whose mode byte
    // would put it past plausibly-valid territory — i.e. just count blocks
    // until we reach a zone referenced by the root inode's zone[0].
    //
    // Simpler + robust: root is at inode 2 with mode 0x41ED, and its first
    // zone always points at the first block after the inode table. So we
    // read root.zones[0] and subtract 2.
    image.Position = 2L * BlockSize + (RootInode - 1) * InodeSize;
    var inodeBuf = new byte[InodeSize];
    image.ReadExactly(inodeBuf);
    var firstZone = Read24(inodeBuf.AsSpan(12));
    if (firstZone < 2)
      throw new InvalidDataException(
        $"Xenix R/W: root inode's first zone is {firstZone}; expected ≥ 2.");
    return (int)(firstZone - 2);
  }

  private static byte[] ReadInode(Stream image, uint inodeNumber) {
    var off = 2L * BlockSize + (long)(inodeNumber - 1) * InodeSize;
    if (off + InodeSize > image.Length)
      throw new InvalidDataException(
        $"Xenix R/W: inode {inodeNumber} past end of image (size={image.Length}).");
    image.Position = off;
    var buf = new byte[InodeSize];
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteInode(Stream image, uint inodeNumber, byte[] inode) =>
    WriteAt(image, 2L * BlockSize + (long)(inodeNumber - 1) * InodeSize, inode);

  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[13];
    for (var i = 0; i < 13; i++)
      zones[i] = Read24(inode.AsSpan(12 + i * 3));
    return (mode, size, zones);
  }

  private static void UpdateInodeSize(Stream image, uint inodeNumber, uint newSize) {
    var inode = ReadInode(image, inodeNumber);
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(8), newSize);
    WriteInode(image, inodeNumber, inode);
  }

  private static void UpdateInodeZones(Stream image, uint inodeNumber, ushort mode, uint size, uint[] zones) {
    var inode = ReadInode(image, inodeNumber);
    BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(8), size);
    for (var i = 0; i < Math.Min(zones.Length, 13); i++)
      Write24(inode.AsSpan(12 + i * 3), zones[i]);
    WriteInode(image, inodeNumber, inode);
  }

  // ── Block-level I/O ─────────────────────────────────────────────────────

  private static byte[] ReadBlock(Stream image, long offset) {
    if (offset + BlockSize > image.Length) {
      // Extend the image with a fresh zero block so the caller can edit it.
      image.Position = offset;
      var pad = new byte[BlockSize];
      image.Write(pad, 0, BlockSize);
      return pad;
    }
    image.Position = offset;
    var buf = new byte[BlockSize];
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteAt(Stream image, long offset, byte[] data) {
    if (offset > image.Length) {
      image.Position = image.Length;
      var gap = offset - image.Length;
      while (gap > 0) {
        var chunk = (int)Math.Min(gap, BlockSize);
        image.Write(new byte[chunk], 0, chunk);
        gap -= chunk;
      }
    }
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  // ── Bit helpers (24-bit zone addresses, name truncation, null-term strings) ──

  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static void Write24(Span<byte> dest, uint val) {
    dest[0] = (byte)(val & 0xFF);
    dest[1] = (byte)((val >> 8) & 0xFF);
    dest[2] = (byte)((val >> 16) & 0xFF);
  }

  private static string TruncateName(string name) {
    // SplitPath flattens nested paths to the leaf name; we do the same here
    // so callers can supply "etc/passwd" and we'll wire the entry as
    // "passwd" inside the root dir.
    var leaf = name;
    var slash = Math.Max(leaf.LastIndexOf('/'), leaf.LastIndexOf('\\'));
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    return leaf.Length > MaxNameLength ? leaf[..MaxNameLength] : leaf;
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }
}
