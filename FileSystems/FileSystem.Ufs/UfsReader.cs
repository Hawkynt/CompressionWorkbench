#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Reads UFS1 (FreeBSD/BSD FFS) filesystem images. Decodes the superblock at
/// <c>SBLOCK_UFS1 = 8192</c>, locates CG 0's inode table, walks the root
/// directory (inode 2) and extracts file contents via <c>di_db[]</c> direct
/// block pointers (indirect blocks are not followed — our writer never uses them).
/// <para>
/// All field offsets mirror FreeBSD's <c>struct fs</c> (<c>sys/ufs/ffs/fs.h</c>)
/// and <c>struct ufs1_dinode</c> (<c>sys/ufs/ufs/dinode.h</c>). <c>fs_magic</c>
/// sits at the last 4 bytes of the 1376-byte superblock (offset 1372).
/// </para>
/// </summary>
public sealed class UfsReader : IDisposable {
  private const int SuperblockOffset = 8192;
  private const int SuperblockSize = 1376;
  private const int FsMagicOffset = SuperblockSize - 4;
  private const uint Ufs1Magic = 0x00011954;
  private const int InodeSize = 128;
  private const int RootInode = 2;
  private const int MaxDirectBlocks = 12;

  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<UfsEntry> _entries = [];

  private int _blockSize;
  private int _fragSize;
  private int _inodesPerGroup;
  private int _iblkno;         // inode-block offset within CG 0 (in frags)
  private int _fpg;            // frags per group
  private int _fsbtodb;        // log2(fs_fsize/DEV_BSIZE)
  private int _inodesPerBlock; // fs_inopb

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<UfsEntry> Entries => _entries;

  /// <summary>The superblock <c>fs_volname</c> volume label (struct fs offset 680,
  /// NUL-terminated ASCII), or empty when unset.</summary>
  public string VolumeName {
    get {
      if (_len < SuperblockOffset + 680 + 32) return "";
      var span = _img.Read(SuperblockOffset + 680, 32).AsSpan();
      var nul = span.IndexOf((byte)0);
      var len = nul < 0 ? 32 : nul;
      return len == 0 ? "" : System.Text.Encoding.ASCII.GetString(span[..len]);
    }
  }

  /// <summary>
  /// Initializes a new instance of <see cref="UfsReader"/>.
  /// </summary>
public UfsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: the metadata is a small fraction of a volume
    // whose data area may run to gigabytes.
    _img = new ImageAccessor(stream, leaveOpen);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private ushort U16(long off) => this._len >= off + 2 ? this._img.ReadUInt16(off) : (ushort)0;
  private uint U32(long off) => this._len >= off + 4 ? this._img.ReadUInt32(off) : 0u;
  private ulong U64(long off) => this._len >= off + 8 ? this._img.ReadUInt64(off) : 0UL;
  private byte B(long off) => off >= 0 && off < this._len ? this._img.ReadByte(off) : (byte)0;
  private string Str(long off, int len) => Encoding.ASCII.GetString(this._img.Read(off, len));

  private void Parse() {
    if (_len < SuperblockOffset + SuperblockSize)
      throw new InvalidDataException("UFS: image too small to contain a UFS1 superblock.");

    var sb = _img.Read(SuperblockOffset, SuperblockSize).AsSpan();
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb[FsMagicOffset..]);
    if (magic != Ufs1Magic)
      throw new InvalidDataException($"UFS: invalid superblock magic 0x{magic:X8} (expected 0x{Ufs1Magic:X8}).");

    // Real spec offsets:
    _iblkno = BinaryPrimitives.ReadInt32LittleEndian(sb[16..]);          // fs_iblkno
    _blockSize = BinaryPrimitives.ReadInt32LittleEndian(sb[48..]);       // fs_bsize
    _fragSize = BinaryPrimitives.ReadInt32LittleEndian(sb[52..]);        // fs_fsize
    _fsbtodb = BinaryPrimitives.ReadInt32LittleEndian(sb[100..]);        // fs_fsbtodb
    _inodesPerBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[120..]); // fs_inopb
    _inodesPerGroup = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[184..]); // fs_ipg
    _fpg = BinaryPrimitives.ReadInt32LittleEndian(sb[188..]);            // fs_fpg

    if (_fragSize <= 0) _fragSize = 1024;
    if (_blockSize <= 0) _blockSize = 8192;
    if (_fpg <= 0) _fpg = 16384;
    if (_inodesPerGroup <= 0) _inodesPerGroup = 2048;
    if (_inodesPerBlock <= 0) _inodesPerBlock = _blockSize / InodeSize;

    ReadDirectory(RootInode, "");
  }

  private long InodeOffset(int ino) {
    // In a single-CG image, cgstart(0) = 0. Inode i → CG (i / fs_ipg), index (i % fs_ipg).
    // For our simple layout, inode table starts at (iblkno * fragSize) within CG 0.
    var cg = ino / _inodesPerGroup;
    var idx = ino % _inodesPerGroup;
    var cgStart = (long)cg * _fpg * _fragSize;
    return cgStart + (long)_iblkno * _fragSize + (long)idx * InodeSize;
  }

  private void ReadDirectory(int ino, string basePath) {
    var inodeOff = InodeOffset(ino);
    if (inodeOff + InodeSize > _len) return;

    var dirData = ReadInodeData(inodeOff);
    if (dirData == null || dirData.Length == 0) return;

    var pos = 0;
    while (pos + 8 <= dirData.Length) {
      var dino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos));
      var reclen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 4));
      if (reclen < 8 || pos + reclen > dirData.Length) break;
      var namlen = dirData[pos + 7];

      if (dino != 0 && namlen > 0 && pos + 8 + namlen <= dirData.Length) {
        var name = Encoding.ASCII.GetString(dirData, pos + 8, namlen);
        // Skip "." / ".." and the synthetic newfs snapshot directory.
        if (name != "." && name != ".." && !(basePath.Length == 0 && name == ".snap")) {
          var childInodeOff = InodeOffset((int)dino);
          var isDir = false;
          var isSymlink = false;
          string? linkTarget = null;
          long size = 0;
          DateTime? mtime = null;

          if (childInodeOff + InodeSize <= _len) {
            var mode = U16(((int)childInodeOff));
            isDir = (mode & 0xF000) == 0x4000;
            isSymlink = (mode & 0xF000) == 0xA000;
            size = (long)U64(((int)(childInodeOff + 8)));
            var mt = U32(((int)(childInodeOff + 24)));
            if (mt > 0) mtime = DateTimeOffset.FromUnixTimeSeconds(mt).UtcDateTime;
            if (isSymlink) linkTarget = ReadSymlinkTarget(childInodeOff, size);
          }

          var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
          _entries.Add(new UfsEntry {
            Name = fullPath,
            // A symlink's own size is the target-path byte length (di_size).
            Size = isDir ? 0 : size,
            IsDirectory = isDir,
            IsSymlink = isSymlink,
            LinkTarget = linkTarget,
            LastModified = mtime,
            Inode = (int)dino,
          });
          if (isDir) ReadDirectory((int)dino, fullPath);
        }
      }
      pos += reclen;
    }
  }

  private byte[]? ReadInodeData(long inodeOff) {
    var size = this.InodeSizeOf(inodeOff);
    if (size <= 0 || size > 256L * 1024 * 1024) return null;
    using var ms = new MemoryStream();
    this.WriteInodeData(inodeOff, ms);
    return ms.ToArray();
  }

  /// <summary>The logical size an inode declares, or 0 when it is out of range.</summary>
  private long InodeSizeOf(long inodeOff) {
    if (inodeOff < 0 || inodeOff + InodeSize > _len) return 0;
    return (long)U64(inodeOff + 8);
  }

  /// <summary>
  /// Writes an inode's contents into <paramref name="destination" />: the twelve
  /// direct blocks, then di_ib[0..2] — a single-, double- and triple-indirect
  /// root, each level's pointer block addressing the level below. Following only
  /// di_ib[0] stopped at 16 MB per file.
  /// </summary>
  private long WriteInodeData(long inodeOff, Stream destination) {
    var size = this.InodeSizeOf(inodeOff);
    if (size <= 0) return 0;

    long written = 0;
    for (var i = 0; i < MaxDirectBlocks && written < size; i++)
      written += this.AppendBlock(destination, (int)U32(inodeOff + 40 + i * 4), size, written);

    var pointersPerBlock = _blockSize / 4;
    for (var level = 1; level <= 3 && written < size; ++level) {
      var root = (int)U32(inodeOff + 40 + MaxDirectBlocks * 4 + (level - 1) * 4);
      if (root == 0) continue;
      written += this.WriteIndirect(destination, root, level, pointersPerBlock, size, written);
    }
    return written;
  }

  private long WriteIndirect(Stream destination, int pointerBlock, int level,
      int pointersPerBlock, long size, long already) {
    var tableOff = (long)pointerBlock * _fragSize;
    if (tableOff < 0 || tableOff + _blockSize > _len) return 0;
    var table = _img.Read(tableOff, _blockSize);

    long written = 0;
    for (var i = 0; i < pointersPerBlock && already + written < size; i++) {
      var blk = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
      if (blk == 0) continue;
      written += level <= 1
        ? this.AppendBlock(destination, blk, size, already + written)
        : this.WriteIndirect(destination, blk, level - 1, pointersPerBlock, size, already + written);
    }
    return written;
  }

  // Appends up to one block of payload from frag-block `blk`, stopping at `size`.
  private long AppendBlock(Stream destination, int blk, long size, long already) {
    var remaining = size - already;
    if (remaining <= 0) return 0;
    var chunk = (int)Math.Min(_blockSize, remaining);
    if (blk == 0) {
      // A hole reads back as zeros.
      destination.Write(new byte[chunk]);
      return chunk;
    }
    var off = (long)blk * _fragSize;
    if (off < 0 || off + chunk > _len) return 0;
    _img.CopyTo(off, destination, chunk);
    return chunk;
  }

  // UFS1 MAXSYMLINKLEN = (NDADDR + NIADDR) * sizeof(ufs1_daddr_t) = (12 + 3) * 4 = 60.
  // A "fast" symlink with di_size < 60 stores its target inline in the di_db/di_ib
  // union (di_shortlink) at inode offset 40; a longer target lives in the file's
  // data block(s). References: FreeBSD sys/ufs/ufs/dinode.h, sys/ufs/ffs.
  private const int MaxFastSymlinkLen = 60;

  private string? ReadSymlinkTarget(long inodeOff, long size) {
    if (size == 0) return "";
    if (size < 0 || size > 4096) return null;
    if (size < MaxFastSymlinkLen) {
      var start = (int)inodeOff + 40;
      var len = (int)Math.Min(size, _len - start);
      if (len <= 0) return "";
      return Str(start, len);
    }
    var data = ReadInodeData(inodeOff);
    if (data == null || data.Length == 0) return "";
    var n = (int)Math.Min(size, data.Length);
    return Encoding.ASCII.GetString(data, 0, n);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(UfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"UFS: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />,
  /// one block at a time through the inode's indirect tree. Returns the byte count.
  /// </summary>
  public long ExtractTo(UfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;
    // A symlink's honest content is its target path text.
    if (entry.IsSymlink) {
      var target = Encoding.ASCII.GetBytes(entry.LinkTarget ?? "");
      destination.Write(target);
      return target.Length;
    }
    return this.WriteInodeData(InodeOffset(entry.Inode), destination);
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() => this._img.Dispose();
}
