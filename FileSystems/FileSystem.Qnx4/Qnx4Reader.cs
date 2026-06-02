#pragma warning disable CS1591
using System.Buffers.Binary;
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

  private readonly byte[] _data;
  private readonly List<Qnx4Entry> _entries = [];

  public IReadOnlyList<Qnx4Entry> Entries => this._entries;

  internal const int BlockSize = 512;
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

  public Qnx4Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
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
    var validInodes = 0;
    for (var i = 0; i < 32; i++) {
      var off = BlockSize + i * InodeSize;
      if (off + InodeSize > this._data.Length) break;
      var status = this._data[off + 0x3D];
      if (IsLiveStatus(status)) validInodes++;
    }
    if (validInodes == 0)
      throw new InvalidDataException("QNX4: no ACTIVE/USED inode found in root directory cluster.");

    this.ReadDirectoryCluster(1, blockCount: 4, path: "");
  }

  private void ReadDirectoryCluster(uint startBlock, uint blockCount, string path) {
    var seen = new HashSet<uint>();
    if (!seen.Add(startBlock)) return;
    for (var b = 0u; b < blockCount; b++) {
      var blockOff = (long)(startBlock + b) * BlockSize;
      if (blockOff + BlockSize > this._data.Length) break;
      for (var i = 0; i < 8; i++) { // 8 inodes per 512-byte block
        var off = (int)blockOff + i * InodeSize;
        if (off + InodeSize > this._data.Length) break;
        var status = this._data[off + 0x3D];
        if (!IsLiveStatus(status)) continue;
        var name = ReadName(this._data.AsSpan(off, 16));
        // Skip the self-referencing root inode ("/") emitted by Qnx4Writer.
        if (string.Equals(name, "/", StringComparison.Ordinal)) continue;
        // Hide QNX4 system files from the user-visible listing — they are
        // structural metadata (block bitmap + overflow inode store + alt boot)
        // not user content. They remain on-disk; the reader simply elides
        // them from <see cref="Entries"/> the same way Linux's qnx4 driver
        // does not surface them in ls(1).
        if (name is ".bitmap" or ".inodes" or ".bootblock" or ".altboot") continue;
        if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off + 0x10));
        var xtntBlk = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off + 0x14));
        var xtntCnt = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(off + 0x18));
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(off + 0x20));
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

  public byte[] Extract(Qnx4Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = (long)entry.FirstExtentBlock * BlockSize;
    if (offset < 0 || offset >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - offset);
    return this._data.AsSpan((int)offset, take).ToArray();
  }

  public void Dispose() { }
}
