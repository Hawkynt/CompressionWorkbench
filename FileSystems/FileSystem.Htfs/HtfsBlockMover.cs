#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Htfs;

/// <summary>
/// Moves a file's blocks inside an HTFS volume and repoints its inode.
/// </summary>
/// <remarks>
/// <para>HTFS stores a file as one contiguous run and records where it starts
/// in a single field of the file's inode. There is no chain to relink and no
/// allocation bitmap to keep in step — the superblock's size fields describe
/// the volume, not which blocks are taken — so relocating a file is the copy
/// plus one four-byte write.</para>
///
/// <para>That is what lets the defragmenter plan moves here instead of reading
/// every file out and writing a fresh volume: a rebuild costs the whole payload
/// to fix a few misplaced runs.</para>
/// </remarks>
public sealed class HtfsBlockMover : IFilesystemBlockMover {

  /// <summary>Offset of the first-block field inside an inode.</summary>
  private const int InodeFirstBlockOffset = 24;

  private int _blockSize = HtfsWriter.DefaultBlockSize;
  private int _inodeStartBlock;
  private int _inodesPerBlock;

  /// <summary>Reads the geometry the volume was laid out with.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._blockSize = DetectBlockSize(image);
    this._inodesPerBlock = this._blockSize / HtfsWriter.InodeSize;
    this._inodeStartBlock = HtfsWriter.SuperblockOffset / this._blockSize + 1;
  }

  /// <summary>Block size in bytes, as the superblock's volume size implies it.</summary>
  public int BlockSize => this._blockSize;

  /// <summary>
  /// First byte a file may occupy: past the boot sector, the superblock and the
  /// inode table.
  /// </summary>
  public long FirstDataByte {
    get {
      var inodeStart = (long)this._inodeStartBlock * this._blockSize;
      return inodeStart + (long)this._inodeTableBlocks * this._blockSize;
    }
  }

  private int _inodeTableBlocks;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._blockSize == 0) this.Init(image);

    var inode = FindInode(image, fileName);
    if (inode <= 0)
      throw new InvalidOperationException($"HTFS: no inode for '{fileName}'.");

    var newBlock = (uint)(newOffset / this._blockSize);
    Span<byte> field = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(field, newBlock);
    image.Position = this.InodeOffset(inode) + InodeFirstBlockOffset;
    image.Write(field);
    image.Flush();
  }

  /// <summary>Byte offset of an inode's record in the inode table.</summary>
  private long InodeOffset(int inode) {
    var blockOffset = (inode - 2) / this._inodesPerBlock;
    var slotOffset = (inode - 2) % this._inodesPerBlock;
    return ((long)this._inodeStartBlock + blockOffset) * this._blockSize
         + (long)slotOffset * HtfsWriter.InodeSize;
  }

  /// <summary>The inode number the directory tree gives this path, or -1.</summary>
  private int FindInode(Stream image, string fileName) {
    image.Position = 0;
    var reader = new HtfsReader(image);
    foreach (var entry in reader.Entries)
      if (string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase))
        return entry.Inode;
    return -1;
  }

  /// <summary>
  /// The block size the volume was written with. The superblock records the
  /// volume's size in blocks, so the size whose product matches the image is
  /// the one it was laid out with.
  /// </summary>
  private int DetectBlockSize(Stream image) {
    var header = new byte[Math.Min(image.Length, 4096)];
    image.Position = 0;
    image.ReadExactly(header, 0, header.Length);
    var superblock = HtfsSuperblock.TryParse(header);
    if (!superblock.Valid)
      throw new InvalidDataException("HTFS: the superblock does not parse.");
    this._inodeTableBlocks = (int)superblock.Isize;
    foreach (var candidate in new[] { 512, 1024, 2048 }) {
      var implied = (long)superblock.Fsize * candidate;
      if (implied >= image.Length - candidate && implied <= image.Length + candidate)
        return candidate;
    }
    return HtfsWriter.DefaultBlockSize;
  }
}
