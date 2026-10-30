#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Reads UFS1 and UFS2 (BSD Fast File System) images in either byte order.
/// <para>
/// The superblock is searched where FreeBSD's <c>ffs_sbget</c> searches
/// (<c>SBLOCKSEARCH</c>: 65536, 8192, 0, 262144). <c>fs_magic</c> sits in the last
/// four bytes of the 1376-byte <c>struct fs</c> (offset 1372) and names the variant:
/// <c>0x00011954</c> UFS1, <c>0x19540119</c> UFS2; read in the opposite byte order it
/// marks a big-endian volume (SPARC, PowerPC, NeXT, <c>makefs -B be</c>). A UFS2
/// superblock is only taken where its own <c>fs_sblockloc</c> says it lives, so a
/// backup copy is never mistaken for the primary.
/// </para>
/// <para>
/// Field offsets follow FreeBSD's <c>struct fs</c> (<c>sys/ufs/ffs/fs.h</c>),
/// <c>struct ufs1_dinode</c> / <c>struct ufs2_dinode</c> (<c>sys/ufs/ufs/dinode.h</c>)
/// and <c>struct direct</c> (<c>sys/ufs/ufs/dir.h</c>). The inode walk follows the
/// twelve direct pointers and the single/double/triple indirect trees; UFS1 pointers
/// are 32-bit, UFS2 pointers 64-bit.
/// </para>
/// </summary>
public sealed class UfsReader : IDisposable {
  private const int SuperblockSize = 1376;
  private const int FsMagicOffset = SuperblockSize - 4;
  internal const uint Ufs1Magic = 0x00011954;
  internal const uint Ufs2Magic = 0x19540119;
  private const int RootInode = 2;
  private const int MaxDirectBlocks = 12;
  private const int MaxDirectoryDepth = 256;

  /// <summary>FreeBSD <c>SBLOCKSEARCH</c>: SBLOCK_UFS2, SBLOCK_UFS1, SBLOCK_FLOPPY, SBLOCK_PIGGY.</summary>
  internal static readonly long[] SuperblockSearch = [65536, 8192, 0, 262144];

  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<UfsEntry> _entries = [];
  private readonly HashSet<long> _visitedDirectories = [];

  private long _sbOffset;
  private int _blockSize;
  private int _fragSize;
  private int _inodesPerGroup;
  private int _cylinderGroups;
  private int _iblkno;         // inode-block offset within a CG (in frags)
  private int _fpg;            // frags per group
  private int _cgOffset;       // UFS1 fs_old_cgoffset (cylinder-group rotation)
  private int _cgMask;         // UFS1 fs_old_cgmask
  private int _inodeSize;      // 128 (UFS1) or 256 (UFS2)
  private int _pointerSize;    // 4 (UFS1) or 8 (UFS2)
  private int _maxSymlinkLen;  // fs_maxsymlinklen: targets shorter than this live in the inode
  private bool _oldDirectoryFormat; // FS_42INODEFMT: 16-bit d_namlen, no d_type

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<UfsEntry> Entries => _entries;

  /// <summary>1 for UFS1, 2 for UFS2.</summary>
  public int Version { get; private set; }

  /// <summary>Whether the volume stores its integers big-endian.</summary>
  public bool IsBigEndian { get; private set; }

  /// <summary>Byte offset of the superblock that was used.</summary>
  public long SuperblockOffset => this._sbOffset;

  /// <summary>The superblock <c>fs_volname</c> volume label (struct fs offset 680,
  /// NUL-terminated ASCII), or empty when unset.</summary>
  public string VolumeName {
    get {
      var span = _img.Read(_sbOffset + 680, 32).AsSpan();
      var nul = span.IndexOf((byte)0);
      var len = nul < 0 ? 32 : nul;
      return len == 0 ? "" : Encoding.ASCII.GetString(span[..len]);
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

  /// <summary>
  /// Locates the primary superblock the way FreeBSD does. Returns the offset,
  /// the variant (1 or 2) and the byte order, or <see langword="null"/> when no
  /// UFS superblock is present.
  /// </summary>
  internal static (long Offset, int Version, bool BigEndian)? Probe(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var buffer = new byte[SuperblockSize];
    foreach (var offset in SuperblockSearch) {
      if (offset + SuperblockSize > stream.Length) continue;
      stream.Position = offset;
      stream.ReadExactly(buffer);
      if (Classify(buffer, offset) is { } found) return (offset, found.Version, found.BigEndian);
    }
    return null;
  }

  private static (int Version, bool BigEndian)? Classify(ReadOnlySpan<byte> sb, long offset) {
    var le = BinaryPrimitives.ReadUInt32LittleEndian(sb[FsMagicOffset..]);
    var be = BinaryPrimitives.ReadUInt32BigEndian(sb[FsMagicOffset..]);
    (int Version, bool BigEndian)? hit = le switch {
      Ufs1Magic => (1, false),
      Ufs2Magic => (2, false),
      _ => be switch {
        Ufs1Magic => (1, true),
        Ufs2Magic => (2, true),
        _ => null,
      },
    };
    if (hit is not { } h) return null;
    if (h.Version == 2) {
      // fs_sblockloc (offset 1000) names the primary's own location; a copy that
      // sits elsewhere is a cylinder-group backup.
      var loc = h.BigEndian
        ? BinaryPrimitives.ReadInt64BigEndian(sb[1000..])
        : BinaryPrimitives.ReadInt64LittleEndian(sb[1000..]);
      if (loc != offset) return null;
    } else if (offset == 65536) {
      // UFS1 never puts its primary at SBLOCK_UFS2; what sits there is data.
      return null;
    }
    return h;
  }

  private ushort U16(long off) {
    if (off < 0 || this._len < off + 2) return 0;
    var v = this._img.ReadUInt16(off);
    return this.IsBigEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
  }

  private uint U32(long off) {
    if (off < 0 || this._len < off + 4) return 0u;
    var v = this._img.ReadUInt32(off);
    return this.IsBigEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
  }

  private ulong U64(long off) {
    if (off < 0 || this._len < off + 8) return 0UL;
    var v = this._img.ReadUInt64(off);
    return this.IsBigEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
  }

  private int I32(ReadOnlySpan<byte> s, int at)
    => this.IsBigEndian ? BinaryPrimitives.ReadInt32BigEndian(s[at..]) : BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

  private long I64(ReadOnlySpan<byte> s, int at)
    => this.IsBigEndian ? BinaryPrimitives.ReadInt64BigEndian(s[at..]) : BinaryPrimitives.ReadInt64LittleEndian(s[at..]);

  private void Parse() {
    var buffer = new byte[SuperblockSize];
    (int Version, bool BigEndian)? found = null;
    foreach (var offset in SuperblockSearch) {
      if (offset + SuperblockSize > _len) continue;
      _img.Read(offset, buffer);
      found = Classify(buffer, offset);
      if (found is null) continue;
      _sbOffset = offset;
      break;
    }
    if (found is not { } f)
      throw new InvalidDataException("UFS: no UFS1 or UFS2 superblock at any standard location (65536, 8192, 0, 262144).");

    this.Version = f.Version;
    this.IsBigEndian = f.BigEndian;
    ReadOnlySpan<byte> sb = buffer;

    _iblkno = I32(sb, 16);                 // fs_iblkno
    _cgOffset = I32(sb, 24);               // fs_old_cgoffset
    _cgMask = I32(sb, 28);                 // fs_old_cgmask
    _cylinderGroups = I32(sb, 44);         // fs_ncg
    _blockSize = I32(sb, 48);              // fs_bsize
    _fragSize = I32(sb, 52);               // fs_fsize
    _inodesPerGroup = I32(sb, 184);        // fs_ipg
    _fpg = I32(sb, 188);                   // fs_fpg
    _maxSymlinkLen = I32(sb, 1320);        // fs_maxsymlinklen
    var inodeFormat = I32(sb, 1324);       // fs_old_inodefmt

    if (_blockSize < 4096 || _blockSize > 65536 || (_blockSize & (_blockSize - 1)) != 0)
      throw new InvalidDataException($"UFS: implausible block size {_blockSize}.");
    if (_fragSize < 512 || _fragSize > _blockSize || _blockSize % _fragSize != 0)
      throw new InvalidDataException($"UFS: implausible fragment size {_fragSize}.");
    if (_inodesPerGroup <= 0 || _fpg <= 0 || _cylinderGroups <= 0 || _iblkno < 0)
      throw new InvalidDataException("UFS: superblock geometry (fs_ipg/fs_fpg/fs_ncg/fs_iblkno) is not positive.");

    if (this.Version == 2) {
      _inodeSize = 256;
      _pointerSize = 8;
      _cgOffset = 0;
      _cgMask = -1;
    } else {
      _inodeSize = 128;
      _pointerSize = 4;
      // 4.2BSD-format volumes predate d_type: d_namlen is a 16-bit field.
      _oldDirectoryFormat = inodeFormat < 2;
      if (_oldDirectoryFormat) _maxSymlinkLen = 0;
    }
    if (_maxSymlinkLen < 0 || _maxSymlinkLen > (MaxDirectBlocks + 3) * _pointerSize) _maxSymlinkLen = 0;

    ReadDirectory(RootInode, "", 0);
  }

  private long InodeOffset(long ino) {
    // cgimin(c) = cgstart(c) + fs_iblkno; UFS1's cgstart rotates by
    // fs_old_cgoffset * (c & ~fs_old_cgmask), UFS2's is plain cgbase.
    var cg = ino / _inodesPerGroup;
    var idx = ino % _inodesPerGroup;
    var cgStartFrags = cg * _fpg + (long)_cgOffset * (cg & ~(uint)_cgMask);
    return (cgStartFrags + _iblkno) * _fragSize + idx * _inodeSize;
  }

  private bool ValidInode(long ino) => ino >= RootInode && ino < (long)_inodesPerGroup * _cylinderGroups;

  // Per-variant inode field offsets (struct ufs1_dinode / struct ufs2_dinode).
  private long SizeField(long inodeOff) => inodeOff + (this.Version == 2 ? 16 : 8);
  private long DirectField(long inodeOff) => inodeOff + (this.Version == 2 ? 112 : 40);

  private DateTime? ModificationTime(long inodeOff) {
    long seconds = this.Version == 2 ? (long)U64(inodeOff + 40) : (int)U32(inodeOff + 24);
    if (seconds <= 0 || seconds > 253402300799L) return null;
    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
  }

  private long Pointer(long at) => this._pointerSize == 8 ? (long)U64(at) : (int)U32(at);

  private void ReadDirectory(long ino, string basePath, int depth) {
    if (depth > MaxDirectoryDepth || !_visitedDirectories.Add(ino)) return;
    var inodeOff = InodeOffset(ino);
    if (inodeOff + _inodeSize > _len) return;

    byte[]? dirData;
    try {
      dirData = ReadInodeData(inodeOff);
    } catch (InvalidDataException) {
      // A directory whose blocks point outside the image lists as empty rather
      // than failing the whole listing.
      return;
    }
    if (dirData == null || dirData.Length == 0) return;

    var pos = 0;
    while (pos + 8 <= dirData.Length) {
      var span = dirData.AsSpan(pos);
      var dino = this.IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
      var reclen = this.IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span[4..]) : BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
      if (reclen < 8 || pos + reclen > dirData.Length) break;
      int namlen = _oldDirectoryFormat
        ? (this.IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span[6..]) : BinaryPrimitives.ReadUInt16LittleEndian(span[6..]))
        : dirData[pos + 7];

      if (ValidInode(dino) && namlen > 0 && 8 + namlen <= reclen) {
        var name = Encoding.UTF8.GetString(dirData, pos + 8, namlen);
        // Skip "." / ".." and the synthetic newfs snapshot directory.
        if (name != "." && name != ".." && !(basePath.Length == 0 && name == ".snap")) {
          var childInodeOff = InodeOffset(dino);
          var isDir = false;
          var isSymlink = false;
          string? linkTarget = null;
          long size = 0;
          DateTime? mtime = null;

          if (childInodeOff + _inodeSize <= _len) {
            var mode = U16(childInodeOff);
            isDir = (mode & 0xF000) == 0x4000;
            isSymlink = (mode & 0xF000) == 0xA000;
            size = (long)U64(SizeField(childInodeOff));
            mtime = ModificationTime(childInodeOff);
            if (isSymlink) {
              try {
                linkTarget = ReadSymlinkTarget(childInodeOff, size);
              } catch (InvalidDataException) {
                linkTarget = null;
              }
            }
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
            Inode = dino,
          });
          if (isDir) ReadDirectory(dino, fullPath, depth + 1);
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
    if (inodeOff < 0 || inodeOff + _inodeSize > _len) return 0;
    var size = (long)U64(SizeField(inodeOff));
    return size < 0 ? 0 : size;
  }

  /// <summary>
  /// Writes an inode's contents into <paramref name="destination" />: the twelve
  /// direct blocks, then the single-, double- and triple-indirect roots, each
  /// level's pointer block addressing the level below.
  /// </summary>
  private long WriteInodeData(long inodeOff, Stream destination) {
    var size = this.InodeSizeOf(inodeOff);
    if (size <= 0) return 0;

    var direct = DirectField(inodeOff);
    long written = 0;
    for (var i = 0; i < MaxDirectBlocks && written < size; i++)
      written += this.AppendBlock(destination, Pointer(direct + i * _pointerSize), size, written);

    var pointersPerBlock = _blockSize / _pointerSize;
    for (var level = 1; level <= 3 && written < size; ++level) {
      var root = Pointer(direct + (MaxDirectBlocks + level - 1) * _pointerSize);
      var span = Span(level, pointersPerBlock);
      if (root == 0) {
        // A missing indirect root is a hole as wide as the tree it would address.
        var hole = Math.Min(size - written, span);
        WriteZeros(destination, hole);
        written += hole;
        continue;
      }
      written += this.WriteIndirect(destination, root, level, pointersPerBlock, size, written);
    }
    return written;
  }

  private long Span(int level, int pointersPerBlock) {
    long span = _blockSize;
    for (var i = 0; i < level; ++i) span = span > long.MaxValue / pointersPerBlock ? long.MaxValue : span * pointersPerBlock;
    return span;
  }

  private static void WriteZeros(Stream destination, long count) {
    Span<byte> zeros = stackalloc byte[4096];
    zeros.Clear();
    while (count > 0) {
      var n = (int)Math.Min(count, zeros.Length);
      destination.Write(zeros[..n]);
      count -= n;
    }
  }

  private long WriteIndirect(Stream destination, long pointerBlock, int level,
      int pointersPerBlock, long size, long already) {
    var tableOff = pointerBlock * _fragSize;
    if (pointerBlock < 0 || tableOff + _blockSize > _len) {
      // A pointer outside the volume: the bytes it would hold are unknowable.
      throw new InvalidDataException($"UFS: indirect block {pointerBlock} lies outside the image.");
    }
    var table = _img.Read(tableOff, _blockSize);

    long written = 0;
    for (var i = 0; i < pointersPerBlock && already + written < size; i++) {
      var blk = _pointerSize == 8 ? I64(table, i * 8) : I32(table, i * 4);
      if (blk == 0 && level > 1) {
        var hole = Math.Min(size - already - written, Span(level - 1, pointersPerBlock));
        WriteZeros(destination, hole);
        written += hole;
        continue;
      }
      written += level <= 1
        ? this.AppendBlock(destination, blk, size, already + written)
        : this.WriteIndirect(destination, blk, level - 1, pointersPerBlock, size, already + written);
    }
    return written;
  }

  // Appends up to one block of payload from frag-block `blk`, stopping at `size`.
  private long AppendBlock(Stream destination, long blk, long size, long already) {
    var remaining = size - already;
    if (remaining <= 0) return 0;
    var chunk = (int)Math.Min(_blockSize, remaining);
    if (blk == 0) {
      // A hole reads back as zeros.
      WriteZeros(destination, chunk);
      return chunk;
    }
    var off = blk * _fragSize;
    if (blk < 0 || off + chunk > _len)
      throw new InvalidDataException($"UFS: data block {blk} lies outside the image.");
    _img.CopyTo(off, destination, chunk);
    return chunk;
  }

  private string? ReadSymlinkTarget(long inodeOff, long size) {
    if (size == 0) return "";
    if (size < 0 || size > 4096) return null;
    // A "fast" symlink shorter than fs_maxsymlinklen stores its target in the
    // block-pointer area of the inode (di_shortlink): 60 bytes in UFS1, 120 in UFS2.
    if (size < _maxSymlinkLen) {
      var start = DirectField(inodeOff);
      var len = (int)Math.Min(size, _len - start);
      if (len <= 0) return "";
      return Encoding.UTF8.GetString(_img.Read(start, len));
    }
    var data = ReadInodeData(inodeOff);
    if (data == null || data.Length == 0) return "";
    var n = (int)Math.Min(size, data.Length);
    return Encoding.UTF8.GetString(data, 0, n);
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
      var target = Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
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
