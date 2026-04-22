#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// Reads ext2/ext3/ext4 filesystem images. Parses the superblock, block group
/// descriptors, inode table, and directory entries. Supports both direct/indirect
/// block pointers (ext2/3) and extent trees (ext4).
/// </summary>
public sealed class ExtReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<ExtEntry> _entries = [];

  public IReadOnlyList<ExtEntry> Entries => _entries;

  // Superblock fields
  private uint _inodesCount;
  private uint _blocksCount;
  private int _blockSize;
  private uint _blocksPerGroup;
  private uint _inodesPerGroup;
  private ushort _inodeSize;
  private uint _featureIncompat;
  private uint _firstDataBlock;

  // Block group descriptor table
  private uint[] _bgInodeTableBlock = [];

  // Constants
  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const ushort InodeModeDir = 0x4000;
  private const ushort InodeModeFile = 0x8000;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;

  public ExtReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext: image too small for superblock.");

    // Read superblock at offset 1024
    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    _inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    _blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4));
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24));
    _blockSize = 1024 << (int)logBlockSize;
    _blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32));
    _inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40));
    _inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(88));
    if (_inodeSize == 0) _inodeSize = 128;
    _featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(96));
    _firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20));

    // Read block group descriptors
    var bgdtBlock = _firstDataBlock + 1; // block group descriptor table is in the block after the superblock
    var bgdtOffset = (long)bgdtBlock * _blockSize;
    var groupCount = (_blocksCount + _blocksPerGroup - 1) / _blocksPerGroup;
    _bgInodeTableBlock = new uint[groupCount];

    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + g * 32;
      if (bgOffset + 32 > _data.Length) break;
      _bgInodeTableBlock[g] = BinaryPrimitives.ReadUInt32LittleEndian(
        _data.AsSpan((int)bgOffset + 8));
    }

    // Read root directory (inode 2)
    var rootInodeData = ReadInode(RootInode);
    if (rootInodeData == null) return;

    var rootMode = BinaryPrimitives.ReadUInt16LittleEndian(rootInodeData);
    if ((rootMode & InodeModeDir) == 0) return;

    var rootBlocks = ReadInodeBlocks(rootInodeData);
    ReadDirectoryEntries(rootBlocks, "");
  }

  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0 || _inodesPerGroup == 0) return null;
    var group = (inodeNum - 1) / _inodesPerGroup;
    var index = (inodeNum - 1) % _inodesPerGroup;

    if (group >= _bgInodeTableBlock.Length) return null;
    var tableBlock = _bgInodeTableBlock[group];
    var offset = (long)tableBlock * _blockSize + (long)index * _inodeSize;

    if (offset + _inodeSize > _data.Length) return null;
    return _data.AsSpan((int)offset, _inodeSize).ToArray();
  }

  private byte[] ReadInodeBlocks(byte[] inode) {
    var sizelow = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));

    var usesExtents = (flags & ExtentsFlag) != 0 &&
                      (_featureIncompat & (1u << 6)) != 0;

    if (usesExtents)
      return ReadExtentTree(inode, sizelow);
    else
      return ReadBlockPointers(inode, sizelow);
  }

  private byte[] ReadBlockPointers(byte[] inode, uint size) {
    using var ms = new MemoryStream();
    var remaining = (long)size;

    // 12 direct block pointers at inode offset 40
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var blockNum = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      if (blockNum == 0) break;
      var toRead = (int)Math.Min(remaining, _blockSize);
      var offset = (long)blockNum * _blockSize;
      if (offset + toRead > _data.Length) break;
      ms.Write(_data, (int)offset, toRead);
      remaining -= toRead;
    }

    // Indirect block (block pointer #12, at inode offset 40 + 48 = 88)
    if (remaining > 0) {
      var indirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (indirectBlock != 0)
        ReadIndirectBlock(indirectBlock, ms, ref remaining, 1);
    }

    // Double-indirect block (block pointer #13, at inode offset 92)
    if (remaining > 0) {
      var dindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (dindirectBlock != 0)
        ReadIndirectBlock(dindirectBlock, ms, ref remaining, 2);
    }

    // Triple-indirect block (block pointer #14, at inode offset 96)
    if (remaining > 0) {
      var tindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (tindirectBlock != 0)
        ReadIndirectBlock(tindirectBlock, ms, ref remaining, 3);
    }

    return ms.ToArray();
  }

  private void ReadIndirectBlock(uint blockNum, MemoryStream ms, ref long remaining, int level) {
    if (blockNum == 0 || remaining <= 0) return;
    var offset = (long)blockNum * _blockSize;
    if (offset + _blockSize > _data.Length) return;

    var pointersPerBlock = _blockSize / 4;

    for (var i = 0; i < pointersPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(
        _data.AsSpan((int)offset + i * 4));
      if (ptr == 0) break;

      if (level == 1) {
        var toRead = (int)Math.Min(remaining, _blockSize);
        var dataOff = (long)ptr * _blockSize;
        if (dataOff + toRead > _data.Length) break;
        ms.Write(_data, (int)dataOff, toRead);
        remaining -= toRead;
      } else {
        ReadIndirectBlock(ptr, ms, ref remaining, level - 1);
      }
    }
  }

  private byte[] ReadExtentTree(byte[] inode, uint size) {
    using var ms = new MemoryStream();
    var remaining = (long)size;

    // Extent header at inode offset 40
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
    if (ehMagic != ExtentMagic) return [];

    var ehEntries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
    var ehDepth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));

    ReadExtentNode(inode.AsSpan(40, 60).ToArray(), 0, ehEntries, ehDepth, ms, ref remaining);

    return ms.ToArray();
  }

  private void ReadExtentNode(byte[] nodeData, int headerOffset, int entries, int depth, MemoryStream ms, ref long remaining) {
    if (depth == 0) {
      // Leaf node - read extents
      for (var i = 0; i < entries && remaining > 0; i++) {
        var extOffset = headerOffset + 12 + i * 12; // header is 12 bytes, each extent is 12 bytes
        if (extOffset + 12 > nodeData.Length) break;

        // Extent: ee_block(4), ee_len(2), ee_start_hi(2), ee_start_lo(4)
        var len = BinaryPrimitives.ReadUInt16LittleEndian(nodeData.AsSpan(extOffset + 4));
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(nodeData.AsSpan(extOffset + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(nodeData.AsSpan(extOffset + 8));
        var startBlock = ((long)startHi << 32) | startLo;

        // Uninitialized extent flag: top bit of len
        var actualLen = len & 0x7FFF;

        for (var b = 0; b < actualLen && remaining > 0; b++) {
          var blockOff = (startBlock + b) * _blockSize;
          if (blockOff + _blockSize > _data.Length) break;
          var toRead = (int)Math.Min(remaining, _blockSize);
          ms.Write(_data, (int)blockOff, toRead);
          remaining -= toRead;
        }
      }
    } else {
      // Internal node - read index entries and recurse
      for (var i = 0; i < entries && remaining > 0; i++) {
        var idxOffset = headerOffset + 12 + i * 12;
        if (idxOffset + 12 > nodeData.Length) break;

        // Index: ei_block(4), ei_leaf_lo(4), ei_leaf_hi(2), ei_unused(2)
        var leafLo = BinaryPrimitives.ReadUInt32LittleEndian(nodeData.AsSpan(idxOffset + 4));
        var leafHi = BinaryPrimitives.ReadUInt16LittleEndian(nodeData.AsSpan(idxOffset + 8));
        var leafBlock = ((long)leafHi << 32) | leafLo;

        var blockOff = leafBlock * _blockSize;
        if (blockOff + _blockSize > _data.Length) break;

        var childNode = _data.AsSpan((int)blockOff, _blockSize).ToArray();
        var childMagic = BinaryPrimitives.ReadUInt16LittleEndian(childNode);
        if (childMagic != ExtentMagic) continue;

        var childEntries = BinaryPrimitives.ReadUInt16LittleEndian(childNode.AsSpan(2));
        var childDepth = BinaryPrimitives.ReadUInt16LittleEndian(childNode.AsSpan(6));

        ReadExtentNode(childNode, 0, childEntries, childDepth, ms, ref remaining);
      }
    }
  }

  private void ReadDirectoryEntries(byte[] dirData, string path) {
    var offset = 0;
    var seen = new HashSet<uint>();

    while (offset + 8 <= dirData.Length) {
      var inodeNum = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(offset));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(offset + 4));
      var nameLen = dirData[offset + 6];
      var fileType = dirData[offset + 7];

      if (recLen == 0) break;
      if (offset + 8 + nameLen > dirData.Length) break;

      if (inodeNum != 0 && nameLen > 0) {
        var name = Encoding.UTF8.GetString(dirData, offset + 8, nameLen);

        // Skip . and ..
        if (name is not ("." or "..")) {
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          var isDir = fileType == 2;

          // Read inode to get file size and timestamps
          long fileSize = 0;
          DateTime? lastMod = null;

          var inodeData = ReadInode(inodeNum);
          if (inodeData != null) {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
            isDir = (mode & InodeModeDir) != 0;
            fileSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(4));

            // mtime at inode offset 16
            var mtime = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(16));
            if (mtime != 0) {
              try {
                lastMod = DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime;
              } catch { /* ignore invalid timestamps */ }
            }
          }

          _entries.Add(new ExtEntry {
            Name = fullPath,
            Size = isDir ? 0 : fileSize,
            IsDirectory = isDir,
            LastModified = lastMod,
            Inode = inodeNum,
          });

          // Recurse into subdirectories
          if (isDir && inodeData != null && seen.Add(inodeNum)) {
            var subDirData = ReadInodeBlocks(inodeData);
            ReadDirectoryEntries(subDirData, fullPath);
          }
        }
      }

      offset += recLen;
    }
  }

  public byte[] Extract(ExtEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Inode == 0) return [];

    var inodeData = ReadInode(entry.Inode);
    if (inodeData == null) return [];

    var data = ReadInodeBlocks(inodeData);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
