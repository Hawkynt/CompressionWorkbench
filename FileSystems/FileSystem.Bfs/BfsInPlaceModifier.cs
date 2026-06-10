#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Bfs;

/// <summary>
/// True in-place R/W mutation for BFS images: inode + B+ tree leaf entries +
/// AG bitmap are flipped at fixed sector offsets, leaving every block that is
/// not directly touched by the requested change byte-identical to the original.
/// </summary>
/// <remarks>
/// <para>Per Dominic Giampaolo's <i>Practical Filesystem Design</i> (BFS chapter):</para>
/// <list type="bullet">
///   <item>1024-byte block_size (single supported size for our writer-produced images)</item>
///   <item>AG bitmap at fixed block 10 — one bit per block, MSB-first packing,
///     <c>1 = allocated</c></item>
///   <item>1024-byte inode at a fixed block address (block_run length = 1)</item>
///   <item>Directory = chain of B+ tree leaf blocks; each leaf is 28-byte header
///     + cumulative-u16 key-length table + concatenated UTF-8 key bytes + i64 value
///     table growing downward from the block tail.</item>
/// </list>
/// <para><b>Scope (MVP):</b></para>
/// <list type="bullet">
///   <item>Root-directory mutations only — files inserted at the root.</item>
///   <item>Single AG (block 10 bitmap) — the writer always emits this layout.</item>
///   <item>Single B+ tree leaf for the root — i.e., the new entry must fit in the
///     existing root leaf (~40-60 short-named entries). When it would overflow,
///     this modifier falls back to <see cref="ModifyRebuilder"/> so the user still
///     gets a working image, just not byte-identically untouched.</item>
///   <item>File data uses a single direct block_run extent (no indirect/double-indirect).</item>
///   <item>Subdirectory creation falls back to <see cref="ModifyRebuilder"/>.</item>
/// </list>
/// <para>Untouched blocks (every block that this modifier does not write to)
/// remain byte-identical at their original offsets. This is the tested
/// invariant of <c>BfsInPlaceModifyTests</c>.</para>
/// </remarks>
internal static class BfsInPlaceModifier {

  // ── BFS layout constants (mirror BfsWriter; do not drift) ───────────
  private const uint InodeMagic = 0x3BDE0AD9;
  private const int BlockSize = 1024;
  private const int AgBitmapBlock = 10;
  private const int RootDirInodeBlock = 11;
  private const int RootDirBtreeBlock = 12;
  private const int InodeDataStreamOffset = 72;
  private const int NumDirectBlocks = 12;
  private const int BtreeLeafHeaderSize = 28;
  private const uint S_IFREG = 0x8000;
  private const uint S_IRWXU = 0x01C0;

  // Inode size field on disk (1024 bytes total per inode block)
  private const int InodeSize = 1024;

  // ── Public entry points ────────────────────────────────────────────

  /// <summary>
  /// Inserts (or replaces) files in the root directory in place. When the
  /// requested change can't be expressed as a single-leaf in-place mutation
  /// — root leaf would overflow, subdirectory write requested, file would
  /// not fit in the free pool — falls back to <paramref name="rebuild"/>.
  /// </summary>
  public static void Add(
    Stream archive,
    IReadOnlyList<ArchiveInputInfo> inputs,
    Action<Stream, IReadOnlyList<ArchiveInputInfo>> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(rebuild);

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      payloads.Add((name, data));

    if (payloads.Count == 0) return;

    // Read whole image into a byte[] so we can plan + commit atomically.
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryAddInPlace(image, payloads)) {
      rebuild(archive, inputs);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Removes the named entries from the root directory in place by clearing
  /// their inode + data bitmap bits and shifting the root B+ tree leaf to
  /// drop them. Falls back to <paramref name="rebuild"/> if an entry is not
  /// at the root or any other reason makes a clean in-place removal
  /// impossible.
  /// </summary>
  public static void Remove(
    Stream archive,
    string[] entryNames,
    Action<Stream, string[]> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ArgumentNullException.ThrowIfNull(rebuild);

    if (entryNames.Length == 0) return;

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryRemoveInPlace(image, entryNames)) {
      rebuild(archive, entryNames);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Replaces the bytes of a single root-level file in place. If the new
  /// payload fits inside the existing allocated block run, every other block
  /// in the image stays byte-identical and only the inode <c>size</c> field
  /// + the data blocks change. If it does not fit, fresh blocks are
  /// allocated from the bitmap and the old run is freed; metadata blocks
  /// outside the touched inode still stay byte-identical.
  /// </summary>
  public static bool TryReplace(Stream archive, string entryName, byte[] newData) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(newData);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryReplaceInPlace(image, entryName, newData))
      return false;

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    return true;
  }

  // ── Core mutators ──────────────────────────────────────────────────

  private static bool TryAddInPlace(byte[] image, List<(string Name, byte[] Data)> payloads) {
    if (!TryLoadRootLeaf(image, out var leaf)) return false;

    // Reject anything that touches a subdirectory — stay strictly root-MVP.
    foreach (var (name, _) in payloads)
      if (name.Contains('/') || name.Contains('\\'))
        return false;

    // Build a working entry list. Existing entries first, replacements applied
    // by name (case-insensitive — matches ModifyRebuilder's default semantics).
    var working = leaf.Entries
      .Select(e => (e.Name, e.InodeBlock, IsExisting: true))
      .ToList();

    foreach (var (name, data) in payloads) {
      var existingIdx = working.FindIndex(e =>
        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

      if (existingIdx >= 0) {
        // Replace: rewrite the file's data in place (possibly re-allocating).
        var inodeBlock = working[existingIdx].InodeBlock;
        if (!TryReplaceFileBytes(image, inodeBlock, data)) return false;
        continue;
      }

      // New entry — allocate inode block + data blocks from the AG bitmap.
      var dataBlockCount = (data.Length + BlockSize - 1) / BlockSize;
      if (!TryAllocateInode(image, out var newInodeBlock)) return false;
      var dataStartBlock = 0;
      if (dataBlockCount > 0) {
        if (!TryAllocateRun(image, dataBlockCount, out dataStartBlock)) {
          // Roll back inode allocation so the bitmap stays consistent.
          ClearBit(image, AgBitmapBlock * BlockSize, newInodeBlock);
          return false;
        }
      }

      WriteFileInode(image, newInodeBlock, RootDirInodeBlock, dataStartBlock, dataBlockCount, data.Length);
      if (data.Length > 0)
        data.CopyTo(image.AsSpan(dataStartBlock * BlockSize));

      working.Add((name, newInodeBlock, false));
    }

    // Re-sort the working list (BFS B+ tree key order). Use ordinal so it
    // matches the writer's SortedDictionary<string, _>(StringComparer.Ordinal).
    working.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

    // Pack back into the single root B+ tree leaf at block 12 — refuse if
    // it would overflow so the rebuild fallback takes over honestly.
    var newEntries = working.Select(e => (e.Name, e.InodeBlock)).ToList();
    if (!FitsInOneLeaf(newEntries)) return false;

    WriteRootBtreeLeaf(image, newEntries);

    // Bookkeeping: refresh superblock.used_blocks from the live bitmap so
    // sanity checks (and our own EnumerateExtents) keep agreeing with reality.
    UpdateUsedBlocksFromBitmap(image);
    return true;
  }

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames) {
    if (!TryLoadRootLeaf(image, out var leaf)) return false;

    var toRemove = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    var matched = new List<(string Name, int InodeBlock)>();
    var kept = new List<(string Name, int InodeBlock)>();

    foreach (var e in leaf.Entries) {
      if (toRemove.Contains(e.Name)) matched.Add((e.Name, e.InodeBlock));
      else kept.Add((e.Name, e.InodeBlock));
    }

    // Every requested removal must exist at the root — otherwise let the
    // rebuild path handle it (it might be a subdirectory entry).
    if (matched.Count != toRemove.Count) {
      var matchedNames = new HashSet<string>(matched.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
      foreach (var name in toRemove)
        if (!matchedNames.Contains(name)) return false;
    }

    var bitmapOffset = AgBitmapBlock * BlockSize;

    // Free each victim's inode + data run via bitmap-flip + zero-wipe of
    // the freed blocks (matches ModifyRebuilder's secure-erase semantics).
    foreach (var (_, inodeBlock) in matched) {
      var inodeOff = inodeBlock * BlockSize;
      if (inodeOff < 0 || inodeOff + BlockSize > image.Length) return false;

      // Parse data_stream.direct[0] before we wipe the inode.
      var (_, dataStart, dataLen) = ReadBlockRun(image, inodeOff + InodeDataStreamOffset);

      // Zero the inode block + free its bit.
      image.AsSpan(inodeOff, BlockSize).Clear();
      ClearBit(image, bitmapOffset, inodeBlock);

      // Free the data run, if any.
      if (dataLen > 0) {
        var dataOff = dataStart * BlockSize;
        if (dataOff >= 0 && dataOff + dataLen * BlockSize <= image.Length) {
          image.AsSpan(dataOff, dataLen * BlockSize).Clear();
          for (var i = 0; i < dataLen; i++)
            ClearBit(image, bitmapOffset, dataStart + i);
        }
      }
    }

    // Rewrite the root B+ tree leaf with the surviving entries (still
    // sorted — we kept them in their existing key order, which is sorted).
    WriteRootBtreeLeaf(image, kept);
    UpdateUsedBlocksFromBitmap(image);
    return true;
  }

  private static bool TryReplaceInPlace(byte[] image, string entryName, byte[] newData) {
    if (!TryLoadRootLeaf(image, out var leaf)) return false;

    var hit = leaf.Entries.FirstOrDefault(e =>
      string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));
    if (hit.Name == null) return false;

    if (!TryReplaceFileBytes(image, hit.InodeBlock, newData)) return false;
    UpdateUsedBlocksFromBitmap(image);
    return true;
  }

  // ── File-data replacement ──────────────────────────────────────────

  /// <summary>
  /// Rewrites the data of the file whose inode lives at
  /// <paramref name="inodeBlock"/> with <paramref name="newData"/>. If the
  /// new payload fits inside the currently allocated run, the bitmap and
  /// the inode's block_run are untouched and only the trailing portion of
  /// the data run + the inode's size field change. Otherwise a fresh run
  /// is allocated, the old run is freed, and the inode is updated.
  /// </summary>
  private static bool TryReplaceFileBytes(byte[] image, int inodeBlock, byte[] newData) {
    var inodeOff = inodeBlock * BlockSize;
    if (inodeOff < 0 || inodeOff + BlockSize > image.Length) return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(inodeOff)) != InodeMagic) return false;

    var (_, oldStart, oldLen) = ReadBlockRun(image, inodeOff + InodeDataStreamOffset);
    var bitmapOffset = AgBitmapBlock * BlockSize;
    var newBlockCount = (newData.Length + BlockSize - 1) / BlockSize;

    if (newBlockCount <= oldLen) {
      // Fits — rewrite in place, zero the slack, update inode.size.
      if (oldLen > 0) {
        var runOff = oldStart * BlockSize;
        if (runOff < 0 || runOff + oldLen * BlockSize > image.Length) return false;
        image.AsSpan(runOff, oldLen * BlockSize).Clear();
        if (newData.Length > 0) newData.CopyTo(image.AsSpan(runOff));
      } else if (newData.Length > 0) {
        // We had no data run and now we need one — fall through to alloc.
        if (!TryAllocateRun(image, newBlockCount, out var alloc)) return false;
        newData.CopyTo(image.AsSpan(alloc * BlockSize));
        WriteBlockRun(image, inodeOff + InodeDataStreamOffset, 0, alloc, newBlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(
          image.AsSpan(inodeOff + InodeDataStreamOffset + NumDirectBlocks * 8),
          (long)newBlockCount * BlockSize);
      }

      BinaryPrimitives.WriteInt64LittleEndian(
        image.AsSpan(inodeOff + InodeDataStreamOffset + 136), newData.Length);
      return true;
    }

    // Doesn't fit — allocate fresh, free old.
    if (!TryAllocateRun(image, newBlockCount, out var newStart)) return false;
    if (newData.Length > 0) newData.CopyTo(image.AsSpan(newStart * BlockSize));

    if (oldLen > 0) {
      var oldOff = oldStart * BlockSize;
      if (oldOff >= 0 && oldOff + oldLen * BlockSize <= image.Length)
        image.AsSpan(oldOff, oldLen * BlockSize).Clear();
      for (var i = 0; i < oldLen; i++)
        ClearBit(image, bitmapOffset, oldStart + i);
    }

    WriteBlockRun(image, inodeOff + InodeDataStreamOffset, 0, newStart, newBlockCount);
    BinaryPrimitives.WriteInt64LittleEndian(
      image.AsSpan(inodeOff + InodeDataStreamOffset + NumDirectBlocks * 8),
      (long)newBlockCount * BlockSize);
    BinaryPrimitives.WriteInt64LittleEndian(
      image.AsSpan(inodeOff + InodeDataStreamOffset + 136), newData.Length);
    return true;
  }

  // ── Root B+ tree leaf parser/writer ────────────────────────────────

  private readonly struct RootLeafView {
    public int LeafBlock { get; init; }
    public List<(string Name, int InodeBlock)> Entries { get; init; }
  }

  /// <summary>
  /// Loads the single-leaf root directory B+ tree. Returns false (forcing the
  /// rebuild fallback) if the image fails any sanity check, if the root leaf
  /// is multi-leaf (right_link != -1), or if any value can't be parsed.
  /// </summary>
  private static bool TryLoadRootLeaf(byte[] image, out RootLeafView leaf) {
    leaf = default;
    if (image.Length < (RootDirBtreeBlock + 1) * BlockSize) return false;

    var sb = BfsSuperblock.TryParse(image);
    if (!sb.Valid) return false;
    if (sb.BlockSize != BlockSize) return false;     // single-block-size MVP
    if (sb.NumAgs != 1) return false;                 // single-AG MVP
    if (sb.SuperblockOffset != 0) return false;       // writer-produced images only

    // Locate the root inode + its B+ tree leaf via the inode's direct[0].
    var rootRun = ReadBlockRun(image, sb.SuperblockOffset + 116);
    if (rootRun.Length == 0) return false;
    var rootInodeOff = rootRun.Start * BlockSize;
    if (rootInodeOff + InodeDataStreamOffset + 8 > image.Length) return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInodeOff)) != InodeMagic) return false;

    var btreeRun = ReadBlockRun(image, rootInodeOff + InodeDataStreamOffset);
    if (btreeRun.Length == 0) return false;
    var leafBlock = btreeRun.Start;
    var leafOff = leafBlock * BlockSize;
    if (leafOff + BlockSize > image.Length) return false;

    // Refuse multi-leaf roots — keep MVP scope honest.
    var rightLink = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(leafOff + 8));
    if (rightLink != -1L) return false;

    var keyCount = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(leafOff + 24));
    var totalKeyLength = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(leafOff + 26));

    var entries = new List<(string Name, int InodeBlock)>(keyCount);
    if (keyCount == 0) {
      leaf = new RootLeafView { LeafBlock = leafBlock, Entries = entries };
      return true;
    }

    var keyLenTableOff = leafOff + BtreeLeafHeaderSize;
    var keyDataOff = keyLenTableOff + keyCount * 2;
    if (keyDataOff + totalKeyLength > image.Length) return false;

    var cumulative = new ushort[keyCount];
    for (var i = 0; i < keyCount; i++)
      cumulative[i] = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(keyLenTableOff + i * 2));

    var valuesStart = leafOff + BlockSize - keyCount * 8;
    if (valuesStart < keyDataOff + totalKeyLength) return false;

    var prev = 0;
    for (var i = 0; i < keyCount; i++) {
      var nameLen = cumulative[i] - prev;
      if (nameLen < 0 || nameLen > 255) return false;
      var name = Encoding.UTF8.GetString(image, keyDataOff + prev, nameLen);
      prev = cumulative[i];
      var inodeBlockOffT = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(valuesStart + i * 8));
      entries.Add((name, (int)inodeBlockOffT));
    }

    leaf = new RootLeafView { LeafBlock = leafBlock, Entries = entries };
    return true;
  }

  private static bool FitsInOneLeaf(IReadOnlyList<(string Name, int InodeBlock)> entries) {
    var used = BtreeLeafHeaderSize;
    foreach (var (name, _) in entries) {
      var nameLen = Encoding.UTF8.GetByteCount(name);
      used += 2 + nameLen + 8;
      if (used > BlockSize) return false;
    }
    return true;
  }

  /// <summary>
  /// Overwrites the root B+ tree leaf block (block 12) with the supplied
  /// sorted entry list — same byte layout as <c>BfsWriter.WriteBtreeLeaf</c>.
  /// </summary>
  private static void WriteRootBtreeLeaf(byte[] image, IReadOnlyList<(string Name, int InodeBlock)> entries) {
    var off = RootDirBtreeBlock * BlockSize;
    image.AsSpan(off, BlockSize).Clear();

    // Header.
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(off), -1L);          // left_link
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(off + 8), -1L);      // right_link
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(off + 16), -1L);     // overflow_link
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 24), (ushort)entries.Count);

    if (entries.Count == 0) return;

    var keyLenTableOff = off + BtreeLeafHeaderSize;
    var keyDataOff = keyLenTableOff + entries.Count * 2;

    var keyBytes = new List<byte>();
    var cumulative = new ushort[entries.Count];
    for (var i = 0; i < entries.Count; i++) {
      var nameBytes = Encoding.UTF8.GetBytes(entries[i].Name);
      keyBytes.AddRange(nameBytes);
      cumulative[i] = (ushort)keyBytes.Count;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 26), (ushort)keyBytes.Count);
    for (var i = 0; i < entries.Count; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(keyLenTableOff + i * 2), cumulative[i]);

    keyBytes.CopyTo(0, image, keyDataOff, keyBytes.Count);

    var valuesStart = off + BlockSize - entries.Count * 8;
    for (var i = 0; i < entries.Count; i++)
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(valuesStart + i * 8), entries[i].InodeBlock);
  }

  // ── Inode writer ───────────────────────────────────────────────────

  private static void WriteFileInode(byte[] image, int inodeBlock, int parentInodeBlock, int dataStartBlock, int dataBlocks, int fileSize) {
    var off = inodeBlock * BlockSize;
    image.AsSpan(off, BlockSize).Clear();

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off), InodeMagic);
    WriteBlockRun(image, off + 4, 0, inodeBlock, 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 20), S_IFREG | S_IRWXU);
    WriteBlockRun(image, off + 44, 0, parentInodeBlock, 1);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(off + 64), InodeSize);

    if (dataBlocks > 0) {
      WriteBlockRun(image, off + InodeDataStreamOffset, 0, dataStartBlock, dataBlocks);
      BinaryPrimitives.WriteInt64LittleEndian(
        image.AsSpan(off + InodeDataStreamOffset + NumDirectBlocks * 8),
        (long)dataBlocks * BlockSize);
    }

    BinaryPrimitives.WriteInt64LittleEndian(
      image.AsSpan(off + InodeDataStreamOffset + 136), fileSize);
  }

  // ── Bitmap allocator ───────────────────────────────────────────────

  /// <summary>
  /// Picks one free block out of the AG-0 bitmap, flips its bit to 1, and
  /// returns the block number. Reserves blocks 0..14 (the fixed-layout
  /// metadata region) so the allocator never collides with the superblock,
  /// log, bitmap, or root/indices directory blocks.
  /// </summary>
  private static bool TryAllocateInode(byte[] image, out int block) {
    block = 0;
    var bitmapOffset = AgBitmapBlock * BlockSize;
    var imageBlocks = image.Length / BlockSize;
    for (var b = 15; b < imageBlocks; b++) {
      if (!GetBit(image, bitmapOffset, b)) {
        SetBit(image, bitmapOffset, b);
        block = b;
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Finds <paramref name="length"/> consecutive free blocks, flips them to
  /// allocated, and returns the start block. Empty runs (length 0) return
  /// success with start = 0 and don't touch the bitmap.
  /// </summary>
  private static bool TryAllocateRun(byte[] image, int length, out int startBlock) {
    startBlock = 0;
    if (length == 0) return true;

    var bitmapOffset = AgBitmapBlock * BlockSize;
    var imageBlocks = image.Length / BlockSize;
    var run = 0;
    var runStart = -1;
    for (var b = 15; b < imageBlocks; b++) {
      if (!GetBit(image, bitmapOffset, b)) {
        if (run == 0) runStart = b;
        run++;
        if (run >= length) {
          for (var i = 0; i < length; i++) SetBit(image, bitmapOffset, runStart + i);
          startBlock = runStart;
          return true;
        }
      } else {
        run = 0;
        runStart = -1;
      }
    }
    return false;
  }

  private static bool GetBit(byte[] image, int bitmapOffset, int blockNum) {
    var byteIdx = blockNum / 8;
    var bitIdx = blockNum % 8;
    if (bitmapOffset + byteIdx >= image.Length) return true; // out of range = treat as allocated
    return (image[bitmapOffset + byteIdx] & (1 << (7 - bitIdx))) != 0;
  }

  private static void SetBit(byte[] image, int bitmapOffset, int blockNum) {
    var byteIdx = blockNum / 8;
    var bitIdx = blockNum % 8;
    if (bitmapOffset + byteIdx >= image.Length) return;
    image[bitmapOffset + byteIdx] |= (byte)(1 << (7 - bitIdx));
  }

  private static void ClearBit(byte[] image, int bitmapOffset, int blockNum) {
    var byteIdx = blockNum / 8;
    var bitIdx = blockNum % 8;
    if (bitmapOffset + byteIdx >= image.Length) return;
    image[bitmapOffset + byteIdx] &= (byte)~(1 << (7 - bitIdx));
  }

  // ── Superblock used_blocks accounting ──────────────────────────────

  private static void UpdateUsedBlocksFromBitmap(byte[] image) {
    var sb = BfsSuperblock.TryParse(image);
    if (!sb.Valid) return;

    var bitmapOffset = AgBitmapBlock * BlockSize;
    var numBlocks = (int)sb.NumBlocks;
    if (numBlocks <= 0) return;

    var used = 0L;
    for (var b = 0; b < numBlocks; b++)
      if (GetBit(image, bitmapOffset, b)) used++;

    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(sb.SuperblockOffset + 56), used);
  }

  // ── block_run codec ────────────────────────────────────────────────

  private static (uint Ag, int Start, int Length) ReadBlockRun(byte[] image, int offset) {
    if (offset + 8 > image.Length) return (0, 0, 0);
    var ag = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
    var start = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4));
    var length = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 6));
    return (ag, start, length);
  }

  private static void WriteBlockRun(byte[] image, int offset, uint ag, int start, int length) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), ag);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 4), (ushort)start);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + 6), (ushort)length);
  }
}
