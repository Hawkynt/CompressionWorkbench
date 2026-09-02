#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ext;

/// <summary>
/// Reads ext2/ext3/ext4 filesystem images. Parses the superblock, block group
/// descriptors, inode table, and directory entries. Supports both direct/indirect
/// block pointers (ext2/3) and extent trees (ext4).
/// </summary>
public sealed class ExtReader : IDisposable {
  /// <summary>
  /// Random-access view. An ext2/3/4 volume is routinely far larger than an array,
  /// so the image is never copied into one.
  /// </summary>
  private readonly ImageAccessor _data;
  private readonly List<ExtEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
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
  private const ushort InodeModeSymlink = 0xA000;
  private const ushort InodeFormatMask = 0xF000;
  // Fast symlinks store the target inline in the 60-byte i_block[] area (inode
  // offset 40). ext uses the inline form whenever the target fits in that area,
  // i.e. i_size < 60; longer ("slow") targets live in the file's data blocks.
  private const int FastSymlinkMaxLen = 60;
  private const uint ExtentsFlag = 0x80000;
  private const ushort ExtentMagic = 0xF30A;
  private const uint RootInode = 2;

    /// <summary>
  /// Initializes a new instance of <see cref="ExtReader"/>.
  /// </summary>
public ExtReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _data = new ImageAccessor(stream, leaveOpen: true);
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext: image too small for superblock.");

    // Read superblock at offset 1024
    var sb = _data.Read(SuperblockOffset, 1024).AsSpan();
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

    // A 64BIT volume writes wider group descriptors and says how wide in the
    // superblock; stepping through the table 32 bytes at a time then lands in the
    // middle of the second one.
    var descriptorSize = ExtBlockGroupGeometry.DescriptorSize(sb);
    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + (long)g * descriptorSize;
      if (bgOffset + 32 > _data.Length) break;
      _bgInodeTableBlock[g] = BinaryPrimitives.ReadUInt32LittleEndian(
        _data.Read(bgOffset + 8, 8).AsSpan());
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
    return _data.Read(offset, _inodeSize);
  }

  private byte[] ReadInodeBlocks(byte[] inode) {
    using var ms = new MemoryStream();
    WriteInodeBlocks(inode, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Walks the inode's block map or extent tree and copies the file's bytes into
  /// <paramref name="destination" />. Nothing larger than one block is held, so a
  /// file past what a byte[] can carry is extracted the same way as any other.
  /// </summary>
  private void WriteInodeBlocks(byte[] inode, Stream destination) {
    var sizelow = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(32));

    var usesExtents = (flags & ExtentsFlag) != 0 &&
                      (_featureIncompat & (1u << 6)) != 0;

    if (usesExtents)
      ReadExtentTree(inode, sizelow, destination);
    else
      ReadBlockPointers(inode, sizelow, destination);
  }

  /// <summary>
  /// Emits the zeros a hole stands for, and counts them against what is left.
  /// </summary>
  /// <remarks>
  /// A zero block pointer in ext does not mean the file stops there. It means the
  /// file has nothing in that block, and every reader of the format hands back
  /// zeros for it and carries on. Stopping instead returned a file cut off at its
  /// first hole -- a 400 KB file beginning with one block of data came back as
  /// that one block -- which is also how a volume left sparse by mke2fs, or by
  /// anything else that writes ext, would have been read here.
  /// </remarks>
  private static void ReadHole(Stream ms, ref long remaining, long bytes) {
    var toWrite = Math.Min(remaining, bytes);
    if (toWrite <= 0) return;

    var zeros = new byte[Math.Min(toWrite, 64 * 1024)];
    while (toWrite > 0) {
      var chunk = (int)Math.Min(zeros.Length, toWrite);
      ms.Write(zeros, 0, chunk);
      toWrite -= chunk;
      remaining -= chunk;
    }
  }

  private void ReadBlockPointers(byte[] inode, uint size, Stream ms) {
    var remaining = (long)size;
    var pointersPerBlock = (long)(_blockSize / 4);

    // 12 direct block pointers at inode offset 40
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var blockNum = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      var toRead = (int)Math.Min(remaining, _blockSize);
      if (blockNum == 0) { ReadHole(ms, ref remaining, toRead); continue; }

      var offset = (long)blockNum * _blockSize;
      if (offset + toRead > _data.Length) break;
      _data.CopyTo(offset, ms, toRead);
      remaining -= toRead;
    }

    // Indirect block (block pointer #12, at inode offset 40 + 48 = 88)
    if (remaining > 0) {
      var indirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (indirectBlock != 0) ReadIndirectBlock(indirectBlock, ms, ref remaining, 1);
      else ReadHole(ms, ref remaining, pointersPerBlock * _blockSize);   // a zero root is a hole the width of the level
    }

    // Double-indirect block (block pointer #13, at inode offset 92)
    if (remaining > 0) {
      var dindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (dindirectBlock != 0) ReadIndirectBlock(dindirectBlock, ms, ref remaining, 2);
      else ReadHole(ms, ref remaining, pointersPerBlock * pointersPerBlock * _blockSize);   // a zero root is a hole the width of the level
    }

    // Triple-indirect block (block pointer #14, at inode offset 96)
    if (remaining > 0) {
      var tindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (tindirectBlock != 0) ReadIndirectBlock(tindirectBlock, ms, ref remaining, 3);
      else ReadHole(ms, ref remaining, pointersPerBlock * pointersPerBlock * pointersPerBlock * _blockSize);   // a zero root is a hole the width of the level
    }
  }

  private void ReadIndirectBlock(uint blockNum, Stream ms, ref long remaining, int level) {
    if (blockNum == 0 || remaining <= 0) return;
    var offset = (long)blockNum * _blockSize;
    if (offset + _blockSize > _data.Length) return;

    var pointersPerBlock = _blockSize / 4;

    for (var i = 0; i < pointersPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(
        _data.Read(offset + i * 4, 4).AsSpan());
      if (level == 1) {
        var toRead = (int)Math.Min(remaining, _blockSize);
        if (ptr == 0) { ReadHole(ms, ref remaining, toRead); continue; }

        var dataOff = (long)ptr * _blockSize;
        if (dataOff + toRead > _data.Length) break;
        _data.CopyTo(dataOff, ms, toRead);
        remaining -= toRead;
      } else if (ptr == 0) {
        // Everything this pointer would have addressed is hole.
        var span = (long)_blockSize;
        for (var l = 1; l < level; ++l) span *= pointersPerBlock;
        ReadHole(ms, ref remaining, span);
      } else {
        ReadIndirectBlock(ptr, ms, ref remaining, level - 1);
      }
    }
  }

  private void ReadExtentTree(byte[] inode, uint size, Stream ms) {
    var remaining = (long)size;

    // Extent header at inode offset 40
    var ehMagic = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(40));
    if (ehMagic != ExtentMagic) return;

    var ehEntries = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(42));
    var ehDepth = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(46));

    var logicalBlock = 0L;
    ReadExtentNode(inode.AsSpan(40, 60).ToArray(), 0, ehEntries, ehDepth, ms,
      ref remaining, ref logicalBlock);
  }

  /// <summary>
  /// Walks an extent tree, keeping track of where in the file it has got to.
  /// </summary>
  /// <remarks>
  /// <para>An extent says which logical block of the file it starts at, and that
  /// is not the same as the order the extents come in. A file with a hole in it
  /// simply has no extent covering it: the next extent's ee_block jumps past the
  /// gap. Concatenating the extents and ignoring ee_block gives back the
  /// allocated blocks only, packed together -- so a two-hundred-kilobyte file
  /// with something at each end came back as eight kilobytes, its two ends
  /// touching, at the wrong length and with the data in the wrong place.</para>
  ///
  /// <para>ext4 is what mke2fs writes by default and what sparse files on Linux
  /// mostly live on, so this is the common case rather than a corner of one.</para>
  ///
  /// <para>An extent flagged uninitialised is allocated and never written, and
  /// reads as zeros. Copying the blocks it names would hand back whatever those
  /// blocks happen to hold, which is somebody else's deleted data.</para>
  /// </remarks>
  private void ReadExtentNode(byte[] nodeData, int headerOffset, int entries, int depth, Stream ms,
      ref long remaining, ref long logicalBlock) {
    if (depth == 0) {
      // Leaf node - read extents
      for (var i = 0; i < entries && remaining > 0; i++) {
        var extOffset = headerOffset + 12 + i * 12; // header is 12 bytes, each extent is 12 bytes
        if (extOffset + 12 > nodeData.Length) break;

        // Extent: ee_block(4), ee_len(2), ee_start_hi(2), ee_start_lo(4)
        var eeBlock = BinaryPrimitives.ReadUInt32LittleEndian(nodeData.AsSpan(extOffset));
        var len = BinaryPrimitives.ReadUInt16LittleEndian(nodeData.AsSpan(extOffset + 4));
        var startHi = BinaryPrimitives.ReadUInt16LittleEndian(nodeData.AsSpan(extOffset + 6));
        var startLo = BinaryPrimitives.ReadUInt32LittleEndian(nodeData.AsSpan(extOffset + 8));
        var startBlock = ((long)startHi << 32) | startLo;

        // Whatever lies between where the file has got to and where this extent
        // begins is a hole, and has to be given back as the zeros it stands for.
        if (eeBlock > logicalBlock) {
          ReadHole(ms, ref remaining, (eeBlock - logicalBlock) * _blockSize);
          logicalBlock = eeBlock;
          if (remaining <= 0) break;
        }

        // A length over 32768 marks an extent that is allocated and unwritten.
        var uninitialised = len > 32768;
        var actualLen = uninitialised ? len - 32768 : len;

        for (var b = 0; b < actualLen && remaining > 0; b++) {
          var toRead = (int)Math.Min(remaining, _blockSize);
          if (uninitialised) { ReadHole(ms, ref remaining, toRead); continue; }

          var blockOff = (startBlock + b) * _blockSize;
          if (blockOff + _blockSize > _data.Length) break;
          _data.CopyTo(blockOff, ms, toRead);
          remaining -= toRead;
        }

        logicalBlock += actualLen;
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

        var childNode = _data.Read(blockOff, _blockSize);
        var childMagic = BinaryPrimitives.ReadUInt16LittleEndian(childNode);
        if (childMagic != ExtentMagic) continue;

        var childEntries = BinaryPrimitives.ReadUInt16LittleEndian(childNode.AsSpan(2));
        var childDepth = BinaryPrimitives.ReadUInt16LittleEndian(childNode.AsSpan(6));

        ReadExtentNode(childNode, 0, childEntries, childDepth, ms, ref remaining, ref logicalBlock);
      }
    }
  }

  /// <summary>The directory e2fsck reconnects orphaned files into, which mkfs makes.</summary>
  private const string LostAndFoundName = "lost+found";

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

        // Skip . and .., and the lost+found every ext volume is made with — it
        // belongs to the volume rather than to whoever filled it, and surfacing it
        // would put a directory nobody added into every listing.
        if (name is not ("." or "..") && !(path.Length == 0 && name == LostAndFoundName)) {
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
          var isDir = fileType == 2;

          // Read inode to get file size and timestamps
          long fileSize = 0;
          DateTime? lastMod = null;
          var isSymlink = false;
          string? linkTarget = null;

          var inodeData = ReadInode(inodeNum);
          if (inodeData != null) {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
            isDir = (mode & InodeFormatMask) == InodeModeDir;
            isSymlink = (mode & InodeFormatMask) == InodeModeSymlink;
            fileSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(4));
            if (isSymlink)
              linkTarget = ReadSymlinkTarget(inodeData, fileSize);

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
            // A symlink's own size is the target-path byte length (i_size).
            Size = isDir ? 0 : fileSize,
            IsDirectory = isDir,
            IsSymlink = isSymlink,
            LinkTarget = linkTarget,
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

  // Decodes an S_IFLNK inode's target path. Fast symlinks (i_size < 60) store the
  // target inline in the 60-byte i_block[] area at inode offset 40; slow symlinks
  // store it in the file's data block(s), reached through the normal block/extent
  // walk. References: linux fs/ext4/symlink.c, e2fsprogs "fast symlink" handling.
  private string? ReadSymlinkTarget(byte[] inode, long size) {
    if (size <= 0) return "";
    if (size > _blockSize * 8L) return null; // implausibly long target — refuse
    if (size < FastSymlinkMaxLen) {
      var span = inode.AsSpan(40, Math.Min((int)size, inode.Length - 40));
      return Encoding.UTF8.GetString(span);
    }
    var data = ReadInodeBlocks(inode);
    var n = (int)Math.Min(size, data.Length);
    return Encoding.UTF8.GetString(data, 0, n);
  }

  /// <summary>
  /// Copies <paramref name="entry" />'s bytes into <paramref name="destination" />
  /// without buffering the whole file, which an entry approaching ext's 4 GB
  /// i_size ceiling would not survive.
  /// </summary>
  public void ExtractTo(ExtEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return;
    if (entry.IsSymlink) {
      var target = Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
      destination.Write(target, 0, target.Length);
      return;
    }
    if (entry.Inode == 0) return;

    var inodeData = ReadInode(entry.Inode);
    if (inodeData == null) return;
    WriteInodeBlocks(inodeData, destination);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(ExtEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    // A symlink's on-disk content is its target path; surface exactly those bytes
    // rather than misreading the inline path text as block pointers.
    if (entry.IsSymlink)
      return Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
    if (entry.Inode == 0) return [];

    var inodeData = ReadInode(entry.Inode);
    if (inodeData == null) return [];

    var data = ReadInodeBlocks(inodeData);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
