#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Qnx4;

/// <summary>
/// Reader for the QNX4 file system (1991-2001, QNX Software Systems Inc.). QNX4
/// uses 512-byte blocks and represents each file as a chain of contiguous
/// extents — each extent is described by an `xtnt_t` record (first block +
/// block count).
///
/// On-disk layout (little-endian):
///   Block 0    boot sector (variable signature)
///   Block 1    root directory cluster (4 blocks of 64-byte inode entries)
///
/// Inode entry (64 bytes per linux/fs/qnx4/qnx4.h's qnx4_inode_entry):
///   +0x00  di_fname            16 bytes ASCII filename
///   +0x10  di_size             4 bytes (LE) file size in bytes
///   +0x14  di_first_xtnt       8 bytes — extent record:
///             u32 xtnt_blk     first block of extent
///             u32 xtnt_size    block count of extent
///   +0x1C  di_num_xtnts        4 bytes (LE) extra extent count
///   +0x20  di_mode             2 bytes mode (uid|gid|perm)
///   +0x22  di_uid              2 bytes
///   +0x24  di_gid              2 bytes
///   +0x26  di_ftime            4 bytes time
///   +0x2A  di_mtime            4 bytes
///   +0x2E  di_atime            4 bytes
///   +0x32  di_ctime            4 bytes
///   +0x36  di_zero             6 bytes
///   +0x3C  di_type             1 byte
///   +0x3D  di_status           1 byte file status (0x08=ACTIVE, 0x04=USED,
///                                                   0x01=DAMAGED, 0x02=DESTROY)
///
/// Spec source: linux/fs/qnx4/{qnx4.h,inode.c,namei.c} — kernel-side QNX4
/// driver maintained from 2.4 through 5.10.
/// </summary>
public sealed class Qnx4Reader : IDisposable {

  /// <summary>
  /// Random-access view over the image. Copying the volume into a byte[] capped
  /// the reader at the array limit, which no QNX4 volume is obliged to respect.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<Qnx4Entry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Qnx4Entry> Entries => this._entries;

    /// <summary>
  /// Defines the block size constant value.
  /// </summary>
public const int BlockSize = 512;
  internal const int InodeSize = 64;
  // Status byte values from qnx4 spec (linux/fs/qnx4/qnx4.h).
  // Linux qnx4 driver treats an entry as live when di_status & (USED|LINK) != 0;
  // the original reader only accepted LINK (0x08) which is the historical
  // QNX4-utils marker. We now accept both LINK-only (legacy) and USED-only
  // (Linux-friendly short-name) entries so our WORM-emitted images and
  // images produced by real QNX systems both round-trip.
  internal const byte StatusActive = 0x08; // QNX4_FILE_LINK
  internal const byte StatusUsed = 0x01;   // QNX4_FILE_USED
  internal const byte StatusBusy = 0x04;   // QNX4_FILE_BUSY (still treated as a live inode)
  // File type bits in di_mode are standard UNIX (S_IFDIR = 0x4000).
  private const ushort SIfdir = 0x4000;

    /// <summary>
  /// Initializes a new instance of <see cref="Qnx4Reader"/>.
  /// </summary>
public Qnx4Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < BlockSize * 4)
      throw new InvalidDataException("QNX4: image too small.");
    // Root directory is at LBA 1 — 4 blocks (= 2048 bytes = 32 inode entries).
    // We accept either a "/" entry pointing to the actual root or directly
    // parse block 1 as if it were the root directory.
    // We look for at least one "ACTIVE" status byte at offset 0x3D of a
    // 64-byte inode in the first 4 blocks to validate the image; this is
    // our weak magic (QNX4 has no fixed magic word).
    // Block 1 is the superblock: four inode entries, the first of which
    // describes the root directory. It is not itself a directory, which is
    // what this used to read it as.
    var rootEntry = (long)Qnx4Layout.SuperBlock * BlockSize;
    if (rootEntry + InodeSize > this._data.Length)
      throw new InvalidDataException("QNX4: image too small for a superblock.");

    var rootName = ReadName(this._data.Read(rootEntry, Qnx4Layout.NameBytes));
    if (rootName != "/")
      throw new InvalidDataException("QNX4: the superblock does not name a root directory.");

    var rootMode = this._data.ReadUInt16(rootEntry + Qnx4Layout.InMode);
    if ((rootMode & SIfdir) != SIfdir)
      throw new InvalidDataException("QNX4: the root entry is not a directory.");

    var rootBlock = this._data.ReadUInt32(rootEntry + Qnx4Layout.InExtentBlock);
    var rootCount = this._data.ReadUInt32(rootEntry + Qnx4Layout.InExtentSize);
    if (rootBlock == 0 || rootCount == 0)
      throw new InvalidDataException("QNX4: the root directory names no blocks.");

    this.ReadDirectoryCluster(rootBlock, rootCount, path: "");
  }

  /// <summary>
  /// Walks a directory's blocks. The block number is the one an extent
  /// records, which QNX4 counts from one.
  /// </summary>
  private void ReadDirectoryCluster(uint startBlock, uint blockCount, string path) {
    var seen = new HashSet<uint>();
    if (!seen.Add(startBlock)) return;
    for (var b = 0u; b < blockCount; b++) {
      var blockOff = Qnx4Layout.ByteOffsetOf(startBlock + b);
      if (blockOff + BlockSize > this._data.Length) break;
      for (var i = 0; i < 8; i++) { // 8 inodes per 512-byte block
        var off = (int)blockOff + i * InodeSize;
        if (off + InodeSize > this._data.Length) break;
        var status = this._data.ReadByte(off + Qnx4Layout.InStatus);
        if (!IsLiveStatus(status)) continue;
        var name = ReadName(this._data.Read(off, Qnx4Layout.NameBytes));
        // Skip the self-referencing root inode ("/") emitted by Qnx4Writer.
        if (string.Equals(name, "/", StringComparison.Ordinal)) continue;
        // Hide QNX4 system files from the user-visible listing — they are
        // structural metadata (block bitmap + overflow inode store + alt boot)
        // not user content. They remain on-disk; the reader simply elides
        // them from <see cref="Entries"/> the same way Linux's qnx4 driver
        // does not surface them in ls(1).
        if (name is ".bitmap" or ".inodes" or ".bootblock" or ".altboot") continue;
        if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
        var size = this._data.ReadUInt32(off + Qnx4Layout.InSize);
        var xtntBlk = this._data.ReadUInt32(off + Qnx4Layout.InExtentBlock);
        var xtntCnt = this._data.ReadUInt32(off + Qnx4Layout.InExtentSize);
        var mode = this._data.ReadUInt16(off + Qnx4Layout.InMode);
        var isDir = (mode & SIfdir) == SIfdir;
        var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
        this._entries.Add(new Qnx4Entry {
          Name = fullPath,
          Size = isDir ? 0 : size,
          FirstExtentBlock = xtntBlk,
          ExtentBlockCount = xtntCnt == 0 ? 1 : xtntCnt,
          IsDirectory = isDir,
        });
        if (isDir && xtntBlk != 0)
          this.ReadDirectoryCluster(xtntBlk, xtntCnt == 0 ? 4 : xtntCnt, fullPath);
      }
    }
  }

  /// <summary>Returns true when the status byte marks a live directory entry
  /// per the Linux qnx4 driver's check (<c>di_status &amp; (USED|LINK) != 0</c>).
  /// BUSY is also accepted because real-world images leave busy inodes
  /// reachable.</summary>
  private static bool IsLiveStatus(byte status) => (status & (StatusActive | StatusUsed | StatusBusy)) != 0;

  private static string ReadName(ReadOnlySpan<byte> raw) {
    var end = 0;
    while (end < raw.Length && raw[end] != 0) end++;
    return Encoding.ASCII.GetString(raw[..end]);
  }

  /// <summary>
  /// Copies an entry's bytes into <paramref name="destination" /> a block at a
  /// time, so an entry larger than a byte[] can hold is extracted like any other.
  /// </summary>
  public void ExtractTo(Qnx4Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    var offset = Qnx4Layout.ByteOffsetOf(entry.FirstExtentBlock);
    if (offset < 0 || offset >= this._data.Length) return;
    this._data.CopyTo(offset, destination, Math.Min(entry.Size, this._data.Length - offset));
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Qnx4Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = Qnx4Layout.ByteOffsetOf(entry.FirstExtentBlock);
    if (offset < 0 || offset >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - offset);
    return this._data.Read(offset, take);
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
