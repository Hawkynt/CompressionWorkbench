#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Qnx4;

/// <summary>
/// Emits valid QNX4 file-system images (WORM — write-once, no in-place mutation).
///
/// On-disk layout (matching what the Linux qnx4 driver expects):
/// <code>
///   Block 0        boot block (zeroed; 512 bytes)
///   Blocks 1-4     root directory cluster (4 contiguous blocks, 32 × 64-byte
///                  inode entries). Entry 0 is the root inode pointing to itself
///                  (xtnt=blk1+4); entries 1..2 are the QNX4 system files
///                  ".bitmap" and ".inodes"; entries 3..N are user files.
///   Block 5        ".bitmap" (block allocation bitmap, 1 bit per block, LSB
///                  first; reserved/used blocks marked).
///   Block 6        ".inodes" (additional inode storage, zeroed — we keep all
///                  user inodes inline in the root cluster).
///   Block 7..      user file data, each file gets a single contiguous extent
///                  rounded up to whole 512-byte blocks.
/// </code>
///
/// <para>Inode status byte (offset 0x3D in 64-byte entry):</para>
/// <list type="bullet">
///   <item><c>QNX4_FILE_USED = 0x01</c> — short-name file (≤ 16 bytes filename)</item>
///   <item><c>QNX4_FILE_LINK = 0x08</c> — long-name entry (uses next 2 slots)</item>
/// </list>
/// We use <c>0x01</c> for plain user files (16-byte short names) and <c>0x09</c>
/// (USED|LINK) for the root inode itself — this matches the on-disk pattern
/// produced by historical QNX4 systems and what the Linux <c>qnx4</c> driver
/// validates.
///
/// <para>Spec source: <c>linux/fs/qnx4/{qnx4.h,inode.c,dir.c,namei.c}</c>.</para>
/// </summary>
public sealed class Qnx4Writer {

  private const int BlockSize = Qnx4Reader.BlockSize;
  private const int InodeSize = Qnx4Reader.InodeSize;
  private const int InodesPerBlock = BlockSize / InodeSize; // 8
  private const int RootDirBlocks = 4;
  private const int MaxShortName = 16;

  // Reserved layout (LBA):
  private const uint BootBlock = 0;
  private const uint RootDirStart = 1;          // blocks 1..4
  private const uint BitmapBlock = 5;
  private const uint InodesBlock = 6;
  private const uint FirstDataBlock = 7;

  // QNX4 inode status flags (from linux/fs/qnx4/qnx4.h).
  private const byte FileUsed = 0x01;
  private const byte FileLink = 0x08;

  // di_mode bits (standard UNIX).
  private const ushort SIfdir = 0x4000;
  private const ushort SIfreg = 0x8000;
  // perm 0755 / 0644
  private const ushort PermFile = 0x01A4;
  private const ushort PermDir = 0x01ED;

  /// <summary>A file's payload: held inline, or opened on demand when it is too large to hold.</summary>
  private readonly record struct FileEntry(string Name, long Size, byte[]? Data, Func<Stream>? Opener);

  private readonly List<FileEntry> _files = [];

  /// <summary>Adds a single regular file to be written into the root directory.
  /// Names are truncated to 16 bytes (QNX4 short-name limit) and any path
  /// separators are stripped (we flatten — QNX4 WORM doesn't emit subdirs).</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (string.IsNullOrEmpty(leaf)) return;
    if (leaf is "." or "..") return;
    // Truncate to 16-byte short-name slot. UTF-8 may slice a multi-byte char;
    // we fall back to ASCII-safe truncation.
    var bytes = Encoding.UTF8.GetBytes(leaf);
    if (bytes.Length > MaxShortName) bytes = bytes.AsSpan(0, MaxShortName).ToArray();
    var trimmed = Encoding.UTF8.GetString(bytes).TrimEnd('�');
    this._files.Add(new FileEntry(trimmed, data.LongLength, data, null));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (string.IsNullOrEmpty(leaf) || leaf is "." or "..") return;
    var bytes = Encoding.UTF8.GetBytes(leaf);
    if (bytes.Length > MaxShortName) bytes = bytes.AsSpan(0, MaxShortName).ToArray();
    this._files.Add(new FileEntry(Encoding.UTF8.GetString(bytes).TrimEnd('�'), size, null, openStream));
  }

  /// <summary>Serialises the accumulated files into a QNX4 image.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Cap: each root-cluster block holds 8 inodes; subtract entry 0 (root
    // self-reference) and 2 system files. Across 4 blocks: 32 - 3 = 29 user
    // files. WORM scope: more than 29 files needs subdirs which we don't emit.
    const int maxFiles = (InodesPerBlock * RootDirBlocks) - 3;
    if (this._files.Count > maxFiles)
      throw new InvalidOperationException(
        $"QNX4 WORM scope: max {maxFiles} files in flat root (got {this._files.Count}). " +
        "Subdirectory emission would need IArchiveModifiable, which this writer does not implement.");

    // Plan: assign each file a contiguous extent starting at FirstDataBlock.
    var fileExtents = new (uint StartBlock, uint BlockCount)[this._files.Count];
    var nextBlock = FirstDataBlock;
    for (var i = 0; i < this._files.Count; i++) {
      var sizeBytes = this._files[i].Size;
      var blocks = (uint)((sizeBytes + BlockSize - 1) / BlockSize);
      if (blocks == 0) blocks = 1; // even zero-byte files reserve one block to satisfy reader extent walk
      fileExtents[i] = (nextBlock, blocks);
      nextBlock += blocks;
    }

    var totalBlocks = nextBlock;
    // Only the blocks the filesystem actually populates are held: file payloads
    // are placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BlockSize, (long)totalBlocks * BlockSize);

    // === Block 0: boot block — zeroed (no QNX4 boot magic; harmless). ===
    // The Linux driver does not validate the boot block.

    // === Blocks 1-4: root directory cluster (32 inodes) ===
    // Entry 0: root inode "/"  — points to itself (the root dir cluster)
    WriteInode(
      image, RootDirStart, entryIndex: 0,
      name: "/", size: (uint)(InodeSize * (3 + this._files.Count)), // dir size = entries × 64
      firstExtentBlock: RootDirStart, extentBlockCount: RootDirBlocks,
      mode: (ushort)(SIfdir | PermDir),
      status: (byte)(FileUsed | FileLink) // 0x09 — matches historical QNX4 root entries
    );

    // Entry 1: .bitmap — system file holding the block allocation bitmap
    WriteInode(
      image, RootDirStart, entryIndex: 1,
      name: ".bitmap", size: BlockSize,
      firstExtentBlock: BitmapBlock, extentBlockCount: 1,
      mode: (ushort)(SIfreg | PermFile),
      status: FileUsed
    );

    // Entry 2: .inodes — overflow inode storage (kept empty by this writer)
    WriteInode(
      image, RootDirStart, entryIndex: 2,
      name: ".inodes", size: BlockSize,
      firstExtentBlock: InodesBlock, extentBlockCount: 1,
      mode: (ushort)(SIfreg | PermFile),
      status: FileUsed
    );

    // Entries 3..N: user files
    var dataWrites = new List<(long Offset, FileEntry Entry)>();
    for (var i = 0; i < this._files.Count; i++) {
      var entry = this._files[i];
      var name = entry.Name;
      var (startBlk, blkCount) = fileExtents[i];
      WriteInode(
        image, RootDirStart, entryIndex: 3 + i,
        name: name, size: (uint)Math.Min(entry.Size, uint.MaxValue),
        firstExtentBlock: startBlk, extentBlockCount: blkCount,
        mode: (ushort)(SIfreg | PermFile),
        status: FileUsed
      );
      dataWrites.Add(((long)startBlk * BlockSize, entry));
    }

    // === Block 5: .bitmap — mark reserved + used blocks ===
    BuildBitmap(image, totalBlocks, fileExtents);

    // === Block 6: .inodes — left zero ===
    // (already zero from initialisation)

    if (output.CanSeek) {
      var basePosition = output.Position;
      image.WriteTo(output);
      WriteEntryData(output, dataWrites, basePosition);
      output.Position = basePosition + image.TotalBytes;
    } else {
      var full = image.Materialise();
      using var target = new MemoryStream(full, writable: true);
      WriteEntryData(target, dataWrites, 0);
      output.Write(full, 0, full.Length);
    }
    output.Flush();
  }

  /// <summary>Copies each entry's bytes into the extent it was allocated.</summary>
  private static void WriteEntryData(Stream output, List<(long Offset, FileEntry Entry)> dataWrites, long basePosition) {
    var buffer = new byte[64 * 1024];
    foreach (var (offset, entry) in dataWrites) {
      if (entry.Size <= 0) continue;
      output.Position = basePosition + offset;
      if (entry.Data is { Length: > 0 } inline) { output.Write(inline, 0, inline.Length); continue; }

      using var src = entry.Opener!();
      var remaining = entry.Size;
      while (remaining > 0) {
        var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (n <= 0) break;
        output.Write(buffer, 0, n);
        remaining -= n;
      }
    }
  }

  private static void WriteInode(
      SparseBlockImage image, uint blockBase, int entryIndex,
      string name, uint size,
      uint firstExtentBlock, uint extentBlockCount,
      ushort mode, byte status) {

    // entryIndex spans 0..31 across the 4-block root cluster.
    var blockOffset = entryIndex / InodesPerBlock;
    var slotInBlock = entryIndex % InodesPerBlock;
    var offset = (long)(blockBase + blockOffset) * BlockSize + slotInBlock * InodeSize;

    // di_fname (16 bytes, NUL-padded ASCII/UTF-8)
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var nameLen = Math.Min(nameBytes.Length, MaxShortName);
    image.Write(offset, nameBytes.AsSpan(0, nameLen));

    // di_size (offset 0x10, 4 bytes LE)
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset + 0x10, 4), size);

    // di_first_xtnt: xtnt_blk (0x14) + xtnt_size (0x18), each u32 LE
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset + 0x14, 4), firstExtentBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset + 0x18, 4), extentBlockCount);

    // di_num_xtnts (0x1C, u32 LE) — 0 = first extent only
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset + 0x1C, 4), 0u);

    // di_mode (0x20, u16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(offset + 0x20, 2), mode);

    // di_uid (0x22) / di_gid (0x24) — leave as 0
    // di_ftime (0x26) / di_mtime (0x2A) / di_atime (0x2E) / di_ctime (0x32) — leave as 0
    // di_zero (0x36..0x3B) — already zero
    // di_type (0x3C) — leave 0
    // di_status (0x3D) — required
    image[offset + 0x3D] = status;
  }

  /// <summary>Marks the on-disk bitmap (block 5) so blocks 0..(used-1) are flagged
  /// allocated. QNX4 stores 1 bit per block, LSB-first within each byte.</summary>
  private static void BuildBitmap(SparseBlockImage image, uint totalBlocks, (uint StartBlock, uint BlockCount)[] fileExtents) {
    var bitmapOffset = (long)BitmapBlock * BlockSize;
    const int bitmapCapacity = BlockSize * 8;

    static void MarkBit(SparseBlockImage img, long baseOffset, uint blk) {
      if (blk >= bitmapCapacity) return;
      img[baseOffset + (blk >> 3)] |= (byte)(1 << (int)(blk & 7));
    }

    // Boot
    MarkBit(image, bitmapOffset, BootBlock);
    // Root dir cluster
    for (var b = 0u; b < RootDirBlocks; b++) MarkBit(image, bitmapOffset, RootDirStart + b);
    // System files
    MarkBit(image, bitmapOffset, BitmapBlock);
    MarkBit(image, bitmapOffset, InodesBlock);
    // User file extents
    foreach (var (start, count) in fileExtents)
      for (var b = 0u; b < count; b++) MarkBit(image, bitmapOffset, start + b);
    _ = totalBlocks; // reserved for future "free block count" header
  }
}
