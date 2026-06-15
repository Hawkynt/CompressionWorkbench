#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.SysV;

/// <summary>
/// In-place AT&amp;T System V (s5fs) image modifier — performs random-access
/// I/O against an existing s5fs image emitted by <see cref="SysVWriter"/>
/// (1024-byte blocks, magic <c>0xFD187E20</c>, type code 2, 64-byte inodes,
/// 24-bit zone pointers, 16-byte dirents).
/// </summary>
/// <remarks>
/// <para>
/// Supports adds and removes of files in the <em>root directory only</em> —
/// path components that reach into subdirectories fall back to the
/// rebuild-from-scratch path so the modifier never has to re-walk the
/// directory tree. Per-file size is bounded at 10 direct zones (10 KB);
/// indirect blocks are out of scope (same as the WORM writer).
/// </para>
/// <para>
/// The free-block bookkeeping implements the classic AT&amp;T System V
/// "interleaving" free-list algorithm — the in-superblock cache is a
/// 50-entry array <c>s_free[0..49]</c> where slot 0 is the chain pointer
/// and slots 1..49 hold direct free-block numbers. <c>s_nfree</c> counts
/// the valid slots from the bottom (so an empty cache has <c>s_nfree=1</c>
/// with the chain pointer in slot 0; a full cache has <c>s_nfree=50</c>).
/// On allocation: pop <c>s_free[--s_nfree]</c>; if that empties the cache
/// (<c>s_nfree==1</c> remaining the chain pointer), read the block pointed
/// to by <c>s_free[0]</c> as a new cache group (laid out as
/// <c>u16 nfree; u8 pad[2]; u32 free[50]</c>), then return the just-consumed
/// chain block as the newly allocated block. If <c>s_free[0]==0</c>, the
/// volume is full. On free: push <c>s_free[s_nfree++] = block</c>; when the
/// cache fills (<c>s_nfree==50</c>), spill the current cache into the
/// about-to-be-freed block as a new chain group and reset the cache to
/// <c>nfree=1, s_free[0]=block</c>.
/// </para>
/// <para>
/// Inode allocation pops from the 100-entry <c>s_inode[]</c> cache; when
/// empty, the cache is refilled by re-scanning the inode table for any
/// zero-mode (unused) inode slot. There is no on-disk inode free list in
/// s5fs — the cache is its only persistent metadata, so a freed inode
/// number simply needs to land somewhere where the scan can rediscover it
/// (writing the inode bytes back as all-zero is enough).
/// </para>
/// </remarks>
public static class SysVModifier {

  private const int BlockSize = SysVWriter.BlockSize;
  private const int InodeSize = SysVWriter.InodeSize;
  private const int DirEntrySize = SysVWriter.DirEntrySize;
  private const int MaxNameLength = SysVWriter.MaxNameLength;
  private const int EntriesPerBlock = SysVWriter.EntriesPerBlock;
  private const int DirectZones = SysVWriter.DirectZones;
  private const int FreeCacheSize = SysVWriter.FreeCacheSize;
  private const int InodeCacheSize = SysVWriter.InodeCacheSize;
  private const int SuperblockOffset = 512;
  private const int FirstInodeBlock = SysVWriter.FirstInodeBlock;
  private const uint MagicSysV = SysVWriter.MagicSysV;
  private const ushort ModeRegularFile = SysVWriter.ModeRegularFile;
  private const ushort ModeDirectory = SysVWriter.ModeDirectory;
  private const int RootInode = 2;

  // ── Public API ────────────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) a flat-root file. Throws
  /// <see cref="NotSupportedException"/> for paths that contain a directory
  /// component — callers should route those through the rebuild path.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("SysV modifier handles flat-root files only; rebuild for nested paths.");
    if (name.Length > MaxNameLength)
      name = name[..MaxNameLength];

    var blocksNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
    if (blocksNeeded > DirectZones)
      throw new InvalidOperationException(
        $"SysV: file '{name}' exceeds the {DirectZones * BlockSize}-byte direct-zone cap.");

    if (!image.CanSeek || !image.CanWrite)
      throw new IOException("SysV modifier requires a seekable, writable stream.");

    var sb = ReadSuperblock(image);

    // If the file already exists, remove it first so we replace cleanly.
    if (TryFindRootEntry(image, sb, name, out var existingIno, out var existingSlot)) {
      FreeFileInode(image, ref sb, existingIno);
      ClearRootDirSlot(image, sb, existingSlot);
    }

    // Allocate data zones up front so we can roll back on capacity failure.
    var allocated = new List<uint>(blocksNeeded);
    try {
      for (var i = 0; i < blocksNeeded; i++)
        allocated.Add(AllocateBlock(image, ref sb));
    } catch {
      foreach (var blk in allocated) FreeBlock(image, ref sb, blk);
      WriteSuperblock(image, sb);
      throw;
    }

    // Write file bytes.
    for (var i = 0; i < blocksNeeded; i++) {
      var off = (long)allocated[i] * BlockSize;
      var src = i * BlockSize;
      var len = Math.Min(BlockSize, data.Length - src);
      var blockBuf = new byte[BlockSize];
      Buffer.BlockCopy(data, src, blockBuf, 0, len);
      WriteAt(image, off, blockBuf);
    }

    // Allocate an inode (may scan if cache empty).
    uint newInode;
    try {
      newInode = AllocateInode(image, ref sb);
    } catch {
      foreach (var blk in allocated) FreeBlock(image, ref sb, blk);
      WriteSuperblock(image, sb);
      throw;
    }

    // Write file inode.
    var zones = new uint[DirectZones];
    for (var i = 0; i < allocated.Count; i++) zones[i] = allocated[i];
    WriteFileInode(image, newInode, (uint)data.Length, zones);

    // Append directory entry. If no slot fits, allocate another root-dir
    // zone and bump the root inode's i_size.
    if (!AppendRootDirEntry(image, sb, newInode, name)) {
      // Free everything back out and surface a capacity error.
      foreach (var blk in allocated) FreeBlock(image, ref sb, blk);
      FreeFileInode(image, ref sb, newInode);
      WriteSuperblock(image, sb);
      throw new InvalidOperationException(
        "SysV: root directory has no free slot and is at the direct-zone ceiling.");
    }

    WriteSuperblock(image, sb);
  }

  /// <summary>
  /// Removes a flat-root file. Returns <c>true</c> if removed; <c>false</c>
  /// if the entry wasn't found. Nested paths and directories are silently
  /// skipped — those go through the rebuild path.
  /// </summary>
  public static bool RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    if (!image.CanSeek || !image.CanWrite)
      throw new IOException("SysV modifier requires a seekable, writable stream.");

    if (name.Contains('/') || name.Contains('\\'))
      return false;
    if (name.Length > MaxNameLength)
      name = name[..MaxNameLength];

    var sb = ReadSuperblock(image);
    if (!TryFindRootEntry(image, sb, name, out var inum, out var slotOffset)) return false;
    // Refuse to remove directories through this path.
    var inodeBytes = ReadInode(image, inum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeBytes);
    if ((mode & 0xF000) == 0x4000) return false;

    FreeFileInode(image, ref sb, inum);
    ClearRootDirSlot(image, sb, slotOffset);
    WriteSuperblock(image, sb);
    return true;
  }

  // ── Superblock ────────────────────────────────────────────────────────

  /// <summary>Mutable in-memory view of the superblock fields the modifier touches.</summary>
  private sealed class Superblock {
    public ushort IListBlocks;           // s_isize
    public uint TotalBlocks;             // s_fsize
    public ushort NFree;                 // s_nfree
    public uint[] Free = new uint[FreeCacheSize];  // s_free[50]
    public ushort NInode;                // s_ninode
    public ushort[] Inode = new ushort[InodeCacheSize]; // s_inode[100]
    public uint TFree;                   // s_tfree
    public ushort TInode;                // s_tinode
  }

  private static Superblock ReadSuperblock(Stream image) {
    var buf = new byte[BlockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(buf);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(504));
    if (magic != MagicSysV)
      throw new InvalidDataException($"SysV: invalid magic 0x{magic:X8} (expected 0x{MagicSysV:X8}).");
    var sb = new Superblock {
      // s_isize is the first data zone (FirstInodeBlock + ilist size); the ilist
      // size is s_isize - FirstInodeBlock.
      IListBlocks = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(buf) - FirstInodeBlock),
      TotalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(2)),
      NFree = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6)),
      NInode = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(216)),
      TFree = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(434)),
      TInode = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(438)),
    };
    for (var i = 0; i < FreeCacheSize; i++)
      sb.Free[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8 + i * 4));
    for (var i = 0; i < InodeCacheSize; i++)
      sb.Inode[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(218 + i * 2));
    return sb;
  }

  private static void WriteSuperblock(Stream image, Superblock sb) {
    var buf = new byte[BlockSize];
    image.Position = SuperblockOffset;
    image.ReadExactly(buf);  // preserve unchanged fields (timestamps, label, magic, type)
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)(sb.IListBlocks + FirstInodeBlock)); // s_isize = first data zone
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2), sb.TotalBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), sb.NFree);
    for (var i = 0; i < FreeCacheSize; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8 + i * 4), sb.Free[i]);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(216), sb.NInode);
    for (var i = 0; i < InodeCacheSize; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(218 + i * 2), sb.Inode[i]);
    // Touch s_time so kernels see the volume as updated.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(422),
      (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(434), sb.TFree);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(438), sb.TInode);
    image.Position = SuperblockOffset;
    image.Write(buf);
  }

  // ── Block allocation (chained free-block group cache) ─────────────────

  /// <summary>
  /// Pops the next free block from the cache, refilling from the on-disk
  /// chain group when the cache empties. Throws if the volume is full.
  /// </summary>
  private static uint AllocateBlock(Stream image, ref Superblock sb) {
    // The cache rule (V7/SYSV interleaving free list):
    //   - sFree[0] is the chain pointer; sFree[1..nfree-1] are direct frees.
    //   - Pop sFree[--nfree]. If that leaves nfree==0, we just consumed the
    //     chain pointer — read the chain block to refill, but the chain
    //     block itself becomes our allocated block.
    if (sb.NFree == 0)
      throw new IOException("SysV: free-block cache underflow (corrupt image).");

    sb.NFree--;
    var block = sb.Free[sb.NFree];

    if (sb.NFree == 0) {
      // We just popped the chain pointer (slot 0). Either refill from the
      // chained group or, if it was a terminator (0), bail out.
      if (block == 0)
        throw new IOException("SysV: out of free space (no chain follows in-line cache).");

      // Read the chain block: u16 nfree; u8 pad[2]; u32 free[50].
      var chainBuf = new byte[BlockSize];
      image.Position = (long)block * BlockSize;
      image.ReadExactly(chainBuf);

      var newNFree = BinaryPrimitives.ReadUInt16LittleEndian(chainBuf);
      if (newNFree == 0 || newNFree > FreeCacheSize)
        throw new InvalidDataException(
          $"SysV: invalid chain-block nfree={newNFree} in block {block}.");

      sb.NFree = newNFree;
      for (var i = 0; i < FreeCacheSize; i++)
        sb.Free[i] = BinaryPrimitives.ReadUInt32LittleEndian(chainBuf.AsSpan(4 + i * 4));
    }

    if (sb.TFree > 0) sb.TFree--;
    return block;
  }

  /// <summary>
  /// Pushes a freed block onto the cache, spilling the cache to a new
  /// on-disk chain group when full.
  /// </summary>
  private static void FreeBlock(Stream image, ref Superblock sb, uint block) {
    if (block == 0) return;
    if (sb.NFree >= FreeCacheSize) {
      // Cache full: spill the current cache into the about-to-be-freed
      // block, then reset the cache to (nfree=1, sFree[0]=block).
      var chainBuf = new byte[BlockSize];
      BinaryPrimitives.WriteUInt16LittleEndian(chainBuf, sb.NFree);
      // pad[2] stays zero
      for (var i = 0; i < FreeCacheSize; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(chainBuf.AsSpan(4 + i * 4), sb.Free[i]);
      image.Position = (long)block * BlockSize;
      image.Write(chainBuf);

      Array.Clear(sb.Free, 0, FreeCacheSize);
      sb.Free[0] = block;
      sb.NFree = 1;
      sb.TFree++;
      return;
    }
    sb.Free[sb.NFree] = block;
    sb.NFree++;
    sb.TFree++;
  }

  // ── Inode allocation ──────────────────────────────────────────────────

  /// <summary>
  /// Pops a free inode number from the cache; re-scans the inode table for
  /// any zero-mode slot when the cache empties.
  /// </summary>
  private static uint AllocateInode(Stream image, ref Superblock sb) {
    if (sb.NInode == 0)
      RefillInodeCache(image, sb);
    if (sb.NInode == 0)
      throw new IOException("SysV: no free inodes.");

    sb.NInode--;
    var inum = sb.Inode[sb.NInode];
    if (sb.TInode > 0) sb.TInode--;
    return inum;
  }

  /// <summary>
  /// Releases an inode by zeroing its on-disk slot and pushing the number
  /// back into the cache (or letting it be rediscovered by re-scan if the
  /// cache is full — s5fs has no on-disk inode free list).
  /// </summary>
  private static void FreeInode(Stream image, ref Superblock sb, uint inum) {
    if (inum == 0) return;
    // Zero the inode slot — that's what marks it free (re-scan looks for
    // di_mode == 0).
    image.Position = InodeTableOffset() + (long)(inum - 1) * InodeSize;
    image.Write(new byte[InodeSize]);

    if (sb.NInode < InodeCacheSize) {
      sb.Inode[sb.NInode] = (ushort)inum;
      sb.NInode++;
    }
    // If the cache is full we just let the re-scan find the inode again
    // next time the cache empties. s_tinode tracks total free inodes.
    sb.TInode = (ushort)Math.Min(sb.TInode + 1, ushort.MaxValue);
  }

  private static void RefillInodeCache(Stream image, Superblock sb) {
    var capacity = sb.IListBlocks * (BlockSize / InodeSize);
    // Inode 1 is reserved (bad-block inode); inode 2 is root. Don't touch
    // them. Scan 3..capacity for any zero-mode slot.
    var ilistOff = InodeTableOffset();
    sb.NInode = 0;
    for (var ino = 3; ino <= capacity && sb.NInode < InodeCacheSize; ino++) {
      image.Position = ilistOff + (long)(ino - 1) * InodeSize;
      var modeBuf = new byte[2];
      image.ReadExactly(modeBuf);
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(modeBuf);
      if (mode != 0) continue;
      sb.Inode[sb.NInode] = (ushort)ino;
      sb.NInode++;
    }
  }

  private static void FreeFileInode(Stream image, ref Superblock sb, uint inum) {
    // Free every zone pointed at by the file inode, then zero the inode.
    var inode = ReadInode(image, inum);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    if ((mode & 0xF000) == 0x4000)
      throw new InvalidOperationException("SysV: refusing to free a directory inode via FreeFileInode.");

    // 10 direct zone pointers — modifier only ever allocates direct zones.
    for (var i = 0; i < DirectZones; i++) {
      var zone = Read24(inode.AsSpan(12 + i * 3));
      if (zone == 0) break;
      // Wipe the data block before returning it to the free list (matches
      // the IArchiveModifiable.Remove contract: "wipe all on-disk traces").
      var wipe = new byte[BlockSize];
      WriteAt(image, (long)zone * BlockSize, wipe);
      FreeBlock(image, ref sb, zone);
    }

    FreeInode(image, ref sb, inum);
  }

  // ── Root directory mutation ───────────────────────────────────────────

  private static bool TryFindRootEntry(Stream image, Superblock sb, string name,
      out uint inum, out long slotOffset) {
    inum = 0;
    slotOffset = -1;

    var rootInodeBytes = ReadInode(image, RootInode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(rootInodeBytes.AsSpan(8));
    for (var z = 0; z < DirectZones; z++) {
      var zone = Read24(rootInodeBytes.AsSpan(12 + z * 3));
      if (zone == 0) break;
      var blockOff = (long)zone * BlockSize;
      var bytesAvailable = Math.Min(BlockSize, (int)Math.Max(0, size - (long)z * BlockSize));
      for (var off = 0; off + DirEntrySize <= bytesAvailable; off += DirEntrySize) {
        var entryOff = blockOff + off;
        image.Position = entryOff;
        var entry = new byte[DirEntrySize];
        image.ReadExactly(entry);
        var entryIno = BinaryPrimitives.ReadUInt16LittleEndian(entry);
        if (entryIno == 0) continue;
        var entryName = ReadNullTermString(entry, 2, MaxNameLength);
        if (string.Equals(entryName, ".", StringComparison.Ordinal) ||
            string.Equals(entryName, "..", StringComparison.Ordinal))
          continue;
        if (!string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase)) continue;
        inum = entryIno;
        slotOffset = entryOff;
        return true;
      }
    }
    return false;
  }

  private static void ClearRootDirSlot(Stream image, Superblock sb, long slotOffset) {
    image.Position = slotOffset;
    image.Write(new byte[DirEntrySize]);
  }

  /// <summary>
  /// Appends a (inum, name) record to the root directory, allocating a new
  /// directory block from the free-block cache when needed. Returns
  /// <c>false</c> only when the root inode is at its 10-direct-zone cap
  /// and no slot is free.
  /// </summary>
  private static bool AppendRootDirEntry(Stream image, Superblock sb, uint inum, string name) {
    var rootInodeBytes = ReadInode(image, RootInode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(rootInodeBytes.AsSpan(8));

    // First, try to reclaim a zero-inum slot inside an existing zone (left
    // over by an earlier ClearRootDirSlot).
    for (var z = 0; z < DirectZones; z++) {
      var zone = Read24(rootInodeBytes.AsSpan(12 + z * 3));
      if (zone == 0) break;
      var blockOff = (long)zone * BlockSize;
      var bytesAvailable = Math.Min(BlockSize, (int)Math.Max(0, size - (long)z * BlockSize));
      for (var off = 0; off + DirEntrySize <= bytesAvailable; off += DirEntrySize) {
        image.Position = blockOff + off;
        var entry = new byte[DirEntrySize];
        image.ReadExactly(entry);
        var entryIno = BinaryPrimitives.ReadUInt16LittleEndian(entry);
        if (entryIno != 0) continue;
        WriteDirEntry(image, blockOff + off, inum, name);
        return true;
      }
    }

    // No free slot in existing zones; try to extend the last allocated
    // zone (if its tail still has room within bytesAvailable < BlockSize).
    var lastZoneIndex = -1;
    for (var z = 0; z < DirectZones; z++) {
      var zone = Read24(rootInodeBytes.AsSpan(12 + z * 3));
      if (zone == 0) break;
      lastZoneIndex = z;
    }
    if (lastZoneIndex >= 0) {
      var lastZone = Read24(rootInodeBytes.AsSpan(12 + lastZoneIndex * 3));
      var bytesInLastZone = (int)(size - (long)lastZoneIndex * BlockSize);
      if (bytesInLastZone < 0) bytesInLastZone = 0;
      if (bytesInLastZone + DirEntrySize <= BlockSize) {
        var slotOff = (long)lastZone * BlockSize + bytesInLastZone;
        WriteDirEntry(image, slotOff, inum, name);
        SetRootInodeSize(image, size + DirEntrySize);
        return true;
      }
    }

    // Allocate a fresh directory zone and bump i_size.
    var nextSlot = lastZoneIndex + 1;
    if (nextSlot >= DirectZones)
      return false;
    uint newZone;
    try {
      newZone = AllocateBlock(image, ref sb);
    } catch (IOException) {
      return false;
    }
    // Zero the new zone.
    WriteAt(image, (long)newZone * BlockSize, new byte[BlockSize]);
    WriteDirEntry(image, (long)newZone * BlockSize, inum, name);

    // Patch root inode di_addr[nextSlot] and i_size.
    var inodeOff = InodeTableOffset() + (long)(RootInode - 1) * InodeSize;
    image.Position = inodeOff + 12 + nextSlot * 3;
    var ptr = new byte[3];
    Write24(ptr, newZone);
    image.Write(ptr);
    SetRootInodeSize(image, (uint)(nextSlot * BlockSize + DirEntrySize));
    return true;
  }

  private static void SetRootInodeSize(Stream image, uint newSize) {
    var inodeOff = InodeTableOffset() + (long)(RootInode - 1) * InodeSize;
    image.Position = inodeOff + 8;
    var buf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, newSize);
    image.Write(buf);
  }

  // ── Inode I/O ─────────────────────────────────────────────────────────

  private static long InodeTableOffset() => (long)FirstInodeBlock * BlockSize;

  private static byte[] ReadInode(Stream image, uint inum) {
    var buf = new byte[InodeSize];
    image.Position = InodeTableOffset() + (long)(inum - 1) * InodeSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteFileInode(Stream image, uint inum, uint size, uint[] zones) {
    var buf = new byte[InodeSize];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), ModeRegularFile);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), size);
    for (var i = 0; i < Math.Min(zones.Length, 13); i++)
      Write24(buf.AsSpan(12 + i * 3, 3), zones[i]);
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(52), now);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(56), now);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60), now);
    image.Position = InodeTableOffset() + (long)(inum - 1) * InodeSize;
    image.Write(buf);
  }

  // ── Bit-twiddling helpers ─────────────────────────────────────────────

  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static void Write24(Span<byte> d, uint v) {
    d[0] = (byte)(v & 0xFF);
    d[1] = (byte)((v >> 8) & 0xFF);
    d[2] = (byte)((v >> 16) & 0xFF);
  }

  private static void WriteAt(Stream image, long offset, byte[] data) {
    image.Position = offset;
    image.Write(data);
  }

  private static void WriteDirEntry(Stream image, long offset, uint inum, string name) {
    var entry = new byte[DirEntrySize];
    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)inum);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    Buffer.BlockCopy(nameBytes, 0, entry, 2, copyLen);
    image.Position = offset;
    image.Write(entry);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  // ── Test hooks ────────────────────────────────────────────────────────

  /// <summary>
  /// Reads (s_nfree, total-free-blocks) from the image. Used by tests to
  /// verify cache-exhaustion bookkeeping.
  /// </summary>
  public static (ushort NFree, uint TFree) ReadFreeStats(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var sb = ReadSuperblock(image);
    return (sb.NFree, sb.TFree);
  }

  /// <summary>
  /// Reads (s_ninode, total-free-inodes) from the image. Used by tests to
  /// verify inode-cache exhaustion bookkeeping.
  /// </summary>
  public static (ushort NInode, ushort TInode) ReadInodeStats(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var sb = ReadSuperblock(image);
    return (sb.NInode, sb.TInode);
  }
}
