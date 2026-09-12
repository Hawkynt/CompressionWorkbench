#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Xfs;

/// <summary>
/// Reads the directory tree of an SGI XFS filesystem image and extracts the files it holds.
/// </summary>
public sealed class XfsReader : IDisposable {
  private const uint XfsMagic = 0x58465342; // "XFSB"
  private const ushort InodeMagic = 0x494E; // "IN"

  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<XfsEntry> _entries = [];

  private uint _blockSize;
  private ushort _inodeSize;
  private ulong _rootIno;
  private uint _agBlocks;
  private uint _agCount;
  private byte _agBlkLog;
  private byte _dirBlkLog;
  private ushort _versionNum;
  private uint _featuresIncompat;

  private const uint XfsFeatIncompatFtype = 0x1;
  private bool HasFtype => (this._featuresIncompat & XfsFeatIncompatFtype) != 0;

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<XfsEntry> Entries => _entries;

  /// <summary>
  /// Initializes a new instance of <see cref="XfsReader"/>.
  /// </summary>
  public XfsReader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Blocks are pulled on demand: an XFS volume's metadata is a small prefix
    // however many gigabytes of file extents follow it.
    _img = new ImageAccessor(stream, leaveOpen);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private ushort U16(long off) => this._img.Length >= off + 2 ? BinaryPrimitives.ReverseEndianness(this._img.ReadUInt16(off)) : (ushort)0;
  private uint U32(long off) => this._img.Length >= off + 4 ? BinaryPrimitives.ReverseEndianness(this._img.ReadUInt32(off)) : 0u;
  private ulong U64(long off) => this._img.Length >= off + 8 ? BinaryPrimitives.ReverseEndianness(this._img.ReadUInt64(off)) : 0UL;
  private byte B(long off) => off >= 0 && off < this._img.Length ? this._img.ReadByte(off) : (byte)0;
  private byte[] Read(long off, int len) => this._img.Read(off, len);
  private string Str(long off, int len) => Encoding.UTF8.GetString(this._img.Read(off, len));

  private void Parse() {
    if (_len < 512)
      throw new InvalidDataException("XFS: image too small.");

    var magic = U32((0));
    if (magic != XfsMagic)
      throw new InvalidDataException("XFS: invalid superblock magic.");

    _blockSize = U32((4));
    _rootIno = U64((56));
    _agBlocks = U32((84));
    _agCount = U32((88));
    _versionNum = U16((100));
    _inodeSize = U16((104));
    _agBlkLog = B(124);
    _dirBlkLog = B(192); // sb_dirblklog — directory block = blocksize << this
    // sb_features_incompat lives at offset 216 on v5 superblocks. Only read
    // when sb is v5 (low nibble of sb_versionnum == 5); otherwise leave zero.
    if ((_versionNum & 0xF) >= 5 && _len >= 220)
      _featuresIncompat = U32((216));

    if (_blockSize == 0) _blockSize = 4096;
    if (_inodeSize == 0) _inodeSize = 256;
    if (_agBlocks == 0) _agBlocks = (uint)(_len / _blockSize);
    if (_agBlkLog == 0) {
      // Recover from agblocks.
      var v = _agBlocks;
      while (v > 1) { _agBlkLog++; v >>= 1; }
    }

    ReadDirectory(_rootIno, "");
  }

  /// <summary>Offset of the extent/local fork within a dinode — 100 for v2, 176 for v3.</summary>
  private int InodeForkOffset => (_versionNum & 0xF) >= 5 ? 176 : 100;

  private long InodeOffset(ulong ino) {
    // XFS inode number encodes AG number and inode position
    var inoPerBlock = (int)(_blockSize / _inodeSize);
    var inoPbLog = 0;
    for (var v = inoPerBlock; v > 1; v >>= 1) inoPbLog++;
    var aginoLog = _agBlkLog + inoPbLog;

    var agNo = (uint)(ino >> aginoLog);
    var agIno = ino & ((1UL << aginoLog) - 1);
    var block = agIno / (ulong)inoPerBlock;
    var offset = agIno % (ulong)inoPerBlock;

    var byteOffset = (long)((agNo * _agBlocks + block) * _blockSize + offset * _inodeSize);
    return byteOffset;
  }

  private void ReadDirectory(ulong ino, string basePath) {
    var off = InodeOffset(ino);
    if (off < 0 || off + _inodeSize > _len) return;
    var ioff = (int)off;

    // Validate inode magic
    if (U16((ioff)) != InodeMagic) return;

    var mode = U16((ioff + 2));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    var format = B(ioff + 5);
    var size = (long)U64((ioff + 56));
    var forkOff = InodeForkOffset;

    if (format == 1) {
      // Short-form directory: inline data after inode core
      ReadShortFormDir(ioff + forkOff, Math.Min((int)size, _inodeSize - forkOff), basePath);
    } else if (format == 2) {
      // Extents format: read extent list and parse as block-form directory
      ReadExtentDir(ioff, basePath);
    }
  }

  private void ReadShortFormDir(int dataOff, int dataLen, string basePath) {
    if (dataOff + 6 > _len) return;
    var count = B(dataOff); // number of entries
    var i8count = B(dataOff + 1); // number of entries with 8-byte inodes
    var pos = dataOff + 6; // skip count(1)+i8count(1)+parent(4)

    if (i8count > 0) pos = dataOff + 10; // parent is 8 bytes

    for (int i = 0; i < count + i8count && pos + 3 < dataOff + dataLen; i++) {
      var nameLen = B(pos);
      if (nameLen == 0) break;
      var offset = U16((pos + 1));
      if (pos + 3 + nameLen > _len) break;
      var name = Str(pos + 3, nameLen);

      ulong childIno;
      // With the FTYPE feature, each sf entry inserts a 1-byte ftype between
      // the filename and the inode number.
      var ftypeLen = this.HasFtype ? 1 : 0;
      var inoPos = pos + 3 + nameLen + ftypeLen;
      if (i < count && i8count == 0) {
        // 4-byte inode
        if (inoPos + 4 > _len) break;
        childIno = U32((inoPos));
        pos = inoPos + 4;
      } else {
        // 8-byte inode
        if (inoPos + 8 > _len) break;
        childIno = U64((inoPos));
        pos = inoPos + 8;
      }

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

      // Check if child is a directory
      var childOff = InodeOffset(childIno);
      bool isDir = false;
      long childSize = 0;
      if (childOff >= 0 && childOff + 64 <= _len) {
        var childMode = U16(((int)childOff + 2));
        isDir = (childMode & 0xF000) == 0x4000;
        childSize = (long)U64(((int)childOff + 56));
      }

      _entries.Add(new XfsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        InodeNumber = (long)childIno,
      });

      if (isDir)
        ReadDirectory(childIno, fullPath);
    }
  }

  // dir2/dir3 directory data block magics. XD2B/XDB3 are single-block dirs;
  // XD2D/XDD3 are multi-block (leaf/node) dir data blocks. Leaf and free-index
  // blocks carry other magics and must not be parsed as data entries.
  private const uint Dir2BlockMagic = 0x58443242; // "XD2B"
  private const uint Dir3BlockMagic = 0x58444233; // "XDB3"
  private const uint Dir2DataMagic = 0x58443244;  // "XD2D"
  private const uint Dir3DataMagic = 0x58444433;  // "XDD3"

  private void ReadExtentDir(int inodeOff, string basePath) {
    // Extent list starts at the inode's fork offset.
    var forkOff = InodeForkOffset;
    if (inodeOff + forkOff + 4 > _len) return;
    var nextents = U32((inodeOff + 76));
    if (nextents == 0 || nextents > 100) return;

    // A directory block can span several fs blocks (sb_dirblklog); parse each
    // logical directory block as a unit. The leaf and free-index blocks live in
    // a higher region of the logical address space (≥ 32 GiB / blocksize) and
    // are skipped here.
    var dirFsBlocks = 1 << _dirBlkLog;
    var leafFsBlockOffset = 1L << (35 - BlockShift(_blockSize));

    var extOff = inodeOff + forkOff;
    for (uint e = 0; e < nextents; e++) {
      if (extOff + 16 > _len) break;
      var hi = U64((extOff));
      var lo = U64((extOff + 8));
      extOff += 16;

      var blockCount = (int)(lo & 0x1FFFFF);
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
      var startOff = (long)((hi >> 9) & 0x3FFFFFFFFFFFFFUL);
      if (startOff >= leafFsBlockOffset) continue; // leaf/free-index space

      // Walk the extent one directory block at a time.
      for (var b = 0; b < blockCount; b += dirFsBlocks) {
        var blockOff = (long)(startBlock + (ulong)b) * _blockSize;
        if (blockOff + 8 > _len) continue;
        ReadDirDataBlock((int)blockOff, dirFsBlocks * (int)_blockSize, basePath);
      }
    }
  }

  private static int BlockShift(uint blockSize) {
    var log = 0;
    while ((1u << log) < blockSize) log++;
    return log;
  }

  private void ReadDirDataBlock(int blockOff, int blockLen, string basePath) {
    var pos = blockOff;
    var end = blockOff + blockLen;

    // Only parse blocks that are directory data blocks; skip everything else.
    if (pos + 4 > _len) return;
    var bMagic = U32((pos));
    var isV3 = bMagic is Dir3BlockMagic or Dir3DataMagic;
    var isV2 = bMagic is Dir2BlockMagic or Dir2DataMagic;
    if (!isV3 && !isV2) return;
    pos += isV3 ? 64 : 16; // dir3 hdr is 64 bytes; dir2 hdr is 16 bytes

    // Single-block dirs ("XDB3"/"XD2B") embed a leaf index + 8-byte tail at the
    // block end; the data-entry area stops short of it. The tail's leaf-entry
    // count lets us compute that boundary so leaf entries aren't misread as data.
    if (bMagic is Dir3BlockMagic or Dir2BlockMagic && end - 8 >= blockOff) {
      var leafCount = (int)U32((end - 8));
      var dataEnd = end - 8 - leafCount * 8;
      if (dataEnd > blockOff && dataEnd < end) end = dataEnd;
    }

    // dir2 data entry: inumber(8), namelen(1), name(namelen), [ftype(1) when the
    // FTYPE feature is set], tag(2), padded to 8 bytes. An unused (free) region
    // begins with the 0xffff free-tag in the first 2 bytes.
    var ftypeLen = this.HasFtype ? 1 : 0;
    while (pos + 12 <= end && pos + 12 <= _len) {
      // Skip an xfs_dir2_data_unused free region (freetag 0xffff, then length).
      if (U16((pos)) == 0xFFFF) {
        var freeLen = U16((pos + 2));
        if (freeLen < 8) break;
        pos += freeLen;
        continue;
      }
      var entIno = U64((pos));
      var nameLen = B(pos + 8);
      if (nameLen == 0 || entIno == 0) { pos += 8; continue; }
      if (pos + 9 + nameLen + ftypeLen + 2 > _len) break;
      var name = Str(pos + 9, nameLen);

      if (name != "." && name != "..") {
        var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
        var childOff = InodeOffset(entIno);
        bool isDir = false;
        long childSize = 0;
        if (childOff >= 0 && childOff + 64 <= _len &&
            U16(((int)childOff)) == InodeMagic) {
          var childMode = U16(((int)childOff + 2));
          isDir = (childMode & 0xF000) == 0x4000;
          childSize = (long)U64(((int)childOff + 56));
        }

        _entries.Add(new XfsEntry {
          Name = fullPath,
          Size = isDir ? 0 : childSize,
          IsDirectory = isDir,
          InodeNumber = (long)entIno,
        });

        // Descend into sub-directories so block/leaf-form parents expose their
        // whole subtree (short-form parents recurse via ReadShortFormDir).
        if (isDir)
          ReadDirectory(entIno, fullPath);
      }

      // Entry size: 8 (ino) + 1 (namelen) + nameLen + ftype + 2 (tag), aligned to 8.
      var entLen = 8 + 1 + nameLen + ftypeLen + 2;
      entLen = (entLen + 7) & ~7;
      pos += entLen;
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(XfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var size = entry.Size;
    if (size > Array.MaxLength)
      throw new IOException(
        $"XFS: '{entry.Name}' is {size:N0} bytes, past the array limit; use ExtractTo.");

    using var ms = new MemoryStream();
    this.ExtractTo(entry, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />,
  /// extent by extent. Returns the number of bytes written.
  /// </summary>
  public long ExtractTo(XfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;

    var off = InodeOffset((ulong)entry.InodeNumber);
    if (off < 0 || off + _inodeSize > _len) return 0;
    var ioff = off;

    if (U16(ioff) != InodeMagic) return 0;

    var format = B(ioff + 5);
    var size = (long)U64(ioff + 56);
    var forkOff = InodeForkOffset;

    if (format == 1) {
      // Local/inline data
      var dataOff = ioff + forkOff;
      var len = (int)Math.Min(size, _inodeSize - forkOff);
      if (len <= 0 || dataOff + len > _len) return 0;
      destination.Write(Read(dataOff, len));
      return len;
    }

    if (format != 2) return 0;
    return this.WriteExtentData(ioff, size, destination);
  }

  private long WriteExtentData(long inodeOff, long size, Stream destination) {
    var nextents = U32(inodeOff + 76);
    if (nextents == 0) return 0;

    long written = 0;
    var extOff = inodeOff + InodeForkOffset;
    for (uint e = 0; e < nextents && written < size; e++) {
      if (extOff + 16 > _len) break;
      var hi = U64(extOff);
      var lo = U64(extOff + 8);
      extOff += 16;

      var blockCount = (long)(lo & 0x1FFFFF);
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);

      for (long b = 0; b < blockCount && written < size; b++) {
        var blockOff = (long)(startBlock + (ulong)b) * _blockSize;
        var len = (int)Math.Min(_blockSize, size - written);
        if (blockOff + len > _len) break;
        _img.CopyTo(blockOff, destination, len);
        written += len;
      }
    }
    return written;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => this._img.Dispose();
}
