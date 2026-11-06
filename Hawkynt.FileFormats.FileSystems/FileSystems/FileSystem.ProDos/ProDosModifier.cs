#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ProDos;

/// <summary>
/// Random-access in-place modifier for Apple ProDOS block-ordered images
/// (<c>.po</c> and <c>.2mg</c>). Reads and writes only the directory chains on
/// the path, the bitmap, and the new file's index + data blocks — never the
/// whole image.
/// <para>
/// A name with '/' separators is placed in real ProDOS subdirectories: each
/// missing folder becomes a storage-type $D entry in its parent pointing at a
/// one-block directory whose key block starts with a $E subdirectory header
/// (parent pointer, parent entry number, entry length $27, $0D entries per
/// block). A full subdirectory grows by a chained block, and its entry's
/// blocks_used and EOF follow; the volume directory has a fixed size, as in
/// ProDOS. Every write is staged and lands only after all checks pass, so a
/// refused edit leaves the image byte-identical.
/// </para>
/// </summary>
public static class ProDosModifier {

  private const int BlockSize = ProDosReader.BlockSize;     // 512
  private const int VolumeDirStartBlock = ProDosReader.VolumeDirStartBlock;  // 2
  private const int EntriesPerBlock = ProDosReader.EntriesPerBlock;          // 13
  private const int EntrySize = ProDosReader.EntrySize;     // 39
  private const int BitmapStartBlock = 6;

  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  /// <summary>Staged block view of the image: reads see pending writes; nothing reaches the stream before <see cref="Commit"/>.</summary>
  private sealed class Disk {
    private readonly Stream _image;
    private readonly int _start;
    private readonly Dictionary<int, byte[]> _pending = [];
    public bool[] Bitmap { get; }
    public int TotalBlocks { get; }
    private readonly int _bitmapPtr, _bitmapBlocks;

    public Disk(Stream image) {
      _image = image;
      _start = DetectImageStart(image);
      var vol = ReadBlock(image, _start, VolumeDirStartBlock);
      TotalBlocks = BinaryPrimitives.ReadUInt16LittleEndian(vol.AsSpan(4 + ProDosReader.HeaderTotalBlocksOrParentEntry));
      _bitmapPtr = BinaryPrimitives.ReadUInt16LittleEndian(vol.AsSpan(4 + ProDosReader.HeaderBitmapOrParentPointer));
      if (_bitmapPtr == 0) _bitmapPtr = BitmapStartBlock;
      _bitmapBlocks = (TotalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
      Bitmap = ReadBitmap(image, _start, _bitmapPtr, _bitmapBlocks);
    }

    public byte[] Read(int block) => _pending.TryGetValue(block, out var b) ? (byte[])b.Clone() : ReadBlock(_image, _start, block);
    public void Write(int block, byte[] data) => _pending[block] = (byte[])data.Clone();
    public int Allocate() => AllocateBlock(Bitmap, TotalBlocks);

    public void Commit() {
      foreach (var (block, data) in _pending.OrderBy(p => p.Key)) WriteBlock(_image, _start, block, data);
      WriteBitmap(_image, _start, _bitmapPtr, _bitmapBlocks, Bitmap);
    }
  }

  private static string[] SplitPath(string name) {
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 0 ? [name] : parts;
  }

  /// <summary>
  /// Adds a file to an existing image, creating any missing folders on its path as
  /// real subdirectories. Caller is responsible for ensuring the name does not
  /// already exist; use <see cref="RemoveFile"/> first for replace-by-name semantics.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data, byte fileType = 0x06) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var disk = new Disk(image);
    var parts = SplitPath(name);
    var dirKey = VolumeDirStartBlock;
    for (var i = 0; i < parts.Length - 1; ++i) {
      var folder = SanitizeName(parts[i]);
      var found = Locate(disk, dirKey, folder);
      if (found.Found) {
        if (found.StorageType != 0xD) throw new InvalidOperationException($"ProDOS: '{folder}' on the path is a file, not a directory.");
        dirKey = found.KeyPointer;
      } else {
        dirKey = CreateSubdirectory(disk, dirKey, folder);
      }
    }
    var sanitized = SanitizeName(parts[^1]);

    var (storageType, keyPointer, blocksUsed, plan) = AllocateStorage(disk.Bitmap, disk.TotalBlocks, data);
    foreach (var (block, offset, count) in plan.DataWrites) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(data, offset, buf, 0, count);
      disk.Write(block, buf);
    }
    foreach (var (block, lowBytes, highBytes) in plan.IndexWrites) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(lowBytes, 0, buf, 0, lowBytes.Length);
      Buffer.BlockCopy(highBytes, 0, buf, 256, highBytes.Length);
      disk.Write(block, buf);
    }
    if (plan.MasterIndexBlock is int master) {
      var buf = new byte[BlockSize];
      Buffer.BlockCopy(plan.MasterLowBytes!, 0, buf, 0, plan.MasterLowBytes!.Length);
      Buffer.BlockCopy(plan.MasterHighBytes!, 0, buf, 256, plan.MasterHighBytes!.Length);
      disk.Write(master, buf);
    }

    var slot = TakeSlot(disk, dirKey);
    var dirBuf = disk.Read(slot.Block);
    var entryOff = ProDosReader.EntryOffsetInBlock(slot.IsFirstBlockOfChain, slot.IndexInBlock);
    WriteEntryName(dirBuf, entryOff, storageType, sanitized);
    dirBuf[entryOff + 0x10] = fileType;
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x11), (ushort)keyPointer);
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x13), (ushort)blocksUsed);
    WriteEof(dirBuf, entryOff, data.Length);
    dirBuf[entryOff + 0x1E] = 0xE3;
    BinaryPrimitives.WriteUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x25), (ushort)dirKey);
    disk.Write(slot.Block, dirBuf);
    AdjustFileCount(disk, dirKey, +1);
    disk.Commit();
  }

  /// <summary>
  /// Removes a named file (a '/'-separated path reaches into subdirectories) from the
  /// image. Returns true if found and removed. When <paramref name="wipeData"/> is true,
  /// data blocks are zeroed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var disk = new Disk(image);
    var parts = SplitPath(name);
    var dirKey = VolumeDirStartBlock;
    for (var i = 0; i < parts.Length - 1; ++i) {
      var found = Locate(disk, dirKey, SanitizeName(parts[i]));
      if (!found.Found || found.StorageType != 0xD) return false;
      dirKey = found.KeyPointer;
    }
    var locator = Locate(disk, dirKey, SanitizeName(parts[^1]));
    if (!locator.Found) return false;

    var dirBuf = disk.Read(locator.Block);
    var entryOff = ProDosReader.EntryOffsetInBlock(locator.IsFirstBlockOfChain, locator.IndexInBlock);
    var storageType = (dirBuf[entryOff + 0] >> 4) & 0x0F;
    var keyPointer = BinaryPrimitives.ReadUInt16LittleEndian(dirBuf.AsSpan(entryOff + 0x11));
    var eof = dirBuf[entryOff + 0x15] | (dirBuf[entryOff + 0x16] << 8) | (dirBuf[entryOff + 0x17] << 16);
    var totalBlocks = disk.TotalBlocks;

    var dataBlocks = new List<int>();
    var indexBlocks = new List<int>();
    switch (storageType) {
      case 1: // Seedling — key is the single data block.
        dataBlocks.Add(keyPointer);
        break;
      case 2: { // Sapling — key is index block; pointers cover up to 256 data blocks.
        indexBlocks.Add(keyPointer);
        var idx = disk.Read(keyPointer);
        var dataBlockCount = (eof + BlockSize - 1) / BlockSize;
        for (var i = 0; i < dataBlockCount && i < 256; i++) {
          var b = idx[i] | (idx[256 + i] << 8);
          if (b != 0) dataBlocks.Add(b);
        }
        break;
      }
      case 3: { // Tree — key is master index; sub-indices cover 256 data blocks each.
        indexBlocks.Add(keyPointer);
        var masterBlock = disk.Read(keyPointer);
        var dataBlockCount = (eof + BlockSize - 1) / BlockSize;
        var indexCount = (dataBlockCount + 255) / 256;
        var dataIdx = 0;
        for (var si = 0; si < indexCount && si < 256; si++) {
          var sub = masterBlock[si] | (masterBlock[256 + si] << 8);
          if (sub == 0) { dataIdx += 256; continue; }
          indexBlocks.Add(sub);
          var subBlk = disk.Read(sub);
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
        if (b > 0 && b < totalBlocks) disk.Write(b, zero);
    }
    foreach (var b in dataBlocks.Concat(indexBlocks))
      if (b > 0 && b < totalBlocks) disk.Bitmap[b] = true;

    dirBuf = disk.Read(locator.Block);
    dirBuf[entryOff + 0] = 0; // storage_type nibble = 0, name_length = 0: deleted
    disk.Write(locator.Block, dirBuf);
    AdjustFileCount(disk, dirKey, -1);
    disk.Commit();
    return true;
  }

  private static void WriteEntryName(byte[] buf, int entryOff, int storageType, string name) {
    buf.AsSpan(entryOff, EntrySize).Clear();
    buf[entryOff + 0] = (byte)((storageType << 4) | (name.Length & 0x0F));
    for (var i = 0; i < name.Length && i < 15; i++) buf[entryOff + 1 + i] = (byte)name[i];
  }

  private static void WriteEof(byte[] buf, int entryOff, int eof) {
    buf[entryOff + 0x15] = (byte)(eof & 0xFF);
    buf[entryOff + 0x16] = (byte)((eof >> 8) & 0xFF);
    buf[entryOff + 0x17] = (byte)((eof >> 16) & 0xFF);
  }

  private static void AdjustFileCount(Disk disk, int dirKey, int delta) {
    var key = disk.Read(dirKey);
    var fc = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(4 + ProDosReader.HeaderFileCount));
    BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(4 + ProDosReader.HeaderFileCount), (ushort)Math.Max(0, fc + delta));
    disk.Write(dirKey, key);
  }

  /// <summary>Creates a one-block subdirectory <paramref name="name"/> in the directory keyed at <paramref name="parentKey"/>; returns its key block.</summary>
  private static int CreateSubdirectory(Disk disk, int parentKey, string name) {
    var slot = TakeSlot(disk, parentKey);
    var key = disk.Allocate();

    var header = new byte[BlockSize]; // prev = next = 0
    const int h = 4;
    header[h] = (byte)((0xE << 4) | (name.Length & 0x0F));
    for (var i = 0; i < name.Length && i < 15; i++) header[h + 1 + i] = (byte)name[i];
    header[h + 0x10] = 0x75; // ProDOS expects $75 here in a subdirectory header
    header[h + ProDosReader.HeaderAccess] = 0xE3;
    header[h + ProDosReader.HeaderEntryLength] = EntrySize;
    header[h + ProDosReader.HeaderEntriesPerBlock] = EntriesPerBlock;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(h + ProDosReader.HeaderBitmapOrParentPointer), (ushort)slot.Block);
    header[h + ProDosReader.HeaderTotalBlocksOrParentEntry] = (byte)(slot.IndexInBlock + 1);
    header[h + ProDosReader.HeaderParentEntryLength] = EntrySize;
    disk.Write(key, header);

    var parentBuf = disk.Read(slot.Block);
    var entryOff = ProDosReader.EntryOffsetInBlock(slot.IsFirstBlockOfChain, slot.IndexInBlock);
    WriteEntryName(parentBuf, entryOff, 0xD, name);
    parentBuf[entryOff + 0x10] = 0x0F; // DIR
    BinaryPrimitives.WriteUInt16LittleEndian(parentBuf.AsSpan(entryOff + 0x11), (ushort)key);
    BinaryPrimitives.WriteUInt16LittleEndian(parentBuf.AsSpan(entryOff + 0x13), 1);
    WriteEof(parentBuf, entryOff, BlockSize);
    parentBuf[entryOff + 0x1E] = 0xE3;
    BinaryPrimitives.WriteUInt16LittleEndian(parentBuf.AsSpan(entryOff + 0x25), (ushort)parentKey);
    disk.Write(slot.Block, parentBuf);
    AdjustFileCount(disk, parentKey, +1);
    return key;
  }

  // ── Directory navigation ──────────────────────────────────────────────

  private readonly record struct DirSlot(bool Found, int Block, int IndexInBlock, bool IsFirstBlockOfChain);

  private readonly record struct DirLocator(bool Found, int Block, int IndexInBlock, bool IsFirstBlockOfChain, int StorageType, int KeyPointer);

  private static IEnumerable<(int Block, byte[] Buffer, bool First)> Chain(Disk disk, int dirKey) {
    var visited = new HashSet<int>();
    var block = dirKey;
    var first = true;
    while (block != 0 && block < disk.TotalBlocks && visited.Add(block)) {
      var buf = disk.Read(block);
      yield return (block, buf, first);
      first = false;
      block = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2));
    }
  }

  private static DirLocator Locate(Disk disk, int dirKey, string name) {
    foreach (var (block, buf, first) in Chain(disk, dirKey))
      for (var i = first ? 1 : 0; i < EntriesPerBlock; i++) {
        var eo = ProDosReader.EntryOffsetInBlock(first, i);
        var storage = (buf[eo] >> 4) & 0x0F;
        var len = buf[eo] & 0x0F;
        if (storage == 0 || len == 0) continue;
        if (Encoding.ASCII.GetString(buf, eo + 1, len) == name)
          return new DirLocator(true, block, i, first, storage, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(eo + 0x11)));
      }
    return default;
  }

  /// <summary>
  /// A free entry slot in the directory keyed at <paramref name="dirKey"/>. A full
  /// subdirectory grows by one chained block; the volume directory cannot grow.
  /// </summary>
  private static DirSlot TakeSlot(Disk disk, int dirKey) {
    var last = 0;
    foreach (var (block, buf, first) in Chain(disk, dirKey)) {
      last = block;
      for (var i = first ? 1 : 0; i < EntriesPerBlock; i++) {
        var eo = ProDosReader.EntryOffsetInBlock(first, i);
        if (((buf[eo] >> 4) & 0x0F) == 0 || (buf[eo] & 0x0F) == 0) return new DirSlot(true, block, i, first);
      }
    }
    if (dirKey == VolumeDirStartBlock)
      throw new InvalidOperationException("ProDOS: volume directory full (no free entry slot).");

    var added = disk.Allocate();
    var fresh = new byte[BlockSize];
    BinaryPrimitives.WriteUInt16LittleEndian(fresh.AsSpan(0), (ushort)last);
    disk.Write(added, fresh);
    var tail = disk.Read(last);
    BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(2), (ushort)added);
    disk.Write(last, tail);

    // The subdirectory's own entry in its parent counts the blocks.
    var key = disk.Read(dirKey);
    var parentBlock = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(4 + ProDosReader.HeaderBitmapOrParentPointer));
    var parentEntry = key[4 + ProDosReader.HeaderTotalBlocksOrParentEntry];
    if (parentBlock != 0 && parentEntry >= 1) {
      var parent = disk.Read(parentBlock);
      var eo = 4 + (parentEntry - 1) * EntrySize;
      var used = BinaryPrimitives.ReadUInt16LittleEndian(parent.AsSpan(eo + 0x13)) + 1;
      BinaryPrimitives.WriteUInt16LittleEndian(parent.AsSpan(eo + 0x13), (ushort)used);
      WriteEof(parent, eo, used * BlockSize);
      disk.Write(parentBlock, parent);
    }
    return new DirSlot(true, added, 0, false);
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
