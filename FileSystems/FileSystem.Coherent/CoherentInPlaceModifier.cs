#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Coherent;

/// <summary>
/// True in-place R/W modifier for Mark Williams Coherent OS filesystem images.
/// V7-derived s5fs layout: 512-byte blocks, 64-byte inodes (10 direct + 1/2/3
/// indirect 3-byte zone pointers), 16-byte directory entries (u16 inode +
/// 14-byte NUL-padded name), superblock at file offset 1024 with magic
/// <c>0xFD18</c> at offset 504.
///
/// <para><b>In-place semantic.</b> All three public operations mutate the
/// image stream at fixed byte offsets — no full rebuild, no temporary buffer
/// of the whole image:
/// <list type="bullet">
///   <item><b>Add</b> — scan the inode table for free slots (mode == 0),
///   scan the data area [<c>2 + s_isize</c>, <c>s_fsize</c>) for unreferenced
///   zones, write the new inode + indirect blocks + data blocks at those
///   exact offsets, append a 16-byte dirent into the root directory. The
///   underlying stream is extended (and <c>s_fsize</c> bumped) only when
///   free zones are exhausted.</item>
///   <item><b>Replace</b> — locate the entry's inode by leaf name. If the
///   new payload fits inside the inode's existing on-disk zones, rewrite
///   the data zones byte-for-byte at their current block offsets and patch
///   <c>i_size</c>. Untouched zones (other files, the inode list, the
///   superblock, the root directory) remain byte-identical. If the new
///   payload no longer fits in the existing zones the operation falls back
///   to Remove + Add.</item>
///   <item><b>Remove</b> — zero the 16-byte dirent slot in the root
///   directory, zero every zone the inode reaches (direct + single-indirect
///   pointer block + its data blocks + double-indirect pointer block +
///   per-row indirect blocks + their data blocks + triple-indirect chain),
///   zero the 64-byte inode slot. Both the freed zones and the dirent are
///   wiped so no forensic recovery of the removed entry's content or name
///   is possible.</item>
/// </list></para>
///
/// <para><b>Honest scope.</b> Subdirectory mutation is not supported — Add
/// and Remove operate on the root directory only. Replace honours the same
/// root-only convention. Multi-component names are flattened to their leaf
/// before lookup (matching the way <see cref="CoherentWriter"/> emits them
/// and the way the format's 14-byte dirents enforce). The inode table is
/// sized by the WORM writer to the originally-committed files, so adding
/// more files than the table can hold raises <see cref="IOException"/>
/// — callers that want unbounded growth must rebuild the image.</para>
///
/// <para>The heavy lifting (free-inode scan with SB-overlap exclusion,
/// free-zone reachability scan, tier-aware zone allocation across direct +
/// single-indirect + double-indirect, dirent slot reuse, indirect-pointer
/// block wiping) lives in <see cref="CoherentModifier"/>; this class is the
/// public canonical-signature surface that the descriptor delegates to.</para>
/// </summary>
public static class CoherentInPlaceModifier {

  private const int BlockSize = 512;
  private const int InodeSize = 64;
  private const int InodesPerBlock = BlockSize / InodeSize; // 8
  private const int SuperblockOffset = 1024;
  private const ushort MagicCoherent = 0xFD18;
  private const int RootInode = 2;
  private const int DirEntrySize = 16;
  private const int MaxNameLen = 14;
  private const int DirectZones = 10;
  private const int SingleIndirectSlot = 10;
  private const int DoubleIndirectSlot = 11;
  private const int TripleIndirectSlot = 12;

  // ── Public surface ──────────────────────────────────────────────────────

  /// <summary>
  /// Adds the given files to the root directory of <paramref name="image"/>
  /// using V7-style in-place inode + zone allocation. Each input's leaf name
  /// (last path component, truncated to 14 bytes) is used as the on-disk
  /// dirent name; an existing entry with the same leaf is replaced.
  /// </summary>
  /// <exception cref="IOException">
  /// Thrown when the inode table has no free slot for a new file. The
  /// underlying image is not rolled back — partial adds completed before
  /// the failure remain committed.
  /// </exception>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    ValidateSuperblock(image);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      CoherentModifier.AddFile(image, name, data);
  }

  /// <summary>
  /// Replaces the contents of the named root-level entry with
  /// <paramref name="newData"/>. When the new payload fits inside the
  /// entry's existing direct/indirect zone footprint the rewrite happens at
  /// the original block offsets — all other on-disk bytes (other inodes,
  /// other files' data, the superblock, the root directory dirent layout)
  /// remain byte-identical. Falls back to Remove + Add when the existing
  /// zones can no longer hold the new payload, or when the entry does not
  /// yet exist. Returns true when the rewrite happened in place, false on
  /// the realloc fall-back path.
  /// </summary>
  public static bool Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    ValidateSuperblock(image);

    var leaf = TruncatedLeaf(name);
    var inum = FindRootEntryInode(image, leaf);
    if (inum == 0) {
      // Not present — degenerates into an Add, which is the natural in-place
      // path for "make sure file X has contents Y" semantics.
      CoherentModifier.AddFile(image, leaf, newData);
      return false;
    }

    if (TryRewriteInPlace(image, inum, newData)) return true;

    // Couldn't fit; honour the documented fall-back: remove + add. Both legs
    // are themselves in-place at the byte-offset level — Remove wipes zones,
    // Add scavenges the freshly-freed zones first via the reachability scan.
    CoherentModifier.RemoveFile(image, leaf, wipeData: true);
    CoherentModifier.AddFile(image, leaf, newData);
    return false;
  }

  /// <summary>
  /// Removes the named root-level entry from <paramref name="image"/>.
  /// Wipes every reachable zone (data + indirect pointer blocks) and the
  /// inode slot, then clears the 16-byte dirent so the slot is reusable by
  /// a subsequent Add. Returns false when the name is not present or refers
  /// to a directory; true on success.
  /// </summary>
  public static bool Remove(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ValidateSuperblock(image);
    return CoherentModifier.RemoveFile(image, name, wipeData: true);
  }

  // ── Internals ────────────────────────────────────────────────────────────

  private static void ValidateSuperblock(Stream image) {
    if (image.Length < SuperblockOffset + BlockSize)
      throw new InvalidDataException("Coherent: image too small for superblock.");
    var magicBuf = new byte[2];
    image.Position = SuperblockOffset + 504;
    image.ReadExactly(magicBuf);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(magicBuf);
    if (magic != MagicCoherent)
      throw new InvalidDataException(
        $"Coherent: invalid superblock magic 0x{magic:X4} at offset {SuperblockOffset + 504} "
        + $"(expected 0x{MagicCoherent:X4}).");
  }

  private static string TruncatedLeaf(string raw) {
    var leaf = Path.GetFileName(raw.Replace('\\', '/').TrimEnd('/'));
    if (string.IsNullOrEmpty(leaf)) leaf = raw;
    return leaf.Length > MaxNameLen ? leaf[..MaxNameLen] : leaf;
  }

  /// <summary>
  /// Walks the root directory's data zones looking for a dirent whose name
  /// (case-insensitive, NUL-trimmed, 14-byte) matches <paramref name="leaf"/>.
  /// Returns the matched inode number, or 0 if not present.
  /// </summary>
  private static int FindRootEntryInode(Stream image, string leaf) {
    var (rootMode, rootSize, rootDirect, _, _, _) = ReadInodeFields(image, RootInode);
    if ((rootMode & 0xF000) != 0x4000) return 0; // root must be a directory

    long remaining = rootSize;
    var buf = new byte[BlockSize];
    for (var i = 0; i < DirectZones && remaining > 0; i++) {
      if (rootDirect[i] == 0) break;
      var off = (long)rootDirect[i] * BlockSize;
      if (off + BlockSize > image.Length) break;
      image.Position = off;
      image.ReadExactly(buf);
      var bytesInBlock = (int)Math.Min(remaining, BlockSize);
      for (var p = 0; p + DirEntrySize <= bytesInBlock; p += DirEntrySize) {
        var ino = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p, 2));
        if (ino == 0) continue;
        var name = ReadNullTermAscii(buf, p + 2, MaxNameLen);
        if (name.Equals(leaf, StringComparison.OrdinalIgnoreCase))
          return ino;
      }
      remaining -= bytesInBlock;
    }
    return 0;
  }

  /// <summary>
  /// Rewrites the on-disk payload of the inode at <paramref name="inum"/> in
  /// place when the new bytes fit inside the existing direct + single-indirect
  /// + double-indirect zone capacity already allocated to the inode. Returns
  /// false (without mutating the image) when the new payload would require
  /// either more zones than the inode currently owns or a tier promotion
  /// (e.g. growing past direct-only into the single-indirect band when no
  /// single-indirect pointer block is currently allocated).
  /// </summary>
  private static bool TryRewriteInPlace(Stream image, int inum, byte[] newData) {
    var (mode, _, direct, single, dbl, _) = ReadInodeFields(image, inum);
    if ((mode & 0xF000) == 0x4000) return false; // refuse to overwrite directories

    var dataBlocksNeeded = newData.Length == 0 ? 0 : (newData.Length + BlockSize - 1) / BlockSize;

    // Count the data zones currently reachable from this inode (excluding
    // indirect pointer blocks — those don't carry payload bytes).
    var availableDataZones = new List<uint>();
    for (var i = 0; i < DirectZones; i++)
      if (direct[i] != 0) availableDataZones.Add(direct[i]);

    if (single != 0)
      foreach (var z in ReadPointerBlock(image, single))
        if (z != 0) availableDataZones.Add(z);

    if (dbl != 0)
      foreach (var row in ReadPointerBlock(image, dbl))
        if (row != 0)
          foreach (var z in ReadPointerBlock(image, row))
            if (z != 0) availableDataZones.Add(z);

    if (availableDataZones.Count < dataBlocksNeeded) return false;

    // Write new payload byte-for-byte into the existing data zones, in
    // walk order. Trailing zones (when shrinking) are zeroed so no stale
    // bytes leak.
    var srcOff = 0;
    var zeros = new byte[BlockSize];
    for (var i = 0; i < availableDataZones.Count; i++) {
      var blockOff = (long)availableDataZones[i] * BlockSize;
      if (i < dataBlocksNeeded) {
        var block = new byte[BlockSize];
        var copy = Math.Min(BlockSize, newData.Length - srcOff);
        if (copy > 0) Array.Copy(newData, srcOff, block, 0, copy);
        image.Position = blockOff;
        image.Write(block, 0, BlockSize);
        srcOff += copy;
      } else {
        image.Position = blockOff;
        image.Write(zeros, 0, BlockSize);
      }
    }

    // Patch i_size only; zone pointers stay byte-identical.
    var sizeBuf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, (uint)newData.Length);
    image.Position = 2L * BlockSize + (long)(inum - 1) * InodeSize + 8;
    image.Write(sizeBuf, 0, 4);
    return true;
  }

  private static (ushort mode, uint size, uint[] direct, uint single, uint dbl, uint trip)
      ReadInodeFields(Stream image, int inum) {
    var buf = new byte[InodeSize];
    image.Position = 2L * BlockSize + (long)(inum - 1) * InodeSize;
    image.ReadExactly(buf);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
    var size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4));
    var direct = new uint[DirectZones];
    for (var i = 0; i < DirectZones; i++)
      direct[i] = Read24(buf.AsSpan(12 + i * 3, 3));
    var single = Read24(buf.AsSpan(12 + SingleIndirectSlot * 3, 3));
    var dbl = Read24(buf.AsSpan(12 + DoubleIndirectSlot * 3, 3));
    var trip = Read24(buf.AsSpan(12 + TripleIndirectSlot * 3, 3));
    return (mode, size, direct, single, dbl, trip);
  }

  private static uint[] ReadPointerBlock(Stream image, uint block) {
    if (block == 0) return [];
    var off = (long)block * BlockSize;
    if (off + BlockSize > image.Length) return [];
    var buf = new byte[BlockSize];
    image.Position = off;
    image.ReadExactly(buf);
    var ptrCount = BlockSize / 3; // 170
    var result = new uint[ptrCount];
    for (var i = 0; i < ptrCount; i++)
      result[i] = Read24(buf.AsSpan(i * 3, 3));
    return result;
  }

  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static string ReadNullTermAscii(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
  }
}
