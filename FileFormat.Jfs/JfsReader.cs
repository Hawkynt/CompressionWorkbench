#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Jfs;

public sealed class JfsReader : IDisposable {
  private const uint JfsMagic = 0x3153464A; // "JFS1"
  private const int SuperblockOffset = 32768;
  private const int RootInode = 2;

  private readonly byte[] _data;
  private readonly List<JfsEntry> _entries = [];
  private int _blockSize;
  private long _filesetInodeTableOffset;

  public IReadOnlyList<JfsEntry> Entries => _entries;

  public JfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 200)
      throw new InvalidDataException("JFS: image too small.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset));
    if (magic != JfsMagic)
      throw new InvalidDataException("JFS: invalid superblock magic.");

    _blockSize = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(SuperblockOffset + 88));
    if (_blockSize == 0) _blockSize = 4096;

    // Aggregate inode table: pxd_t at superblock offset 96
    // pxd_t: flags_len(4 bytes) + addr_offset(4 bytes) = 8 bytes
    // Simplified: bytes at offset 96-103, addr(uint32 LE @100) is block address
    var aitAddr = (long)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 100)) * _blockSize;

    // Aggregate inode #16 (FILESYSTEM_I) within AIT
    // Each inode is 512 bytes in JFS
    const int jfsInodeSize = 512;
    var fsInoOff = aitAddr + 16 * jfsInodeSize;
    if (fsInoOff + jfsInodeSize > _data.Length) {
      // Fallback: try to find fileset starting after superblock
      _filesetInodeTableOffset = SuperblockOffset + _blockSize;
    } else {
      // Read FILESYSTEM_I inode to find fileset inode table
      // xtree root at inode offset 160: header(24 bytes) + extents
      var xtreeOff = (int)fsInoOff + 160;
      if (xtreeOff + 48 <= _data.Length) {
        // First xtree extent at xtreeOff + 24: xad_t(16 bytes)
        // Simplified: read block address from first extent
        var extAddr = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(xtreeOff + 36));
        _filesetInodeTableOffset = (long)extAddr * _blockSize;
      } else {
        _filesetInodeTableOffset = SuperblockOffset + _blockSize * 2;
      }
    }

    if (_filesetInodeTableOffset >= 0 && _filesetInodeTableOffset < _data.Length)
      ReadDirectory(RootInode, "");
  }

  private long InodeOffset(int ino) =>
    _filesetInodeTableOffset + (long)ino * 512;

  private void ReadDirectory(int ino, string basePath) {
    var inodeOff = InodeOffset(ino);
    if (inodeOff < 0 || inodeOff + 512 > _data.Length) return;
    var ioff = (int)inodeOff;

    // JFS inode: mode(uint32 LE @0) ... size(int64 LE @48)
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(ioff));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    var size = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(ioff + 48));

    // Directory data: try dtree (inline) at inode offset 160
    // dtree header: flag(1), nextindex(1), freecnt(1), freelist(1), idotdot(4), stbl[8]
    var dtOff = ioff + 160;
    if (dtOff + 16 > _data.Length) return;

    var dtFlag = _data[dtOff];
    var nextIndex = _data[dtOff + 1];

    ReadDtreeEntries(dtOff, ioff + 512, basePath);
  }

  private void ReadDtreeEntries(int dtOff, int dtEnd, string basePath) {
    if (dtOff + 16 > _data.Length) return;
    var nextIndex = _data[dtOff + 1];

    // stbl at offset 8, each entry 1 byte (slot index)
    var stblOff = dtOff + 8;

    for (int i = 0; i < nextIndex && i < 32; i++) {
      if (stblOff + i >= _data.Length) break;
      var slotIdx = _data[stblOff + i];
      var slotOff = dtOff + slotIdx * 32;
      if (slotOff + 32 > _data.Length || slotOff < dtOff) continue;

      // Slot: inumber(4 LE) + namlen(1) + name
      var childIno = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(slotOff));
      if (childIno < 2 || childIno > 100000) continue;
      var nameLen = _data[slotOff + 4];
      if (nameLen == 0 || nameLen > 27 || slotOff + 5 + nameLen > _data.Length) continue;

      var name = Encoding.UTF8.GetString(_data, slotOff + 5, nameLen);
      if (name == "." || name == "..") continue;
      // Validate name is printable
      if (!name.All(c => c >= 0x20 && c < 0x7F)) continue;

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var childInodeOff = InodeOffset(childIno);
      bool isDir = false;
      long childSize = 0;

      if (childInodeOff >= 0 && childInodeOff + 64 <= _data.Length) {
        var childMode = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)childInodeOff));
        isDir = (childMode & 0xF000) == 0x4000;
        childSize = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan((int)childInodeOff + 48));
      }

      _entries.Add(new JfsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        InodeNumber = childIno,
      });

      if (isDir)
        ReadDirectory(childIno, fullPath);
    }
  }

  public byte[] Extract(JfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var inodeOff = InodeOffset(entry.InodeNumber);
    if (inodeOff < 0 || inodeOff + 512 > _data.Length) return [];
    var ioff = (int)inodeOff;

    var size = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(ioff + 48));
    if (size <= 0) return [];

    // xtree root at offset 160
    var xtOff = ioff + 160;
    if (xtOff + 48 > _data.Length) return [];

    var xtFlag = _data[xtOff];
    var nextIdx = _data[xtOff + 1];

    // Inline data (small files stored inline when no extents)
    if (size <= 512 - 160 && nextIdx == 0) {
      var dataOff = xtOff;
      var len = (int)Math.Min(size, _data.Length - dataOff);
      return _data.AsSpan(dataOff, len).ToArray();
    }

    // Read from xtree extents
    using var ms = new MemoryStream();
    for (int i = 0; i < nextIdx && i < 8 && ms.Length < size; i++) {
      var xadOff = xtOff + 24 + i * 16;
      if (xadOff + 16 > _data.Length) break;

      // xad_t: flagOffLen(4) + addr(4) simplified
      var addr = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(xadOff + 12));
      var blkCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(xadOff + 8)) & 0xFFFFFF;
      if (addr == 0 || blkCount == 0) continue;

      var dataOff = (long)addr * _blockSize;
      var len = (int)Math.Min((long)blkCount * _blockSize, size - ms.Length);
      if (dataOff + len <= _data.Length)
        ms.Write(_data, (int)dataOff, len);
    }

    var result = ms.ToArray();
    if (result.Length > size)
      return result.AsSpan(0, (int)size).ToArray();
    return result;
  }

  public void Dispose() { }
}
