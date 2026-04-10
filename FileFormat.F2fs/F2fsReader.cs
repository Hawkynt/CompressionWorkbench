#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.F2fs;

public sealed class F2fsReader : IDisposable {
  private const uint F2fsMagic = 0xF2F52010;
  private const int SbOffset = 1024;
  private const uint RootNodeId = 3;

  private readonly byte[] _data;
  private readonly List<F2fsEntry> _entries = [];

  private int _blockSize;
  private int _natBlkAddr; // in blocks
  private int _mainBlkAddr;

  public IReadOnlyList<F2fsEntry> Entries => _entries;

  public F2fsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SbOffset + 200)
      throw new InvalidDataException("F2FS: image too small.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SbOffset));
    if (magic != F2fsMagic)
      throw new InvalidDataException("F2FS: invalid superblock magic.");

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SbOffset + 12));
    _blockSize = 1 << (int)logBlockSize;
    if (_blockSize < 512) _blockSize = 4096;

    _natBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SbOffset + 72));
    _mainBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SbOffset + 80));

    ReadDirectory(RootNodeId, "");
  }

  private int LookupNat(uint nodeId) {
    // NAT entry: 9 bytes each (version(1) + ino(4) + block_addr(4))
    // 455 entries per 4096-byte NAT block
    var entriesPerBlock = _blockSize / 9;
    if (entriesPerBlock == 0) entriesPerBlock = 455;
    var natBlock = (int)(nodeId / entriesPerBlock);
    var natIdx = (int)(nodeId % entriesPerBlock);
    var natOff = (long)(_natBlkAddr + natBlock) * _blockSize + natIdx * 9;
    if (natOff + 9 > _data.Length) return -1;

    var blockAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)natOff + 5));
    return blockAddr;
  }

  private void ReadDirectory(uint nodeId, string basePath) {
    var blockAddr = LookupNat(nodeId);
    if (blockAddr <= 0) return;
    var nodeOff = (long)blockAddr * _blockSize;
    if (nodeOff + _blockSize > _data.Length) return;
    var noff = (int)nodeOff;

    // f2fs_inode: mode at offset 0
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(noff));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(noff + 40));

    // Read directory data blocks from i_addr[] (at offset 128)
    // Each i_addr is uint32 LE, up to 923 direct blocks
    for (int i = 0; i < 923; i++) {
      var addrOff = noff + 128 + i * 4;
      if (addrOff + 4 > _data.Length) break;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(addrOff));
      if (dataBlock == 0) continue;

      var dataOff = (long)dataBlock * _blockSize;
      if (dataOff + _blockSize > _data.Length) continue;

      // Parse dentry block: bitmap(27 bytes) + reserved(3) + dentry[214] + filename[214][8]
      // Simplified: scan for ino + name patterns
      ParseDentryBlock((int)dataOff, basePath);
    }
  }

  private void ParseDentryBlock(int blockOff, string basePath) {
    // F2FS dentry block layout:
    // dentry_bitmap: ceil(NR_DENTRY_IN_BLOCK / 8) bytes (NR_DENTRY_IN_BLOCK=214 for 4096)
    // reserved: padding to 32 bytes
    // dentry array: 214 entries, 11 bytes each (hash(4) + ino(4) + name_len(2) + file_type(1))
    // filename array: 214 entries, 8 bytes each (F2FS_SLOT_LEN)

    var nrDentry = (_blockSize - 64) / (11 + 8); // approximate
    if (nrDentry > 214) nrDentry = 214;
    var bitmapSize = (nrDentry + 7) / 8;
    var dentryOff = blockOff + 64; // after bitmap + reserved
    var nameOff = dentryOff + nrDentry * 11;

    for (int i = 0; i < nrDentry; i++) {
      // Check bitmap
      var byteIdx = blockOff + i / 8;
      var bitIdx = i % 8;
      if (byteIdx >= _data.Length) break;
      if ((_data[byteIdx] & (1 << bitIdx)) == 0) continue; // slot empty

      var entryOff = dentryOff + i * 11;
      if (entryOff + 11 > _data.Length) break;

      var hash = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entryOff));
      var ino = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entryOff + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(entryOff + 8));
      var fileType = _data[entryOff + 10];

      if (ino == 0 || nameLen == 0 || nameLen > 255) continue;

      // Filename is at nameOff + i * F2FS_SLOT_LEN(8)
      // Multi-slot names span consecutive slots
      var fnOff = nameOff + i * 8;
      var fnLen = Math.Min(nameLen, _data.Length - fnOff);
      if (fnLen <= 0 || fnOff + fnLen > _data.Length) continue;

      var name = Encoding.UTF8.GetString(_data, fnOff, fnLen);
      name = name.TrimEnd('\0');
      if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var isDir = fileType == 2;

      long childSize = 0;
      if (!isDir) {
        var childBlock = LookupNat(ino);
        if (childBlock > 0) {
          var childOff = (long)childBlock * _blockSize;
          if (childOff + 48 <= _data.Length)
            childSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan((int)childOff + 40));
        }
      }

      _entries.Add(new F2fsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        NodeId = ino,
      });

      if (isDir)
        ReadDirectory(ino, fullPath);
    }
  }

  public byte[] Extract(F2fsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var blockAddr = LookupNat(entry.NodeId);
    if (blockAddr <= 0) return [];
    var nodeOff = (long)blockAddr * _blockSize;
    if (nodeOff + _blockSize > _data.Length) return [];
    var noff = (int)nodeOff;

    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(noff + 40));
    if (size <= 0) return [];

    using var ms = new MemoryStream();
    for (int i = 0; i < 923 && ms.Length < size; i++) {
      var addrOff = noff + 128 + i * 4;
      if (addrOff + 4 > _data.Length) break;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(addrOff));
      if (dataBlock == 0) continue;
      var dataOff = (long)dataBlock * _blockSize;
      var len = (int)Math.Min(_blockSize, size - ms.Length);
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
