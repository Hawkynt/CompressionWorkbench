#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Qnx4;

/// <summary>
/// In-place modifier for QNX4 file-system images. Performs Add / Remove
/// against the existing root-directory cluster (LBA 1-4) and updates the
/// on-disk <c>.bitmap</c> (LBA 5) in-place — no full image rebuild, no
/// snapshot copy.
///
/// <para>The companion <see cref="Qnx4Writer"/> still serves the WORM
/// "build a fresh image from a file list" path; this class handles the
/// "mutate an existing image" path that <c>IArchiveModifiable</c> exposes.</para>
///
/// <para>Layout reminders for the image shape produced by <see cref="Qnx4Writer"/>:
/// <list type="bullet">
///   <item>Block 0 (LBA 0): boot block — zeroed.</item>
///   <item>Blocks 1-4: root directory cluster, 32 × 64-byte inode entries.
///         Entry 0 = root self-reference (status=0x09), entries 1-2 = system files
///         (.bitmap / .inodes, status=0x01), entries 3..31 = user files (status=0x01)
///         or free (status=0x00).</item>
///   <item>Block 5: <c>.bitmap</c> — 1 bit per block, LSB-first within each byte.</item>
///   <item>Block 6: <c>.inodes</c> — overflow inode storage (unused by our writer).</item>
///   <item>Block 7..: user file data, each file has a single contiguous extent
///         rounded up to whole 512-byte blocks.</item>
/// </list></para>
///
/// <para>Scope match with WORM: the modifier respects the same 29-user-file
/// capacity (32 root slots minus 3 system entries). Add throws
/// <see cref="NotSupportedException"/> with a "root cluster full" message
/// when the root cluster has no free entry slot — the same boundary as
/// <see cref="Qnx4Writer"/>'s capacity guard.</para>
///
/// <para>Spec source: <c>linux/fs/qnx4/{qnx4.h,inode.c,dir.c,namei.c}</c>.</para>
/// </summary>
public static class Qnx4Modifier {

  private const int BlockSize = Qnx4Reader.BlockSize;
  private const int InodeSize = Qnx4Reader.InodeSize;
  private const int InodesPerBlock = BlockSize / InodeSize; // 8
  private const int RootDirBlocks = 4;
  private const int MaxShortName = 16;

  // Reserved layout (LBA):
  private const uint RootDirStart = 1; // blocks 1..4
  private const uint BitmapBlock = 5;
  private const uint InodesBlock = 6;
  private const uint FirstDataBlock = 7;

  // QNX4 inode status flags.
  private const byte FileUsed = 0x01;
  private const byte FileLink = 0x08;
  private const byte FileBusy = 0x04;

  // di_mode bits.
  private const ushort SIfreg = 0x8000;
  private const ushort PermFile = 0x01A4; // 0644

  // ── Public API ──────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a single file to the existing image. Allocates a contiguous extent
  /// from the <c>.bitmap</c>, writes the file data into it, then writes a new
  /// 64-byte inode into the first free root-cluster slot (entries 3..31).
  /// <para>If a file with the same name already exists, it is removed first
  /// (its extent freed and dirent cleared) so the new entry replaces it
  /// cleanly.</para>
  /// </summary>
  /// <exception cref="NotSupportedException">Root cluster full (no free entry
  /// slot in entries 3..31).</exception>
  /// <exception cref="IOException">Disk full (no contiguous free extent of
  /// the required size).</exception>
  /// <exception cref="ArgumentException">File name resolves to empty after
  /// path flattening, or to "." / "..".</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new IOException("QNX4 modifier requires a readable, writable, seekable stream.");

    var leaf = TruncateShortName(name);
    if (string.IsNullOrEmpty(leaf))
      throw new ArgumentException("QNX4: file name resolves to empty after flattening.", nameof(name));

    // Replacement semantics: remove the old entry first so we recycle its
    // extent + slot. Matches MfsModifier.AddFile pattern.
    RemoveFile(image, leaf, wipeData: true);

    // Snapshot the root cluster + bitmap so we can plan the allocation.
    var rootCluster = ReadRootCluster(image);
    var bitmap = ReadBitmap(image);

    // Find the first free root-cluster entry slot (entries 3..31 — entries 0..2
    // are reserved for root-self + .bitmap + .inodes).
    var slot = FindFreeRootSlot(rootCluster);
    if (slot < 0)
      throw new NotSupportedException(
        $"QNX4: root cluster full (max {(InodesPerBlock * RootDirBlocks) - 3} user files in flat root). " +
        "Subdirectory emission is out of scope for the current R/W writer.");

    // Allocate a contiguous extent — zero-byte files still reserve one block
    // so the reader's extent walker has something to follow.
    var blocksNeeded = data.Length == 0 ? 1u : (uint)((data.Length + BlockSize - 1) / BlockSize);

    var startBlock = FindFreeContiguousExtent(bitmap, image.Length, blocksNeeded)
      ?? throw new IOException(
        $"QNX4: disk full — cannot allocate {blocksNeeded} contiguous block(s) for '{leaf}'.");

    // Grow the underlying stream if our allocation overruns the current length.
    var requiredLen = (long)(startBlock + blocksNeeded) * BlockSize;
    if (image.Length < requiredLen)
      image.SetLength(requiredLen);

    // Write the file data into the allocated extent + zero the tail.
    image.Position = (long)startBlock * BlockSize;
    if (data.Length > 0)
      image.Write(data);
    var tail = (int)((long)blocksNeeded * BlockSize - data.Length);
    if (tail > 0)
      image.Write(new byte[tail]);

    // Mark the extent allocated in the bitmap.
    for (var b = 0u; b < blocksNeeded; b++)
      SetBitmapBit(bitmap, startBlock + b, true);

    // Build the new inode entry and write it into the root cluster slot.
    WriteInode(image, slot, leaf, (uint)data.Length, startBlock, blocksNeeded);

    // Refresh root-self di_size = entry count × 64 (3 system + visible user files).
    UpdateRootSelfSize(image, rootCluster, addedSlot: slot);

    // Flush the bitmap back to LBA 5.
    WriteBitmap(image, bitmap);
  }

  /// <summary>
  /// Removes the named file from the existing image. Locates the dirent in
  /// the root cluster, frees its extent in <c>.bitmap</c>, wipes the data
  /// blocks (when <paramref name="wipeData"/> is true), and clears the inode
  /// status byte to zero (marking the slot free).
  /// </summary>
  /// <returns>True if the file was found and removed, false otherwise.</returns>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new IOException("QNX4 modifier requires a readable, writable, seekable stream.");

    var leaf = TruncateShortName(name);
    if (string.IsNullOrEmpty(leaf)) return false;

    var rootCluster = ReadRootCluster(image);

    // Walk user slots (entries 3..31) looking for the matching name.
    for (var slot = 3; slot < InodesPerBlock * RootDirBlocks; slot++) {
      var status = rootCluster[SlotOffset(slot) + 0x3D];
      if (!IsLiveStatus(status)) continue;
      var entryName = ReadInodeName(rootCluster.AsSpan(SlotOffset(slot), MaxShortName));
      if (!string.Equals(entryName, leaf, StringComparison.Ordinal)) continue;

      // Found it. Pull the extent record.
      var xtntBlk = BinaryPrimitives.ReadUInt32LittleEndian(rootCluster.AsSpan(SlotOffset(slot) + 0x14));
      var xtntCnt = BinaryPrimitives.ReadUInt32LittleEndian(rootCluster.AsSpan(SlotOffset(slot) + 0x18));
      if (xtntCnt == 0) xtntCnt = 1; // matches reader convention

      // Free + wipe the data extent.
      var bitmap = ReadBitmap(image);
      if (xtntBlk >= FirstDataBlock && xtntCnt > 0) {
        if (wipeData) {
          var extentBytes = (long)xtntCnt * BlockSize;
          var dataOff = (long)xtntBlk * BlockSize;
          if (dataOff + extentBytes <= image.Length) {
            image.Position = dataOff;
            image.Write(new byte[extentBytes]);
          }
        }
        for (var b = 0u; b < xtntCnt; b++)
          SetBitmapBit(bitmap, xtntBlk + b, false);
        WriteBitmap(image, bitmap);
      }

      // Clear the inode slot — every byte goes to 0 so no stale name/extent
      // bytes leak into the next reader. Status byte goes to 0 explicitly
      // so the reader treats the slot as free (di_status & (USED|LINK) == 0).
      ClearInodeSlot(image, slot);

      // Refresh root-self di_size to reflect the reduced entry count.
      UpdateRootSelfSize(image, ReadRootCluster(image), addedSlot: -1);

      return true;
    }
    return false;
  }

  // ── Root cluster helpers ─────────────────────────────────────────────────

  private static int SlotOffset(int slot) {
    var blockOffset = slot / InodesPerBlock;
    var slotInBlock = slot % InodesPerBlock;
    return blockOffset * BlockSize + slotInBlock * InodeSize;
  }

  private static byte[] ReadRootCluster(Stream image) {
    var buf = new byte[RootDirBlocks * BlockSize];
    image.Position = (long)RootDirStart * BlockSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static int FindFreeRootSlot(byte[] rootCluster) {
    // Entries 0..2 are reserved (root self-ref + .bitmap + .inodes).
    for (var slot = 3; slot < InodesPerBlock * RootDirBlocks; slot++) {
      var status = rootCluster[SlotOffset(slot) + 0x3D];
      if (status == 0) return slot;
    }
    return -1;
  }

  private static void WriteInode(Stream image, int slot, string name, uint size,
                                 uint firstExtentBlock, uint extentBlockCount) {
    var buf = new byte[InodeSize];
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, MaxShortName);
    nameBytes.AsSpan(0, nameLen).CopyTo(buf.AsSpan(0, MaxShortName));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x10), size);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x14), firstExtentBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x18), extentBlockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x1C), 0u);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x20), (ushort)(SIfreg | PermFile));
    // Times/uid/gid/zero stay zero. Status = USED.
    buf[0x3D] = FileUsed;

    image.Position = (long)RootDirStart * BlockSize + SlotOffset(slot);
    image.Write(buf);
  }

  private static void ClearInodeSlot(Stream image, int slot) {
    image.Position = (long)RootDirStart * BlockSize + SlotOffset(slot);
    image.Write(new byte[InodeSize]);
  }

  /// <summary>
  /// Updates the root self-reference inode's <c>di_size</c> (offset 0x10) so
  /// it reflects the current live-entry count × 64 bytes. The writer sets
  /// this initially, but every Add/Remove changes the count. We recount from
  /// the cluster snapshot for accuracy — cheap, the cluster is 2 KB.
  /// </summary>
  /// <param name="image">Underlying stream — the new <c>di_size</c> is
  /// written back at LBA 1 + entry 0 + 0x10.</param>
  /// <param name="rootCluster">Snapshot of LBA 1-4 used to count live entries
  /// without re-reading the stream.</param>
  /// <param name="addedSlot">Slot index just added; used to seed the count
  /// when the caller has not yet flushed the new dirent. Pass -1 for the
  /// remove path (the slot is already cleared on-disk).</param>
  private static void UpdateRootSelfSize(Stream image, byte[] rootCluster, int addedSlot) {
    var live = 0;
    for (var slot = 0; slot < InodesPerBlock * RootDirBlocks; slot++) {
      var status = rootCluster[SlotOffset(slot) + 0x3D];
      if (IsLiveStatus(status)) live++;
    }
    if (addedSlot >= 0) {
      // Caller snapshotted before flushing the new dirent; account for it.
      var snapshotStatus = rootCluster[SlotOffset(addedSlot) + 0x3D];
      if (!IsLiveStatus(snapshotStatus)) live++;
    }
    var newSize = (uint)(InodeSize * live);
    image.Position = (long)RootDirStart * BlockSize + SlotOffset(0) + 0x10;
    var buf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, newSize);
    image.Write(buf);
  }

  // ── Bitmap helpers ──────────────────────────────────────────────────────

  private static byte[] ReadBitmap(Stream image) {
    var buf = new byte[BlockSize];
    image.Position = (long)BitmapBlock * BlockSize;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBitmap(Stream image, byte[] bitmap) {
    image.Position = (long)BitmapBlock * BlockSize;
    image.Write(bitmap);
  }

  private static bool BitmapBitSet(byte[] bitmap, uint block) {
    var byteIdx = (int)(block >> 3);
    if (byteIdx >= bitmap.Length) return false;
    return (bitmap[byteIdx] & (1 << (int)(block & 7))) != 0;
  }

  private static void SetBitmapBit(byte[] bitmap, uint block, bool allocated) {
    var byteIdx = (int)(block >> 3);
    if (byteIdx >= bitmap.Length) return;
    var mask = (byte)(1 << (int)(block & 7));
    if (allocated)
      bitmap[byteIdx] |= mask;
    else
      bitmap[byteIdx] &= (byte)~mask;
  }

  /// <summary>
  /// Finds the lowest-block-index contiguous run of <paramref name="needed"/>
  /// free blocks at or after <see cref="FirstDataBlock"/>. The bitmap is the
  /// authoritative free-space record. When the existing bitmap doesn't have
  /// a long-enough run within its current scope, we extend past the current
  /// image end (the caller grows the stream). The bitmap block itself holds
  /// 1 bit per block, so an image of N blocks needs N bits — for our
  /// 512-byte bitmap that caps at 4096 blocks (= 2 MB image). Past that the
  /// modifier returns null and the caller raises "disk full".
  /// </summary>
  private static uint? FindFreeContiguousExtent(byte[] bitmap, long imageLength, uint needed) {
    if (needed == 0) needed = 1;
    var maxBlocks = (uint)Math.Min((long)bitmap.Length * 8, int.MaxValue);
    var imageBlocks = (uint)((imageLength + BlockSize - 1) / BlockSize);

    // Scan candidates starting at FirstDataBlock. Two phases:
    //   (1) Look inside the current bitmap scope for a run of free bits.
    //   (2) If nothing fits, try growing past the current image end —
    //       the caller's SetLength() handles the actual extension.
    var run = 0u;
    var runStart = 0u;
    for (var blk = FirstDataBlock; blk < maxBlocks; blk++) {
      if (BitmapBitSet(bitmap, blk)) {
        run = 0;
        runStart = 0;
        continue;
      }
      if (run == 0) runStart = blk;
      run++;
      if (run >= needed) return runStart;
    }

    // Phase 2: grow past the image end. The trailing bits of the bitmap are
    // already zero (free) for past-EOF blocks; if we ran out of run because
    // imageBlocks > maxBlocks the bitmap is undersized and we give up.
    if (imageBlocks >= maxBlocks) return null;
    // Best-effort start at imageBlocks (or end of last found run if it was
    // straddling the image boundary).
    return run > 0 ? runStart : imageBlocks;
  }

  // ── Shared helpers ──────────────────────────────────────────────────────

  private static bool IsLiveStatus(byte status)
    => (status & (FileUsed | FileLink | FileBusy)) != 0;

  private static string ReadInodeName(ReadOnlySpan<byte> raw) {
    var end = 0;
    while (end < raw.Length && raw[end] != 0) end++;
    return Encoding.UTF8.GetString(raw[..end]);
  }

  /// <summary>
  /// Flattens path-style names down to the leaf (QNX4 R/W writer does not
  /// emit subdirs — same scope as the WORM writer) and truncates to the
  /// 16-byte QNX4 short-name slot.
  /// </summary>
  private static string TruncateShortName(string name) {
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (string.IsNullOrEmpty(leaf)) return "";
    if (leaf is "." or "..") return "";
    var bytes = Encoding.UTF8.GetBytes(leaf);
    if (bytes.Length > MaxShortName) bytes = bytes.AsSpan(0, MaxShortName).ToArray();
    return Encoding.UTF8.GetString(bytes).TrimEnd('�');
  }
}
