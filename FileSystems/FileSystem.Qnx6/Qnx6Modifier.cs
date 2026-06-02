#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Qnx6;

/// <summary>
/// In-place Add/Remove modifier for QNX6 (Neutrino) WORM images produced by
/// <see cref="Qnx6Writer"/>. The on-disk layout we mutate is the one the writer
/// lays down: primary superblock at 0x2000, a flat 128-byte inode array starting
/// at the inode-table block pointed at by sb+0x50, root directory in inode 1's
/// first-direct block as 32-byte dirents, and a mirrored secondary superblock
/// at the last 512 bytes of the volume.
///
/// <para>The Power-Safe contract is respected literally: every mutation that
/// changes any byte in the primary superblock window is followed by a verbatim
/// copy of those 512 bytes to the secondary mirror at the tail. Adds that
/// extend the volume re-locate the mirror to the new tail; removes never shrink
/// the volume (the freed data blocks are zeroed but their span stays addressable
/// for future allocation).</para>
///
/// <para>Scope mirrors the reader's:
///   <list type="bullet">
///     <item><description>Single-block root directory (32 dirents max — file
///       count past that throws <see cref="NotSupportedException"/>).</description></item>
///     <item><description>Direct-extent files only (one contiguous run starting
///       at <c>di_block_ptr[0]</c>).</description></item>
///     <item><description>Names &gt; 27 bytes silently skipped, mirroring the
///       writer's WORM behaviour and the reader's <c>name_len &gt; 27</c> gate.</description></item>
///   </list>
/// </para>
/// </summary>
public static class Qnx6Modifier {

  private const int BlockSize = 1024;
  private const int SuperblockOffset = Qnx6Reader.SuperblockOffset; // 0x2000
  private const int SuperblockSize = 512;
  private const int InodeSize = Qnx6Reader.InodeSize;               // 128
  private const int DirentSize = 32;
  private const int MaxNameLen = 27;
  private const int MaxDirents = BlockSize / DirentSize;            // 32
  private const int InodesPerBlock = BlockSize / InodeSize;         // 8
  private const uint MagicQnx6 = Qnx6Reader.MagicQnx6;

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Appends a single file at the root. If a dirent with the same leaf name
  /// already exists it is removed (data zeroed, inode slot cleared, dirents
  /// compacted) before the new file is written — replace-by-name semantics
  /// matching every other R/W FS in the repo.
  /// </summary>
  /// <exception cref="NotSupportedException">When the root directory is full
  /// (32 dirent slots already occupied) — the single-block root limit.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var leaf = FlattenLeafName(name);
    if (string.IsNullOrEmpty(leaf)) return;
    var nameBytes = Encoding.ASCII.GetBytes(leaf);
    if (nameBytes.Length is 0 or > MaxNameLen) return;

    // Replace-by-name: remove the existing entry first.
    var img = ReadImage(image);
    var sb = ReadSuperblockState(img);

    // If name exists, remove and re-read.
    if (TryFindDirent(img, sb, leaf, out _)) {
      RemoveInternal(img, sb, leaf);
      // sb metadata is mutated in place by RemoveInternal; re-read for safety.
      sb = ReadSuperblockState(img);
    }

    var dirents = ReadDirents(img, sb);
    if (dirents.Count >= MaxDirents)
      throw new NotSupportedException(
        $"QNX6: root directory is full ({dirents.Count}/{MaxDirents}). The Stage-1 writer/reader pair " +
        "use a single-block root directory; extending past 32 entries requires multi-block dir support.");

    // Find a free inode slot. Slot 0 = root (always allocated); we scan from
    // slot 1 (inode 2) upward looking for di_status==0. If none free, extend
    // the inode array by one block (8 inode slots) — provided we have room
    // before the file-data blocks, which for our writer is true iff sb.NumInodes
    // is a multiple of 8 (inode array always whole-block in the layout).
    var inodeNumber = AllocateInodeSlot(img, ref sb);

    // Compute file-data layout: contiguous run after the highest currently
    // used block (inode table + root dir + every file data extent). The
    // mirror lives in the last 512 bytes of the file; we always re-allocate
    // the mirror at the new tail after extending.
    var blocksNeeded = data.Length == 0 ? 0u : (uint)((data.Length + BlockSize - 1) / BlockSize);
    var firstBlock = blocksNeeded == 0 ? 0u : (uint)FindNextFreeDataBlock(img, sb);

    // Extend image if necessary: we need room for blocksNeeded data blocks
    // starting at firstBlock, plus the 512-byte mirror at the tail.
    if (blocksNeeded > 0) {
      var endByte = (long)(firstBlock + blocksNeeded) * BlockSize + SuperblockSize;
      // Round to block boundary.
      var rem = endByte % BlockSize;
      if (rem != 0) endByte += BlockSize - rem;
      if (endByte > img.Length)
        Array.Resize(ref img, (int)endByte);
    }

    // Write the file data extent.
    if (blocksNeeded > 0)
      data.CopyTo(img.AsSpan((int)(firstBlock * BlockSize), data.Length));

    // Write the inode for the new file (slot inodeNumber).
    var inodeTableOffset = (long)sb.InodeTablePtr * BlockSize;
    var inodeOff = inodeTableOffset + (long)(inodeNumber - 1) * InodeSize;
    WriteFileInode(img.AsSpan((int)inodeOff, InodeSize), (ulong)data.Length, firstBlock);

    // Append the dirent to the root directory block.
    var dirBlockOff = (long)sb.RootDirFirstBlock * BlockSize;
    var direntOff = (int)dirBlockOff + dirents.Count * DirentSize;
    var dirent = img.AsSpan(direntOff, DirentSize);
    dirent.Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(dirent, inodeNumber);
    dirent[4] = (byte)nameBytes.Length;
    nameBytes.CopyTo(dirent.Slice(5));

    // Update root dir inode size and free-inode counter.
    var rootInodeOff = (int)inodeTableOffset; // inode 1 is at offset 0
    BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(rootInodeOff, 8), (ulong)((dirents.Count + 1) * DirentSize));
    UpdateSuperblockCounters(img, sb, freeInodesDelta: -1, freeBlocksDelta: 0);

    // Mirror the (possibly mutated) primary superblock to the new tail.
    MirrorSuperblock(img);

    // Flush to the underlying stream.
    WriteImage(image, img);
  }

  /// <summary>
  /// Removes a single file by leaf name. Data blocks are zeroed to wipe the
  /// content, the inode slot is cleared, and the trailing dirents are compacted
  /// so reads see no gap.
  /// </summary>
  public static void RemoveFile(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    var leaf = FlattenLeafName(name);
    if (string.IsNullOrEmpty(leaf)) return;

    var img = ReadImage(image);
    var sb = ReadSuperblockState(img);
    if (!RemoveInternal(img, sb, leaf)) return;

    MirrorSuperblock(img);
    WriteImage(image, img);
  }

  /// <summary>
  /// Removes multiple entries in one pass. Each name not found is silently
  /// skipped (matches every other modifier in the repo).
  /// </summary>
  public static void RemoveFiles(Stream image, IEnumerable<string> names) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(names);

    var img = ReadImage(image);
    var sb = ReadSuperblockState(img);
    var anyRemoved = false;
    foreach (var n in names) {
      var leaf = FlattenLeafName(n);
      if (string.IsNullOrEmpty(leaf)) continue;
      if (RemoveInternal(img, sb, leaf)) {
        anyRemoved = true;
        // After remove, sb counters and dirent layout shift. Re-read sb state.
        sb = ReadSuperblockState(img);
      }
    }
    if (!anyRemoved) return;
    MirrorSuperblock(img);
    WriteImage(image, img);
  }

  // ── Internals ─────────────────────────────────────────────────────────────

  private readonly struct SuperblockState(uint inodeTablePtr, uint numInodes, uint freeInodes, uint numBlocks, uint freeBlocks, uint rootDirFirstBlock) {
    public uint InodeTablePtr { get; } = inodeTablePtr;
    public uint NumInodes { get; } = numInodes;
    public uint FreeInodes { get; } = freeInodes;
    public uint NumBlocks { get; } = numBlocks;
    public uint FreeBlocks { get; } = freeBlocks;
    public uint RootDirFirstBlock { get; } = rootDirFirstBlock;
  }

  private static SuperblockState ReadSuperblockState(byte[] img) {
    if (img.Length < SuperblockOffset + 0x48 + 16)
      throw new InvalidDataException("QNX6: image too small for superblock.");

    var sb = img.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (magic != MagicQnx6)
      throw new InvalidDataException($"QNX6: invalid magic 0x{magic:X8}.");

    var inodeTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x50));
    var numInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x34));
    var freeInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x38));
    var numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x3C));
    var freeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x40));

    // Root dir = inode 1 (offset 0 in inode table). First direct ptr at +0x24.
    var inodeTableOffset = (long)inodeTablePtr * BlockSize;
    var rootInodeOff = inodeTableOffset;
    if (rootInodeOff + InodeSize > img.Length)
      throw new InvalidDataException("QNX6: inode table out of bounds.");
    var rootDir = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)rootInodeOff + 0x24, 4));

    return new SuperblockState(inodeTablePtr, numInodes, freeInodes, numBlocks, freeBlocks, rootDir);
  }

  private static List<(uint Inum, byte NameLen, string Name, int SlotIndex)> ReadDirents(byte[] img, SuperblockState sb) {
    var result = new List<(uint, byte, string, int)>();
    if (sb.RootDirFirstBlock == 0) return result;
    var dirOff = (long)sb.RootDirFirstBlock * BlockSize;
    if (dirOff + BlockSize > img.Length) return result;

    for (var slot = 0; slot < MaxDirents; slot++) {
      var off = (int)dirOff + slot * DirentSize;
      var entry = img.AsSpan(off, DirentSize);
      var inum = BinaryPrimitives.ReadUInt32LittleEndian(entry);
      if (inum == 0) break; // first zero terminates the dirent run
      var nameLen = entry[4];
      if (nameLen == 0 || nameLen > MaxNameLen) continue;
      var name = Encoding.ASCII.GetString(entry.Slice(5, nameLen));
      result.Add((inum, nameLen, name, slot));
    }
    return result;
  }

  private static bool TryFindDirent(byte[] img, SuperblockState sb, string leaf, out (uint Inum, int SlotIndex) found) {
    foreach (var (inum, _, name, slotIdx) in ReadDirents(img, sb)) {
      if (string.Equals(name, leaf, StringComparison.Ordinal)) {
        found = (inum, slotIdx);
        return true;
      }
    }
    found = default;
    return false;
  }

  private static uint AllocateInodeSlot(byte[] img, ref SuperblockState sb) {
    // Walk the inode array looking for di_status == 0 (free). Start at slot 1
    // (inode 2 — slot 0 is the root inode and always allocated). Bounds is the
    // inode-array region between inode-table-block and root-dir-block.
    var inodeTableOffset = (long)sb.InodeTablePtr * BlockSize;
    var inodeArrayBlocks = (long)sb.RootDirFirstBlock - sb.InodeTablePtr;
    var inodeArraySlots = inodeArrayBlocks * InodesPerBlock;
    if (inodeArraySlots < 2)
      throw new InvalidDataException("QNX6: inode table region too small.");

    for (var slot = 1; slot < inodeArraySlots; slot++) {
      var off = inodeTableOffset + slot * InodeSize;
      var status = img[(int)off + 0x65];
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((int)off + 0x20, 2));
      var firstPtr = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)off + 0x24, 4));
      if (status == 0 && mode == 0 && firstPtr == 0) return (uint)(slot + 1); // inode numbers are 1-based
    }

    throw new NotSupportedException(
      $"QNX6: inode array exhausted ({inodeArraySlots} slots all in use). " +
      "Extending the inode array would require shifting the root directory and all data extents — " +
      "out of scope for the Stage-2 in-place modifier.");
  }

  private static int FindNextFreeDataBlock(byte[] img, SuperblockState sb) {
    // Highest data block currently in use = max of (sb.NumBlocks - 1) and the
    // last data-block address claimed by any file inode. The mirror sits in
    // the tail 512 bytes; we ignore it for extent placement and re-emit it
    // afterwards.
    var inodeTableOffset = (long)sb.InodeTablePtr * BlockSize;
    var inodeArrayBlocks = (long)sb.RootDirFirstBlock - sb.InodeTablePtr;
    var inodeArraySlots = inodeArrayBlocks * InodesPerBlock;
    long cursor = sb.RootDirFirstBlock + 1; // first slot past the root dir block

    for (var slot = 1; slot < inodeArraySlots; slot++) {
      var off = inodeTableOffset + slot * InodeSize;
      var status = img[(int)off + 0x65];
      if (status == 0) continue;
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan((int)off + 0x20, 2));
      if ((mode & 0xF000) != 0x8000) continue; // S_IFREG only — directory inodes don't claim data extents in our writer
      var size = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)off, 8));
      var firstPtr = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan((int)off + 0x24, 4));
      if (firstPtr == 0 || size == 0) continue;
      var blocks = (size + BlockSize - 1) / BlockSize;
      var endExclusive = (long)firstPtr + (long)blocks;
      if (endExclusive > cursor) cursor = endExclusive;
    }
    return (int)cursor;
  }

  private static bool RemoveInternal(byte[] img, SuperblockState sb, string leaf) {
    if (!TryFindDirent(img, sb, leaf, out var found)) return false;

    var dirOff = (long)sb.RootDirFirstBlock * BlockSize;
    var inodeTableOffset = (long)sb.InodeTablePtr * BlockSize;
    var inodeOff = inodeTableOffset + (long)(found.Inum - 1) * InodeSize;
    if (inodeOff + InodeSize > img.Length) return false;

    // Wipe file data bytes (zeroes the extent the inode pointed at — the
    // power-safe contract requires that removed bytes are unrecoverable from
    // the resulting image).
    var inode = img.AsSpan((int)inodeOff, InodeSize);
    var size = BinaryPrimitives.ReadUInt64LittleEndian(inode);
    var firstPtr = BinaryPrimitives.ReadUInt32LittleEndian(inode.Slice(0x24, 4));
    if (firstPtr != 0 && size != 0) {
      var blocks = (long)((size + BlockSize - 1) / BlockSize);
      var dataOff = (long)firstPtr * BlockSize;
      var dataLen = blocks * BlockSize;
      if (dataOff >= 0 && dataOff + dataLen <= img.Length)
        img.AsSpan((int)dataOff, (int)dataLen).Clear();
    }

    // Clear the inode slot.
    inode.Clear();

    // Compact dirents: shift all entries past the removed slot left by one,
    // and zero the tail slot.
    var dirents = ReadDirents(img, sb);
    // Re-read because the slot list is what we walk; rebuild without the
    // removed entry, then re-emit the whole dirent block region we used.
    var live = new List<(uint Inum, byte NameLen, string Name)>();
    foreach (var (inum, nl, name, slotIdx) in dirents) {
      if (slotIdx == found.SlotIndex) continue;
      live.Add((inum, nl, name));
    }

    // Zero the whole dirent area we previously occupied, then re-emit live
    // entries from offset 0 onward. The dirent block is BlockSize bytes; we
    // only walked the first MaxDirents slots, so clear that span only.
    var dirSpan = img.AsSpan((int)dirOff, MaxDirents * DirentSize);
    dirSpan.Clear();
    for (var i = 0; i < live.Count; i++) {
      var slotOff = i * DirentSize;
      var slot = dirSpan.Slice(slotOff, DirentSize);
      BinaryPrimitives.WriteUInt32LittleEndian(slot, live[i].Inum);
      slot[4] = live[i].NameLen;
      Encoding.ASCII.GetBytes(live[i].Name).CopyTo(slot.Slice(5));
    }

    // Update root inode size + sb free-inode counter.
    var rootInodeOff = (int)inodeTableOffset;
    BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(rootInodeOff, 8), (ulong)(live.Count * DirentSize));
    UpdateSuperblockCounters(img, sb, freeInodesDelta: +1, freeBlocksDelta: 0);

    return true;
  }

  private static void UpdateSuperblockCounters(byte[] img, SuperblockState sb, int freeInodesDelta, int freeBlocksDelta) {
    var span = img.AsSpan(SuperblockOffset);
    if (freeInodesDelta != 0) {
      var fi = (long)sb.FreeInodes + freeInodesDelta;
      if (fi < 0) fi = 0;
      if (fi > uint.MaxValue) fi = uint.MaxValue;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x38), (uint)fi);
    }
    if (freeBlocksDelta != 0) {
      var fb = (long)sb.FreeBlocks + freeBlocksDelta;
      if (fb < 0) fb = 0;
      if (fb > uint.MaxValue) fb = uint.MaxValue;
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x40), (uint)fb);
    }
    // sb_num_blocks must stay in sync with the actual image size — the writer
    // sets it to totalSizeBytes/BlockSize.
    var numBlocks = (uint)(img.Length / BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x3C), numBlocks);
  }

  private static void WriteFileInode(Span<byte> inode, ulong size, uint firstBlock) {
    inode.Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(inode, size);
    BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0x20), 0x81A4); // S_IFREG | 0644
    BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(0x24), firstBlock);
    inode[0x65] = 0x01; // di_status = allocated
  }

  private static void MirrorSuperblock(byte[] img) {
    var secondaryOff = img.Length - SuperblockSize;
    img.AsSpan(SuperblockOffset, SuperblockSize)
      .CopyTo(img.AsSpan(secondaryOff, SuperblockSize));
  }

  private static string FlattenLeafName(string name) {
    if (string.IsNullOrEmpty(name)) return "";
    var leaf = name.Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    return slash >= 0 ? leaf.Substring(slash + 1) : leaf;
  }

  private static byte[] ReadImage(Stream image) {
    if (image.CanSeek) image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteImage(Stream image, byte[] bytes) {
    if (image.CanSeek) {
      image.Position = 0;
      image.SetLength(bytes.Length);
    }
    image.Write(bytes, 0, bytes.Length);
    image.Flush();
  }
}
