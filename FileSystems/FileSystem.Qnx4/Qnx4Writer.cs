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

  // Reserved layout (LBA). Block 1 is the superblock — four inode entries
  // describing the volume — so the root directory cannot live there.
  private const uint BootBlock = 0;
  private const uint SuperBlock = Qnx4Layout.SuperBlock;   // 1
  private const uint RootDirStart = 2;          // blocks 2..5
  private const uint BitmapBlock = 6;
  private const uint InodesBlock = 7;
  private const uint FirstDataBlock = 8;

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
    // The root directory holds .bitmap and .inodes as well as the user files;
    // the driver refuses to mount a volume whose root has no .bitmap in it.
    const int maxFiles = (InodesPerBlock * RootDirBlocks) - 2;
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

    // === Block 1: the superblock — four inode entries, not a directory ===
    // The first of them describes the root directory, and it is the one the
    // driver reads as the root inode.
    var rootEntries = 2 + this._files.Count;      // .bitmap, .inodes, then files
    WriteInode(
      image, SuperBlock, entryIndex: 0,
      name: "/", size: (uint)(InodeSize * rootEntries),
      firstExtentBlock: Qnx4Layout.ExtentValueFor(RootDirStart), extentBlockCount: RootDirBlocks,
      mode: (ushort)(SIfdir | PermDir), status: FileUsed);
    WriteInode(
      image, SuperBlock, entryIndex: 1,
      name: Qnx4Layout.InodesName, size: BlockSize,
      firstExtentBlock: Qnx4Layout.ExtentValueFor(InodesBlock), extentBlockCount: 1,
      mode: (ushort)(SIfreg | PermFile), status: FileUsed);
    WriteInode(
      image, SuperBlock, entryIndex: 2,
      name: ".boot", size: 0,
      firstExtentBlock: 0, extentBlockCount: 0,
      mode: (ushort)(SIfreg | PermFile), status: 0);
    WriteInode(
      image, SuperBlock, entryIndex: 3,
      name: ".altboot", size: 0,
      firstExtentBlock: 0, extentBlockCount: 0,
      mode: (ushort)(SIfreg | PermFile), status: 0);

    // === Blocks 2..5: the root directory itself ===
    // .bitmap comes first because the driver scans the root for it and will
    // not mount a volume that has none.
    WriteInode(
      image, RootDirStart, entryIndex: 0,
      name: Qnx4Layout.BitmapName, size: BlockSize,
      firstExtentBlock: Qnx4Layout.ExtentValueFor(BitmapBlock), extentBlockCount: 1,
      mode: (ushort)(SIfreg | PermFile), status: FileUsed);
    WriteInode(
      image, RootDirStart, entryIndex: 1,
      name: Qnx4Layout.InodesName, size: BlockSize,
      firstExtentBlock: Qnx4Layout.ExtentValueFor(InodesBlock), extentBlockCount: 1,
      mode: (ushort)(SIfreg | PermFile), status: FileUsed);

    var dataWrites = new List<(long Offset, FileEntry Entry)>();
    for (var i = 0; i < this._files.Count; i++) {
      var entry = this._files[i];
      var (startBlk, blkCount) = fileExtents[i];
      WriteInode(
        image, RootDirStart, entryIndex: 2 + i,
        name: entry.Name, size: (uint)Math.Min(entry.Size, uint.MaxValue),
        firstExtentBlock: Qnx4Layout.ExtentValueFor(startBlk), extentBlockCount: blkCount,
        mode: (ushort)(SIfreg | PermFile), status: FileUsed);
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

    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset + Qnx4Layout.InSize, 4), size);
    BinaryPrimitives.WriteUInt32LittleEndian(
      image.At(offset + Qnx4Layout.InExtentBlock, 4), firstExtentBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(
      image.At(offset + Qnx4Layout.InExtentSize, 4), extentBlockCount);

    // di_num_xtnts counts the extents the file has, and one is one — not zero.
    BinaryPrimitives.WriteUInt16LittleEndian(
      image.At(offset + Qnx4Layout.InNumExtents, 2), extentBlockCount == 0 ? (ushort)0 : (ushort)1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(offset + Qnx4Layout.InMode, 2), mode);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(offset + Qnx4Layout.InNlink, 2), 1);
    image[offset + Qnx4Layout.InStatus] = status;
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
    // Superblock and root dir cluster
    MarkBit(image, bitmapOffset, SuperBlock);
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
