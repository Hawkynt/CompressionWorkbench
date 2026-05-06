#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ProDos;

/// <summary>
/// Random-access in-place modifier for Apple ProDOS block-ordered images
/// (<c>.po</c> and <c>.2mg</c>). Reads and writes only the volume directory
/// chain, the bitmap, and the new file's index + data blocks — never the
/// whole image. Lets callers operate on huge underlying streams without
/// paging the entire disk into memory.
/// </summary>
public static class ProDosModifier {

  private const int BlockSize = ProDosReader.BlockSize;     // 512
  private const int VolumeDirStartBlock = ProDosReader.VolumeDirStartBlock;  // 2
  private const int EntriesPerBlock = ProDosReader.EntriesPerBlock;          // 13
  private const int EntrySize = ProDosReader.EntrySize;     // 39
  private const int BitmapStartBlock = 6;

  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  /// <summary>
  /// Adds a file to an existing image. Caller is responsible for ensuring the
  /// name does not already exist; use <see cref="RemoveFile"/> first for
  /// replace-by-name semantics.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x06) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var imageStart = DetectImageStart(image);
    var sanitized = SanitizeName(name);

    var volHeader = ReadBlock(image, imageStart, VolumeDirStartBlock);
    var totalBlocks = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x28));
    var bitmapPtr = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x26));
    if (bitmapPtr == 0) bitmapPtr = BitmapStartBlock;

    var bitmapBlocks = (totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
    var bitmap = ReadBitmap(image, imageStart, bitmapPtr, bitmapBlocks);

    // Plan storage tier + allocate blocks.
    var (storageType, keyPointer, blocksUsed, allocPlan) =
      AllocateStorage(bitmap, totalBlocks, data);

    // Persist data blocks.
    foreach (var (block, offset, count) in allocPlan.DataWrites) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(data, offset, buf, 0, count);
      WriteBlock(image, imageStart, block, buf);
    }

    // Persist index blocks (sapling + tree).
    foreach (var (block, lowBytes, highBytes) in allocPlan.IndexWrites) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(lowBytes, 0, buf, 0, lowBytes.Length);
      Buffer.BlockCopy(highBytes, 0, buf, 256, highBytes.Length);
      WriteBlock(image, imageStart, block, buf);
    }

    // Persist master index block (tree only).
    if (allocPlan.MasterIndexBlock is int master) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(allocPlan.MasterLowBytes!, 0, buf, 0, allocPlan.MasterLowBytes!.Length);
      Buffer.BlockCopy(allocPlan.MasterHighBytes!, 0, buf, 256, allocPlan.MasterHighBytes!.Length);
      WriteBlock(image, imageStart, master, buf);
    }

    // Insert directory entry.
    var slot = FindFreeDirectorySlot(image, imageStart);
    if (!slot.Found)
      throw new InvalidOperationException("ProDOS: volume directory full (no free entry slot).");

    var dirBuf = ReadBlock(image, imageStart, slot.Block);
    var entryOff = 4 + slot.IndexInBlock * EntrySize;
    dirBuf[entryOff + 0] = (byte)((storageType << 4) | (sanitized.Length & 0x0F));
    for (var i = 0; i < sanitized.Length && i < 15; i++)
      dirBuf[entryOff + 1 + i] = (byte)sanitized[i];
    for (var i = sanitized.Length; i < 15; i++)
      dirBuf[entryOff + 1 + i] = 0;
    dirBuf[entryOff + 0x10] = fileType;
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x11), (ushort)keyPointer);
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x13), (ushort)blocksUsed);
    dirBuf[entryOff + 0x15] = (byte)(data.Length & 0xFF);
    dirBuf[entryOff + 0x16] = (byte)((data.Length >> 8) & 0xFF);
    dirBuf[entryOff + 0x17] = (byte)((data.Length >> 16) & 0xFF);
    dirBuf[entryOff + 0x1C] = 0x00;
    dirBuf[entryOff + 0x1D] = 0x00;
    dirBuf[entryOff + 0x1E] = 0xE3;
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x25), VolumeDirStartBlock);

    // Bump file_count. The header lives in block 2; if slot.Block == 2 we must
    // update the same buffer we're about to write, not a stale snapshot.
    if (slot.Block == VolumeDirStartBlock) {
      var fc = BinaryPrimitives.ReadUInt16LittleEndian(dirBuf.AsSpan(4 + 0x24));
      BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(4 + 0x24), (ushort)(fc + 1));
      WriteBlock(image, imageStart, slot.Block, dirBuf);
    } else {
      WriteBlock(image, imageStart, slot.Block, dirBuf);
      var fc = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x24));
      BinaryPrimitives.WriteUInt16LittleEndian(volHeader.AsSpan(4 + 0x24), (ushort)(fc + 1));
      WriteBlock(image, imageStart, VolumeDirStartBlock, volHeader);
    }

    WriteBitmap(image, imageStart, bitmapPtr, bitmapBlocks, bitmap);
  }

  /// <summary>
  /// Removes a named file from the image. Returns true if found and removed.
  /// When <paramref name="wipeData"/> is true, data blocks are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var imageStart = DetectImageStart(image);
    var sanitized = SanitizeName(name);

    var volHeader = ReadBlock(image, imageStart, VolumeDirStartBlock);
    var totalBlocks = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x28));
    var bitmapPtr = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x26));
    if (bitmapPtr == 0) bitmapPtr = BitmapStartBlock;
    var bitmapBlocks = (totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
    var bitmap = ReadBitmap(image, imageStart, bitmapPtr, bitmapBlocks);

    var locator = LocateDirectoryEntry(image, imageStart, sanitized);
    if (!locator.Found) return false;

    var dirBuf = ReadBlock(image, imageStart, locator.Block);
    var entryOff = 4 + locator.IndexInBlock * EntrySize;
    var storageType = (dirBuf[entryOff + 0] >> 4) & 0x0F;
    var keyPointer = BinaryPrimitives.ReadUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x11));
    var eof = dirBuf[entryOff + 0x15] | (dirBuf[entryOff + 0x16] << 8) | (dirBuf[entryOff + 0x17] << 16);

    var dataBlocks = new List<int>();
    var indexBlocks = new List<int>();

    switch (storageType) {
      case 1: // Seedling — key is the single data block.
        dataBlocks.Add(keyPointer);
        break;
      case 2: { // Sapling — key is index block; pointers cover up to 256 data blocks.
        indexBlocks.Add(keyPointer);
        var idx = ReadBlock(image, imageStart, keyPointer);
        var dataBlockCount = (eof + BlockSize - 1) / BlockSize;
        for (var i = 0; i < dataBlockCount && i < 256; i++) {
          var b = idx[i] | (idx[256 + i] << 8);
          if (b != 0) dataBlocks.Add(b);
        }
        break;
      }
      case 3: { // Tree — key is master index; sub-indices cover 256 data blocks each.
        indexBlocks.Add(keyPointer);
        var master = ReadBlock(image, imageStart, keyPointer);
        var dataBlockCount = (eof + BlockSize - 1) / BlockSize;
        var indexCount = (dataBlockCount + 255) / 256;
        var dataIdx = 0;
        for (var si = 0; si < indexCount && si < 256; si++) {
          var sub = master[si] | (master[256 + si] << 8);
          if (sub == 0) { dataIdx += 256; continue; }
          indexBlocks.Add(sub);
          var subBlk = ReadBlock(image, imageStart, sub);
          var inThisSub = Math.Min(256, dataBlockCount - dataIdx);
          for (var di = 0; di < inThisSub; di++) {
            var b = subBlk[di] | (subBlk[256 + di] << 8);
            if (b != 0) dataBlocks.Add(b);
            dataIdx++;
          }
        }
        break;
      }
      default:
        // Directory or unknown — refuse to handle.
        return false;
    }

    if (wipeData) {
      var zero = new byte[BlockSize];
      foreach (var b in dataBlocks)
        if (b > 0 && b < totalBlocks) WriteBlock(image, imageStart, b, zero);
    }

    foreach (var b in dataBlocks)
      if (b > 0 && b < totalBlocks) bitmap[b] = true;
    foreach (var b in indexBlocks)
      if (b > 0 && b < totalBlocks) bitmap[b] = true;

    // Mark entry deleted: storage_type nibble = 0, name_length = 0.
    dirBuf[entryOff + 0] = 0;

    if (locator.Block == VolumeDirStartBlock) {
      var fc = BinaryPrimitives.ReadUInt16LittleEndian(dirBuf.AsSpan(4 + 0x24));
      if (fc > 0)
        BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(4 + 0x24), (ushort)(fc - 1));
      WriteBlock(image, imageStart, locator.Block, dirBuf);
    } else {
      WriteBlock(image, imageStart, locator.Block, dirBuf);
      var fc = BinaryPrimitives.ReadUInt16LittleEndian(volHeader.AsSpan(4 + 0x24));
      if (fc > 0)
        BinaryPrimitives.WriteUInt16LittleEndian(volHeader.AsSpan(4 + 0x24), (ushort)(fc - 1));
      WriteBlock(image, imageStart, VolumeDirStartBlock, volHeader);
    }

    WriteBitmap(image, imageStart, bitmapPtr, bitmapBlocks, bitmap);
    return true;
  }

  // ── Allocation planning ───────────────────────────────────────────────

  private sealed class AllocPlan {
    public List<(int Block, int SrcOffset, int Count)> DataWrites = [];
    public List<(int Block, byte[] LowBytes, byte[] HighBytes)> IndexWrites = [];
    public int? MasterIndexBlock;
    public byte[]? MasterLowBytes;
    public byte[]? MasterHighBytes;
  }

  private static (int StorageType, int KeyPointer, int BlocksUsed, AllocPlan Plan)
      AllocateStorage(bool[] bitmap, int totalBlocks, byte[] data) {
    var plan = new AllocPlan();

    if (data.Length == 0) {
      var key = AllocateBlock(bitmap, totalBlocks);
      // Seedling with empty key block — already zero.
      return (1, key, 1, plan);
    }

    if (data.Length <= BlockSize) {
      var key = AllocateBlock(bitmap, totalBlocks);
      plan.DataWrites.Add((key, 0, data.Length));
      return (1, key, 1, plan);
    }

    var dataBlockCount = (data.Length + BlockSize - 1) / BlockSize;

    if (dataBlockCount <= 256) {
      // Sapling.
      var indexKey = AllocateBlock(bitmap, totalBlocks);
      var dataBlocks = new int[dataBlockCount];
      for (var i = 0; i < dataBlockCount; i++) {
        dataBlocks[i] = AllocateBlock(bitmap, totalBlocks);
        var off = i * BlockSize;
        var take = Math.Min(BlockSize, data.Length - off);
        plan.DataWrites.Add((dataBlocks[i], off, take));
      }
      var lo = new byte[dataBlockCount];
      var hi = new byte[dataBlockCount];
      for (var i = 0; i < dataBlockCount; i++) {
        lo[i] = (byte)(dataBlocks[i] & 0xFF);
        hi[i] = (byte)((dataBlocks[i] >> 8) & 0xFF);
      }
      plan.IndexWrites.Add((indexKey, lo, hi));
      return (2, indexKey, dataBlockCount + 1, plan);
    }

    if (dataBlockCount > 256 * 256)
      throw new InvalidOperationException("ProDOS: file exceeds 32 MB tree capacity.");

    // Tree.
    var indexBlockCount = (dataBlockCount + 255) / 256;
    var masterKey = AllocateBlock(bitmap, totalBlocks);
    var subIndexBlocks = new int[indexBlockCount];
    var dataIdx = 0;
    for (var si = 0; si < indexBlockCount; si++) {
      subIndexBlocks[si] = AllocateBlock(bitmap, totalBlocks);
      var inThisSub = Math.Min(256, dataBlockCount - dataIdx);
      var subLo = new byte[inThisSub];
      var subHi = new byte[inThisSub];
      for (var di = 0; di < inThisSub; di++) {
        var dataBlock = AllocateBlock(bitmap, totalBlocks);
        var off = dataIdx * BlockSize;
        var take = Math.Min(BlockSize, data.Length - off);
        plan.DataWrites.Add((dataBlock, off, take));
        subLo[di] = (byte)(dataBlock & 0xFF);
        subHi[di] = (byte)((dataBlock >> 8) & 0xFF);
        dataIdx++;
      }
      plan.IndexWrites.Add((subIndexBlocks[si], subLo, subHi));
    }
    var masterLo = new byte[indexBlockCount];
    var masterHi = new byte[indexBlockCount];
    for (var si = 0; si < indexBlockCount; si++) {
      masterLo[si] = (byte)(subIndexBlocks[si] & 0xFF);
      masterHi[si] = (byte)((subIndexBlocks[si] >> 8) & 0xFF);
    }
    plan.MasterIndexBlock = masterKey;
    plan.MasterLowBytes = masterLo;
    plan.MasterHighBytes = masterHi;
    return (3, masterKey, 1 + indexBlockCount + dataBlockCount, plan);
  }

  // ── Block I/O ─────────────────────────────────────────────────────────

  private static long BlockOffset(int imageStart, int block) =>
    imageStart + (long)block * BlockSize;

  private static byte[] ReadBlock(Stream s, int imageStart, int block) {
    var buf = new byte[BlockSize];
    s.Position = BlockOffset(imageStart, block);
    var read = 0;
    while (read < BlockSize) {
      var n = s.Read(buf, read, BlockSize - read);
      if (n <= 0) break;
      read += n;
    }
    return buf;
  }

  private static void WriteBlock(Stream s, int imageStart, int block, byte[] data) {
    s.Position = BlockOffset(imageStart, block);
    s.Write(data, 0, BlockSize);
  }

  private static int DetectImageStart(Stream s) {
    if (s.Length < 64) return 0;
    var origPos = s.Position;
    s.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    s.ReadExactly(magic);
    s.Position = origPos;
    return magic.SequenceEqual(TwoImgMagic) ? 64 : 0;
  }

  // ── Bitmap helpers ────────────────────────────────────────────────────

  private static bool[] ReadBitmap(Stream s, int imageStart, int bitmapStart, int bitmapBlockCount) {
    var bytes = bitmapBlockCount * BlockSize;
    var raw = new byte[bytes];
    for (var i = 0; i < bitmapBlockCount; i++) {
      var blk = ReadBlock(s, imageStart, bitmapStart + i);
      Buffer.BlockCopy(blk, 0, raw, i * BlockSize, BlockSize);
    }
    var bits = new bool[bytes * 8];
    // Bit 7 of byte 0 = block 0; bit 0 of byte 0 = block 7. Bit SET = free.
    for (var b = 0; b < bytes; b++)
      for (var bit = 0; bit < 8; bit++)
        bits[b * 8 + bit] = (raw[b] & (0x80 >> bit)) != 0;
    return bits;
  }

  private static void WriteBitmap(Stream s, int imageStart, int bitmapStart, int bitmapBlockCount, bool[] bits) {
    var bytes = bitmapBlockCount * BlockSize;
    var raw = new byte[bytes];
    for (var b = 0; b < bytes; b++) {
      byte mask = 0;
      for (var bit = 0; bit < 8; bit++)
        if (b * 8 + bit < bits.Length && bits[b * 8 + bit])
          mask |= (byte)(0x80 >> bit);
      raw[b] = mask;
    }
    for (var i = 0; i < bitmapBlockCount; i++) {
      var blk = new byte[BlockSize];
      Buffer.BlockCopy(raw, i * BlockSize, blk, 0, BlockSize);
      WriteBlock(s, imageStart, bitmapStart + i, blk);
    }
  }

  private static int AllocateBlock(bool[] bitmap, int totalBlocks) {
    for (var b = 0; b < totalBlocks; b++) {
      if (bitmap[b]) {
        bitmap[b] = false;
        return b;
      }
    }
    throw new InvalidOperationException("ProDOS: out of free blocks.");
  }

  // ── Directory navigation ──────────────────────────────────────────────

  private readonly record struct DirSlot(bool Found, int Block, int IndexInBlock);

  private static DirSlot FindFreeDirectorySlot(Stream image, int imageStart) {
    var block = VolumeDirStartBlock;
    var visited = new HashSet<int>();
    var firstBlock = true;
    while (block != 0 && visited.Add(block)) {
      var buf = ReadBlock(image, imageStart, block);
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
      for (var i = 0; i < EntriesPerBlock; i++) {
        if (firstBlock && i == 0) continue; // volume header slot
        var eo = 4 + i * EntrySize;
        var storage = (buf[eo + 0] >> 4) & 0x0F;
        var len = buf[eo + 0] & 0x0F;
        if (storage == 0 || len == 0) return new DirSlot(true, block, i);
      }
      firstBlock = false;
      block = nextBlock;
    }
    return new DirSlot(false, 0, 0);
  }

  private readonly record struct DirLocator(bool Found, int Block, int IndexInBlock);

  private static DirLocator LocateDirectoryEntry(Stream image, int imageStart, string name) {
    var block = VolumeDirStartBlock;
    var visited = new HashSet<int>();
    var firstBlock = true;
    while (block != 0 && visited.Add(block)) {
      var buf = ReadBlock(image, imageStart, block);
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
      for (var i = 0; i < EntriesPerBlock; i++) {
        if (firstBlock && i == 0) continue;
        var eo = 4 + i * EntrySize;
        var storage = (buf[eo + 0] >> 4) & 0x0F;
        var len = buf[eo + 0] & 0x0F;
        if (storage == 0 || len == 0) continue;
        var entryName = Encoding.ASCII.GetString(buf, eo + 1, len);
        if (entryName == name) return new DirLocator(true, block, i);
      }
      firstBlock = false;
      block = nextBlock;
    }
    return new DirLocator(false, 0, 0);
  }

  // ── Name sanitisation (mirrors ProDosWriter) ─────────────────────────

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = Path.GetFileName(raw).ToUpperInvariant();
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.')
        sb.Append(c);
      else
        sb.Append('.');
    }
    var clean = sb.ToString().TrimStart('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    if (clean.Length == 0) clean = "F" + sb;
    if (clean.Length > 15) clean = clean[^15..];
    if (clean.Length == 0 || !(clean[0] >= 'A' && clean[0] <= 'Z'))
      clean = "F" + (clean.Length > 14 ? clean[^14..] : clean);
    if (clean.Length > 15) clean = clean[..15];
    return clean;
  }
}
