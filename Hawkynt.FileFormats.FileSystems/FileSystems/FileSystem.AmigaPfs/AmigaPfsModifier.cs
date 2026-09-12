#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.AmigaPfs;

/// <summary>
/// Random-access in-place modifier for Stage 1 PFS3 images produced by
/// <see cref="AmigaPfsWriter"/>. Adds and removes entries against the same
/// on-disk shape <see cref="AmigaPfsReader"/> parses: root block, linear
/// dirblock chain (id 0xC4 / 0xCC, next-chain pointer at +12, variable-length
/// entries from +20), and per-file contiguous data extents whose starting
/// block number is recorded as the entry's anode field.
///
/// <para>
/// <b>Stage 1 scope.</b> This modifier preserves the writer's
/// "anode-as-direct-block" convention: each file's data sits at
/// <c>anode * BlockSize</c>, not in an anode table. The full PFS3aio
/// anode table / bitmap / rootinfo machinery is still deferred to a future
/// Stage 2 promotion, so mutated images are <em>not</em> FS-UAE/WinUAE
/// mountable. Self-round-trip through <see cref="AmigaPfsReader"/> is the
/// only correctness gate.
/// </para>
///
/// <para>
/// <b>Allocation policy.</b> Data extents always live past the high-water
/// mark of (last dirblock, last existing file extent). The image is grown
/// via <see cref="Stream.SetLength"/> when a new file's extent or a newly
/// chained dirblock pushes past the current length. Removed extents are
/// zeroed in place; the freed range is not currently re-used by subsequent
/// adds (no free-list bookkeeping — the writer never allocated one).
/// </para>
/// </summary>
public static class AmigaPfsModifier {

  private const int BootBlockRootPointerOffset = 8;
  private const int RootBlockDirPointerOffset = 60;
  private const int DirBlockNextChainOffset = 12;
  private const int DirBlockParentOffset = 16;
  private const int DirBlockEntriesOffset = 20;
  private const int EntryHeaderSize = 17;             // type+anode+size+date+time1+time2+nameLen = 1+1+4+4+2+2+2+1
  private const int EntryTrailingCommentByte = 1;     // zero-length comment byte tracked by the reader
  private const int MaxFilenameLength = 200;
  private const ushort DirBlockId = 0xC4;
  private const ushort DirBlockIdAlt = 0xCC;
  private const uint DefaultRootBlock = 80;

  /// <summary>
  /// Adds <paramref name="name"/> with <paramref name="data"/> to the image.
  /// If an entry with the same name already exists it is removed first
  /// (replace-by-name semantics, matching the descriptor's <c>Add</c>).
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("AmigaPFS: image stream must be readable, writable and seekable.", nameof(image));

    RemoveByName(image, name, wipeData: true);

    var ctx = LoadContext(image);
    var nameBytes = EncodeName(name);
    var entryLen = EntryHeaderSize + nameBytes.Length + EntryTrailingCommentByte;
    var perBlockBudget = ctx.BlockSize - DirBlockEntriesOffset - 1; // -1 sentinel
    if (entryLen > perBlockBudget)
      throw new InvalidOperationException(
        $"AmigaPFS: entry '{name}' takes {entryLen} bytes which exceeds the per-dirblock budget of {perBlockBudget}.");

    // Find a dirblock with room (matching the writer's first-fit packing); otherwise
    // allocate a fresh dirblock past the existing chain and link it.
    var targetBlock = FindDirBlockWithRoom(image, ctx, entryLen);
    if (targetBlock == 0)
      targetBlock = AppendNewDirBlock(image, ctx);

    // Allocate a contiguous run of blocks for the file data past the high-water mark.
    var dataBlocks = data.Length == 0
      ? 1 // zero-byte files still need a non-zero anode so the reader doesn't treat it as a terminator
      : (data.Length + ctx.BlockSize - 1) / ctx.BlockSize;
    var anodeBlock = AllocateContiguousRun(image, ctx, dataBlocks);

    // Write the file payload first so a crash during the dirblock-entry write leaves a stale
    // (already-zero) data extent rather than a referenced one with garbage.
    if (data.Length > 0)
      WriteRange(image, (long)anodeBlock * ctx.BlockSize, data);

    // Append the new entry into the chosen dirblock.
    AppendEntry(image, ctx, targetBlock, nameBytes, anodeBlock, (uint)data.Length, isDirectory: false, modTime);

    // Refresh in-memory high-water bookkeeping the next call will reuse.
  }

  /// <summary>
  /// Adds a directory marker. Stage 1 PFS3 has no real subdir; the entry is
  /// recorded with the directory type bit so the reader surfaces it via
  /// <see cref="AmigaPfsReader.Entries"/>.
  /// </summary>
  public static void AddDirectory(Stream image, string name, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("AmigaPFS: image stream must be readable, writable and seekable.", nameof(image));

    RemoveByName(image, name, wipeData: true);

    var ctx = LoadContext(image);
    var nameBytes = EncodeName(name);
    var entryLen = EntryHeaderSize + nameBytes.Length + EntryTrailingCommentByte;
    var perBlockBudget = ctx.BlockSize - DirBlockEntriesOffset - 1;
    if (entryLen > perBlockBudget)
      throw new InvalidOperationException(
        $"AmigaPFS: directory entry '{name}' takes {entryLen} bytes which exceeds the per-dirblock budget of {perBlockBudget}.");

    var targetBlock = FindDirBlockWithRoom(image, ctx, entryLen);
    if (targetBlock == 0)
      targetBlock = AppendNewDirBlock(image, ctx);

    AppendEntry(image, ctx, targetBlock, nameBytes, anode: 0u, size: 0u, isDirectory: true, modTime);
  }

  /// <summary>
  /// Removes <paramref name="name"/> from the image (if present). Returns
  /// true on success, false if the entry wasn't found. The file's data
  /// extent and the dirblock entry bytes are zeroed when
  /// <paramref name="wipeData"/> is true.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("AmigaPFS: image stream must be readable, writable and seekable.", nameof(image));
    return RemoveByName(image, name, wipeData);
  }

  // ── Core operations ────────────────────────────────────────────────────

  private static bool RemoveByName(Stream image, string name, bool wipeData) {
    var ctx = LoadContext(image);
    if (ctx.FirstDirBlock == 0)
      return false;

    // Walk the dirblock chain, tracking each block's predecessor so we can
    // unlink an empty block from the chain after the last entry leaves.
    var blockNum = ctx.FirstDirBlock;
    uint prevBlock = 0;
    var seen = new HashSet<uint>();
    while (blockNum != 0 && seen.Add(blockNum)) {
      var off = (long)blockNum * ctx.BlockSize;
      if (off + ctx.BlockSize > image.Length)
        return false;
      var block = ReadBlock(image, off, ctx.BlockSize);
      var id = BinaryPrimitives.ReadUInt16BigEndian(block);
      if (id != DirBlockId && id != DirBlockIdAlt)
        return false;
      var nextChain = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(DirBlockNextChainOffset));

      if (TryRemoveEntryFromBlock(block, ctx.BlockSize, name, out var removedAnode, out var removedSize)) {
        // Persist the compacted dirblock.
        WriteRange(image, off, block);

        // Wipe the file's data extent so the bytes don't linger on disk.
        if (wipeData && removedAnode != 0 && removedSize > 0) {
          var extentOff = (long)removedAnode * ctx.BlockSize;
          var blocks = (removedSize + ctx.BlockSize - 1) / ctx.BlockSize;
          var extentLen = (long)blocks * ctx.BlockSize;
          if (extentOff + extentLen <= image.Length)
            WriteRange(image, extentOff, new byte[extentLen]);
        }

        // If the block is now empty AND it isn't the first dirblock, unlink
        // it from the chain. The first dirblock is kept even when empty so
        // the root block's pointer stays valid (matches the writer's
        // always-emit-at-least-one-dirblock invariant).
        if (IsDirBlockEmpty(block) && prevBlock != 0) {
          // prev.next = current.next
          var prevOff = (long)prevBlock * ctx.BlockSize;
          var prevBlk = ReadBlock(image, prevOff, ctx.BlockSize);
          BinaryPrimitives.WriteUInt32BigEndian(prevBlk.AsSpan(DirBlockNextChainOffset), nextChain);
          WriteRange(image, prevOff, prevBlk);
          // Zero the now-orphaned block.
          if (wipeData)
            WriteRange(image, off, new byte[ctx.BlockSize]);
        }
        return true;
      }

      prevBlock = blockNum;
      blockNum = nextChain;
    }
    return false;
  }

  private static bool TryRemoveEntryFromBlock(byte[] block, int blockSize, string name, out uint removedAnode, out uint removedSize) {
    removedAnode = 0;
    removedSize = 0;
    var cursor = DirBlockEntriesOffset;
    while (cursor < blockSize) {
      var len = block[cursor];
      if (len == 0) break;
      if (cursor + len > blockSize) break;
      if (len < EntryHeaderSize) {
        cursor += len;
        continue;
      }
      var nameLen = block[cursor + 16];
      if (cursor + 17 + nameLen > blockSize) {
        cursor += len;
        continue;
      }
      var entryName = Encoding.ASCII.GetString(block, cursor + 17, nameLen);
      if (string.Equals(entryName, name, StringComparison.Ordinal)) {
        removedAnode = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(cursor + 2));
        removedSize = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(cursor + 6));

        // Compact entries: shift everything past this entry left by `len`,
        // zero the freed tail (including the implicit sentinel position).
        var tailStart = cursor + len;
        var tailLen = blockSize - tailStart;
        if (tailLen > 0)
          Buffer.BlockCopy(block, tailStart, block, cursor, tailLen);
        // Zero the now-unused tail so no entry-shaped bytes linger.
        Array.Clear(block, blockSize - len, len);
        return true;
      }
      cursor += len;
    }
    return false;
  }

  private static bool IsDirBlockEmpty(byte[] block) {
    if (block.Length <= DirBlockEntriesOffset) return true;
    // Entries start at +20; if the first length byte is zero the block carries no entries.
    return block[DirBlockEntriesOffset] == 0;
  }

  // ── Layout / allocation helpers ────────────────────────────────────────

  private sealed class Context {
    public int BlockSize { get; init; }
    public uint RootBlock { get; init; }
    public uint FirstDirBlock { get; set; }
  }

  private static Context LoadContext(Stream image) {
    if (image.Length < 512)
      throw new InvalidDataException("AmigaPFS: image too small (need at least one block).");

    // Detect block size by reading offset 0..3 signature; we only emit/parse 512-byte blocks
    // (matching the writer/reader pair), so don't probe larger sizes here.
    const int blockSize = 512;
    var boot = ReadBlock(image, 0, blockSize);
    if (boot[0] != 'P' || boot[1] != 'F' || boot[2] != 'S')
      throw new InvalidDataException("AmigaPFS: missing PFS boot-block signature.");

    var rootBlock = BinaryPrimitives.ReadUInt32BigEndian(boot.AsSpan(BootBlockRootPointerOffset));
    if (rootBlock == 0) rootBlock = DefaultRootBlock;

    var rootOffset = (long)rootBlock * blockSize;
    if (rootOffset + blockSize > image.Length)
      throw new InvalidDataException("AmigaPFS: root-block pointer past end of image.");
    var root = ReadBlock(image, rootOffset, blockSize);
    var firstDirBlock = BinaryPrimitives.ReadUInt32BigEndian(root.AsSpan(RootBlockDirPointerOffset));

    return new Context { BlockSize = blockSize, RootBlock = rootBlock, FirstDirBlock = firstDirBlock };
  }

  private static uint FindDirBlockWithRoom(Stream image, Context ctx, int neededBytes) {
    if (ctx.FirstDirBlock == 0)
      return 0;
    var blockNum = ctx.FirstDirBlock;
    var seen = new HashSet<uint>();
    while (blockNum != 0 && seen.Add(blockNum)) {
      var off = (long)blockNum * ctx.BlockSize;
      if (off + ctx.BlockSize > image.Length) break;
      var block = ReadBlock(image, off, ctx.BlockSize);
      var id = BinaryPrimitives.ReadUInt16BigEndian(block);
      if (id != DirBlockId && id != DirBlockIdAlt) break;
      var used = MeasureUsedEntryBytes(block, ctx.BlockSize);
      var budget = ctx.BlockSize - DirBlockEntriesOffset - 1; // -1 sentinel
      if (used + neededBytes <= budget)
        return blockNum;
      blockNum = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(DirBlockNextChainOffset));
    }
    return 0;
  }

  private static int MeasureUsedEntryBytes(byte[] block, int blockSize) {
    var cursor = DirBlockEntriesOffset;
    while (cursor < blockSize) {
      var len = block[cursor];
      if (len == 0) break;
      if (cursor + len > blockSize) break;
      cursor += len;
    }
    return cursor - DirBlockEntriesOffset;
  }

  /// <summary>
  /// Allocates a fresh dirblock past the existing chain's tail, links it in,
  /// updates the chain or the root pointer if this is the first dirblock,
  /// and grows the stream to cover the new block.
  /// </summary>
  private static uint AppendNewDirBlock(Stream image, Context ctx) {
    var newBlock = NextFreeBlock(image, ctx);
    var newOff = (long)newBlock * ctx.BlockSize;
    EnsureLength(image, newOff + ctx.BlockSize);

    // Emit an empty dirblock at the chosen position.
    var blk = new byte[ctx.BlockSize];
    BinaryPrimitives.WriteUInt16BigEndian(blk, DirBlockId);
    BinaryPrimitives.WriteUInt32BigEndian(blk.AsSpan(4), ToAmigaDatestamp(DateTime.UtcNow));
    BinaryPrimitives.WriteUInt32BigEndian(blk.AsSpan(DirBlockNextChainOffset), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(blk.AsSpan(DirBlockParentOffset), ctx.RootBlock);
    WriteRange(image, newOff, blk);

    // Link the new block.
    if (ctx.FirstDirBlock == 0) {
      // First dirblock — patch the root block.
      var rootOff = (long)ctx.RootBlock * ctx.BlockSize;
      var root = ReadBlock(image, rootOff, ctx.BlockSize);
      BinaryPrimitives.WriteUInt32BigEndian(root.AsSpan(RootBlockDirPointerOffset), newBlock);
      WriteRange(image, rootOff, root);
      ctx.FirstDirBlock = newBlock;
    } else {
      // Walk to the chain tail and update its next pointer.
      var blockNum = ctx.FirstDirBlock;
      var seen = new HashSet<uint>();
      while (blockNum != 0 && seen.Add(blockNum)) {
        var off = (long)blockNum * ctx.BlockSize;
        var block = ReadBlock(image, off, ctx.BlockSize);
        var nextChain = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(DirBlockNextChainOffset));
        if (nextChain == 0) {
          BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(DirBlockNextChainOffset), newBlock);
          WriteRange(image, off, block);
          break;
        }
        blockNum = nextChain;
      }
    }
    return newBlock;
  }

  /// <summary>
  /// Reserves <paramref name="blocks"/> contiguous blocks past the high-water
  /// mark, grows the stream, and returns the starting block number. The
  /// caller writes the file payload to <c>blockNum * BlockSize</c>.
  /// </summary>
  private static uint AllocateContiguousRun(Stream image, Context ctx, int blocks) {
    var start = NextFreeBlock(image, ctx);
    var endOff = (long)(start + blocks) * ctx.BlockSize;
    EnsureLength(image, endOff);
    return start;
  }

  /// <summary>
  /// Computes the next free block past everything the image currently uses:
  /// boot + root + dirblock chain + every entry's data extent. Returns a
  /// block number such that <c>blockNum * BlockSize >= image.Length</c> or
  /// past the last existing extent — whichever is larger.
  /// </summary>
  private static uint NextFreeBlock(Stream image, Context ctx) {
    // High-water mark seeded with the image's current length.
    var highWaterBlock = (uint)((image.Length + ctx.BlockSize - 1) / ctx.BlockSize);

    // Also pessimise past the dirblock chain tail + every existing data extent.
    // We walk the chain and inspect each entry's anode + size.
    if (ctx.FirstDirBlock != 0) {
      var blockNum = ctx.FirstDirBlock;
      var seen = new HashSet<uint>();
      while (blockNum != 0 && seen.Add(blockNum)) {
        if (blockNum + 1 > highWaterBlock) highWaterBlock = blockNum + 1;
        var off = (long)blockNum * ctx.BlockSize;
        if (off + ctx.BlockSize > image.Length) break;
        var block = ReadBlock(image, off, ctx.BlockSize);
        var id = BinaryPrimitives.ReadUInt16BigEndian(block);
        if (id != DirBlockId && id != DirBlockIdAlt) break;
        // Walk entries to track their data extents.
        var cursor = DirBlockEntriesOffset;
        while (cursor < ctx.BlockSize) {
          var len = block[cursor];
          if (len == 0) break;
          if (cursor + len > ctx.BlockSize) break;
          if (len < EntryHeaderSize) { cursor += len; continue; }
          var anode = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(cursor + 2));
          var size = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(cursor + 6));
          var typeByte = block[cursor + 1];
          var isDir = (typeByte & 0x80) != 0;
          if (!isDir && anode != 0) {
            var extentBlocks = size == 0 ? 1u : ((size + (uint)ctx.BlockSize - 1) / (uint)ctx.BlockSize);
            var end = anode + extentBlocks;
            if (end > highWaterBlock) highWaterBlock = end;
          }
          cursor += len;
        }
        blockNum = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(DirBlockNextChainOffset));
      }
    } else {
      // No dirblocks yet; keep at least one block past the root for the first dirblock allocation.
      var rootEnd = ctx.RootBlock + 1u;
      if (rootEnd > highWaterBlock) highWaterBlock = rootEnd;
    }
    return highWaterBlock;
  }

  private static void AppendEntry(Stream image, Context ctx, uint dirBlockNum,
    byte[] nameBytes, uint anode, uint size, bool isDirectory, DateTime? modTime) {
    var off = (long)dirBlockNum * ctx.BlockSize;
    var block = ReadBlock(image, off, ctx.BlockSize);
    var used = MeasureUsedEntryBytes(block, ctx.BlockSize);
    var cursor = DirBlockEntriesOffset + used;
    var entryLen = EntryHeaderSize + nameBytes.Length + EntryTrailingCommentByte;

    block[cursor + 0] = (byte)entryLen;
    block[cursor + 1] = isDirectory ? (byte)0x80 : (byte)0x20;
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(cursor + 2), anode);
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(cursor + 6), size);
    var (date, time1, time2) = ToAmigaDateTime(modTime ?? DateTime.UtcNow);
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(cursor + 10), date);
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(cursor + 12), time1);
    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(cursor + 14), time2);
    block[cursor + 16] = (byte)nameBytes.Length;
    Buffer.BlockCopy(nameBytes, 0, block, cursor + 17, nameBytes.Length);
    block[cursor + 17 + nameBytes.Length] = 0; // zero-length trailing comment
    // The next byte (at cursor + entryLen) stays zero — that's the sentinel.

    WriteRange(image, off, block);
  }

  // ── Encoding & I/O helpers ─────────────────────────────────────────────

  private static byte[] EncodeName(string name) {
    var normalised = name.Replace('\\', '/').TrimStart('/');
    var bytes = Encoding.ASCII.GetBytes(normalised);
    if (bytes.Length > MaxFilenameLength) bytes = bytes.AsSpan(0, MaxFilenameLength).ToArray();
    return bytes;
  }

  private static byte[] ReadBlock(Stream image, long offset, int size) {
    image.Position = offset;
    var buf = new byte[size];
    var read = 0;
    while (read < size) {
      var n = image.Read(buf, read, size - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteRange(Stream image, long offset, byte[] data) {
    EnsureLength(image, offset + data.Length);
    image.Position = offset;
    image.Write(data, 0, data.Length);
  }

  private static void EnsureLength(Stream image, long required) {
    if (image.Length < required)
      image.SetLength(required);
  }

  private static (ushort date, ushort time1, ushort time2) ToAmigaDateTime(DateTime dt) {
    var epoch = new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var local = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    if (local < epoch) local = epoch;
    var days = (local - epoch).Days;
    var secsInDay = (int)((local - epoch).TotalSeconds - days * 86400.0);
    var mins = secsInDay / 60;
    var ticks = secsInDay % 60 * 50;
    return ((ushort)(days & 0xFFFF), (ushort)(mins & 0xFFFF), (ushort)(ticks & 0xFFFF));
  }

  private static uint ToAmigaDatestamp(DateTime dt) {
    var epoch = new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var local = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    if (local < epoch) local = epoch;
    return (uint)(local - epoch).TotalSeconds;
  }
}
