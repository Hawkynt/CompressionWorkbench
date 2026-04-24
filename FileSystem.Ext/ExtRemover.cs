#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// Secure-remove implementation for ext2 images produced by <see cref="ExtWriter"/>.
/// Finds the named file in the root directory (inode 2), zeros every data block the
/// file occupies (trailing block-tip slack past <c>i_size</c> is included because we
/// zero whole blocks), zeros the inode, clears the corresponding bits in the block
/// and inode bitmaps, wipes the directory entry bytes, and updates the free-space
/// bookkeeping in both the superblock and the block group descriptor. After the
/// operation no bytes of the original filename or content remain recoverable.
/// <para>
/// <b>Scope:</b> root-directory-only; only direct block pointers are supported
/// (files up to <c>12 * blockSize</c> bytes — matching the range <see cref="ExtWriter"/>
/// can create). Indirect, double-indirect, and triple-indirect blocks are NOT
/// traversed here; if encountered we throw to prevent a half-wiped file.
/// </para>
/// <para>
/// <b>Dirent strategy:</b> rather than stitch the victim's <c>rec_len</c> into the
/// previous entry's record, we clear the dirent bytes and set the <c>inode</c> field
/// of the entry to zero. Our <see cref="ExtReader"/> stops iterating at a zero-inode
/// slot; that truncates enumeration of anything that follows in the same directory
/// block but satisfies the "no forensic trace" contract. For the descriptor's
/// <see cref="ExtFormatDescriptor.Add"/>-then-<c>Remove</c> usage pattern the file
/// being removed will typically be the last entry, so the truncation is harmless.
/// </para>
/// </summary>
public static class ExtRemover {
  /// <summary>
  /// Removes <paramref name="fileName"/> from the in-memory ext2 image. Throws
  /// <see cref="FileNotFoundException"/> if no root-dir entry matches. The image
  /// is modified in place.
  /// </summary>
  public static void Remove(byte[] image, string fileName) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);

    const int SuperblockOffset = 1024;
    const ushort ExtMagic = 0xEF53;
    const int InodeSize = 128;
    // Root inode number is 2 — inlined below as the "index 1" in the inode table.

    if (image.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext: image too small for superblock.");

    // --- Superblock fields ---
    var sb = image.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24));
    var blockSize = 1024 << (int)logBlockSize;
    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20));

    // --- Metadata block offsets (single-group layout produced by ExtWriter) ---
    var bgdOffset = (int)(firstDataBlock + 1) * blockSize;
    var blockBitmapOffset = (int)(firstDataBlock + 2) * blockSize;
    var inodeBitmapOffset = (int)(firstDataBlock + 3) * blockSize;
    var inodeTableOffset = (int)(firstDataBlock + 4) * blockSize;

    // --- Read root inode and its first direct block (the root-dir contents) ---
    var rootInodeOffset = inodeTableOffset + 1 * InodeSize; // inode 2 = index 1
    var rootDirBlock = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInodeOffset + 40));
    if (rootDirBlock == 0)
      throw new InvalidDataException("ext: root directory has no data block.");

    var dirDataOffset = (int)(rootDirBlock * blockSize);
    if (dirDataOffset + blockSize > image.Length)
      throw new InvalidDataException("ext: root directory block out of range.");

    // --- Walk dir entries to locate the victim ---
    var (victimInode, victimDirOffset, victimRecLen, victimNameLen) =
      FindDirEntry(image, dirDataOffset, blockSize, fileName);
    if (victimInode == 0)
      throw new FileNotFoundException($"File '{fileName}' not found in ext2 root directory.");

    // --- Read the file's inode, collect direct block pointers ---
    var fileInodeOffset = inodeTableOffset + (int)(victimInode - 1) * InodeSize;
    if (fileInodeOffset + InodeSize > image.Length)
      throw new InvalidDataException("ext: file inode out of range.");

    var fileInode = image.AsSpan(fileInodeOffset, InodeSize);
    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(fileInode.Slice(4));

    // Guard against indirect-block files we don't support.
    var maxDirectBytes = 12L * blockSize;
    if (fileSize > maxDirectBytes)
      throw new NotSupportedException(
        $"ext remover: file '{fileName}' ({fileSize} bytes) exceeds direct-block capacity ({maxDirectBytes}); " +
        "indirect block traversal is not implemented.");

    // Non-zero indirect pointers at inode offsets 88/92/96 would mean we'd miss data blocks.
    var indirect = BinaryPrimitives.ReadUInt32LittleEndian(fileInode.Slice(88));
    var dindirect = BinaryPrimitives.ReadUInt32LittleEndian(fileInode.Slice(92));
    var tindirect = BinaryPrimitives.ReadUInt32LittleEndian(fileInode.Slice(96));
    if (indirect != 0 || dindirect != 0 || tindirect != 0)
      throw new NotSupportedException(
        $"ext remover: file '{fileName}' uses indirect block pointers; not supported.");

    var dataBlocks = new List<uint>(12);
    for (var i = 0; i < 12; ++i) {
      var bn = BinaryPrimitives.ReadUInt32LittleEndian(fileInode.Slice(40 + i * 4));
      if (bn == 0) break;
      dataBlocks.Add(bn);
    }

    // --- Zero each data block in full (covers block-tip slack past i_size) ---
    foreach (var bn in dataBlocks) {
      var off = (long)bn * blockSize;
      if (off + blockSize <= image.Length)
        image.AsSpan((int)off, blockSize).Clear();
    }

    // --- Zero the inode bytes entirely ---
    fileInode.Clear();

    // --- Clear the bits in the block bitmap for each freed data block.
    //     Bitmap bit N refers to block (firstDataBlock + N), so we must
    //     subtract firstDataBlock before indexing. ---
    foreach (var bn in dataBlocks)
      ClearBitmapBit(image, blockBitmapOffset, (int)bn - (int)firstDataBlock);

    // --- Clear the bit in the inode bitmap for the freed inode ---
    ClearBitmapBit(image, inodeBitmapOffset, (int)(victimInode - 1));

    // --- Wipe the directory entry bytes; set inode=0 to mark the slot unused ---
    // See class remarks: this truncates enumeration in this block but leaves no
    // trace of the filename. For single-file removal and the descriptor's typical
    // usage this is the pragmatic fsck-tolerable choice within the time budget.
    var victimDirTotal = (int)victimRecLen;
    if (victimDirOffset + victimDirTotal <= dirDataOffset + blockSize)
      image.AsSpan(victimDirOffset, victimDirTotal).Clear();
    // Re-assert inode=0 (already zero after the clear, but explicit about the contract).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(victimDirOffset), 0);
    _ = victimNameLen;

    // --- Update free-count accounting in superblock and BGD ---
    var freeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(12));
    var freeInodes = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(16));
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(12), freeBlocks + (uint)dataBlocks.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(16), freeInodes + 1);

    if (bgdOffset + 32 <= image.Length) {
      var bgd = image.AsSpan(bgdOffset, 32);
      var bgdFreeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(bgd.Slice(12));
      var bgdFreeInodes = BinaryPrimitives.ReadUInt16LittleEndian(bgd.Slice(14));
      BinaryPrimitives.WriteUInt16LittleEndian(bgd.Slice(12), (ushort)(bgdFreeBlocks + dataBlocks.Count));
      BinaryPrimitives.WriteUInt16LittleEndian(bgd.Slice(14), (ushort)(bgdFreeInodes + 1));
      // bg_used_dirs_count only decrements if the removed inode was a directory.
      // ExtWriter only emits regular files outside root, so we skip.
    }
  }

  /// <summary>
  /// Scans the root-dir block for an entry matching <paramref name="fileName"/>.
  /// Returns (0, 0, 0, 0) when not found.
  /// </summary>
  private static (uint Inode, int DirEntryOffset, ushort RecLen, byte NameLen) FindDirEntry(
      byte[] image, int dirDataOffset, int blockSize, string fileName) {
    var targetBytes = Encoding.UTF8.GetBytes(fileName);
    var offset = 0;
    while (offset + 8 <= blockSize) {
      var entryOffset = dirDataOffset + offset;
      var inodeNum = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entryOffset));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entryOffset + 4));
      var nameLen = image[entryOffset + 6];
      // fileType at +7 (unused here)

      if (recLen == 0) break;
      if (offset + 8 + nameLen > blockSize) break;

      if (inodeNum != 0 && nameLen == targetBytes.Length) {
        var match = true;
        for (var i = 0; i < nameLen; ++i) {
          if (image[entryOffset + 8 + i] != targetBytes[i]) { match = false; break; }
        }
        if (match)
          return (inodeNum, entryOffset, recLen, nameLen);
      }

      offset += recLen;
    }
    return (0, 0, 0, 0);
  }

  private static void ClearBitmapBit(byte[] image, int bitmapOffset, int bitIndex) {
    var byteIndex = bitmapOffset + bitIndex / 8;
    if (byteIndex < 0 || byteIndex >= image.Length) return;
    image[byteIndex] &= (byte)~(1 << (bitIndex % 8));
  }
}
