#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.F2fs;

/// <summary>
/// Reads F2FS filesystem images using the on-disk layout defined by the Linux kernel
/// header <c>include/linux/f2fs_fs.h</c>. Handles both traditional data-block dentries
/// and inline dentries (i_inline F2FS_INLINE_DENTRY flag).
/// </summary>
public sealed class F2fsReader : IDisposable {
  // --- Superblock / on-disk constants ---
  private const uint F2fsMagic = 0xF2F52010;
  private const int SbOffset = 1024;
  private const uint RootNodeId = 3;
  private const int SlotLen = 8;

  // Inline flag bits (kernel f2fs_fs.h).
  private const byte F2fsInlineDentry = 0x04;

  // Superblock field offsets (relative to SB-struct start, i.e. file offset 1024).
  private const int SbLogBlocksizeOff = 16;
  private const int SbNatBlkAddrOff = 84;
  private const int SbMainBlkAddrOff = 92;

  // Inode field offsets (kernel struct f2fs_inode).
  private const int InodeModeOff = 0;
  private const int InodeInlineFlagOff = 3;
  private const int InodeSizeOff = 16;
  private const int InodeIAddrOff = 360; // start of i_addr[DEF_ADDRS_PER_INODE]
  private const int AddrsPerInode = 923;

  // Inline dentry region inside inode (see F2fsWriter.WriteRootInodeInline for layout).
  private const int InlineDentryStart = 360;
  private const int NrInlineDentry = 182;
  private const int InlineBitmapSize = (NrInlineDentry + 7) / 8; // 23
  private const int InlineReservedSize = 8;
  private const int InlineDentryBase = InlineDentryStart + InlineBitmapSize + InlineReservedSize;
  private const int InlineNameBase = InlineDentryBase + NrInlineDentry * 11;

  private readonly byte[] _data;
  private readonly List<F2fsEntry> _entries = [];

  private int _blockSize;
  private int _natBlkAddr; // in blocks
  private int _mainBlkAddr;

  public IReadOnlyList<F2fsEntry> Entries => this._entries;

  public F2fsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SbOffset + 200)
      throw new InvalidDataException("F2FS: image too small.");

    var sb = SbOffset;
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(sb));
    if (magic != F2fsMagic)
      throw new InvalidDataException("F2FS: invalid superblock magic.");

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(sb + SbLogBlocksizeOff));
    this._blockSize = 1 << (int)logBlockSize;
    if (this._blockSize < 512) this._blockSize = 4096;

    this._natBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(sb + SbNatBlkAddrOff));
    this._mainBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(sb + SbMainBlkAddrOff));

    this.ReadDirectory(RootNodeId, "");
  }

  private int LookupNat(uint nodeId) {
    // NAT entry layout: version(1) + ino(4) + block_addr(4) = 9 bytes.
    var entriesPerBlock = this._blockSize / 9;
    if (entriesPerBlock == 0) entriesPerBlock = 455;
    var natBlock = (int)(nodeId / entriesPerBlock);
    var natIdx = (int)(nodeId % entriesPerBlock);
    var natOff = (long)(this._natBlkAddr + natBlock) * this._blockSize + natIdx * 9;
    if (natOff + 9 > this._data.Length) return -1;

    var blockAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan((int)natOff + 5));
    return blockAddr;
  }

  private void ReadDirectory(uint nodeId, string basePath) {
    var blockAddr = this.LookupNat(nodeId);
    if (blockAddr <= 0) return;
    var nodeOff = (long)blockAddr * this._blockSize;
    if (nodeOff + this._blockSize > this._data.Length) return;
    var noff = (int)nodeOff;

    var mode = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(noff + InodeModeOff));
    if ((mode & 0xF000) != 0x4000) return; // not directory

    var inlineFlag = this._data[noff + InodeInlineFlagOff];

    if ((inlineFlag & F2fsInlineDentry) != 0) {
      this.ParseInlineDentries(noff, basePath);
      return;
    }

    // Traditional layout: iterate i_addr[] data blocks, each a dentry block.
    for (var i = 0; i < AddrsPerInode; ++i) {
      var addrOff = noff + InodeIAddrOff + i * 4;
      if (addrOff + 4 > this._data.Length) break;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(addrOff));
      if (dataBlock == 0) continue;

      var dataOff = (long)dataBlock * this._blockSize;
      if (dataOff + this._blockSize > this._data.Length) continue;

      this.ParseDentryBlock((int)dataOff, basePath);
    }
  }

  /// <summary>
  /// Parses inline dentries embedded in the inode (F2FS_INLINE_DENTRY layout).
  /// </summary>
  private void ParseInlineDentries(int inodeOff, string basePath) {
    var bitmapOff = inodeOff + InlineDentryStart;
    var dentryOff = inodeOff + InlineDentryBase;
    var nameOff = inodeOff + InlineNameBase;

    for (var i = 0; i < NrInlineDentry; ++i) {
      var byteIdx = bitmapOff + i / 8;
      if (byteIdx >= this._data.Length) break;
      if ((this._data[byteIdx] & (1 << (i % 8))) == 0) continue;

      var entryOff = dentryOff + i * 11;
      if (entryOff + 11 > this._data.Length) break;

      var ino = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(entryOff + 8));
      var fileType = this._data[entryOff + 10];
      if (ino == 0 || nameLen == 0 || nameLen > 255) continue;

      var fnOff = nameOff + i * SlotLen;
      var fnLen = Math.Min(nameLen, Math.Min(SlotLen, this._data.Length - fnOff));
      if (fnLen <= 0 || fnOff + fnLen > this._data.Length) continue;

      var name = Encoding.UTF8.GetString(this._data, fnOff, fnLen);
      name = name.TrimEnd('\0');
      if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var isDir = fileType == 2;
      long childSize = 0;
      if (!isDir) childSize = this.ReadInodeSize(ino);

      this._entries.Add(new F2fsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        NodeId = ino,
      });

      if (isDir) this.ReadDirectory(ino, fullPath);
    }
  }

  private void ParseDentryBlock(int blockOff, string basePath) {
    // F2FS dentry block: bitmap(27 bytes) + reserved(3) + dentry[214](11 each) + filename[214][8].
    var nrDentry = 214;
    var bitmapSize = (nrDentry + 7) / 8; // 27
    const int reserved = 3;
    var dentryOff = blockOff + bitmapSize + reserved;
    var nameOff = dentryOff + nrDentry * 11;

    for (var i = 0; i < nrDentry; ++i) {
      var byteIdx = blockOff + i / 8;
      if (byteIdx >= this._data.Length) break;
      if ((this._data[byteIdx] & (1 << (i % 8))) == 0) continue;

      var entryOff = dentryOff + i * 11;
      if (entryOff + 11 > this._data.Length) break;

      var ino = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(entryOff + 8));
      var fileType = this._data[entryOff + 10];
      if (ino == 0 || nameLen == 0 || nameLen > 255) continue;

      var fnOff = nameOff + i * SlotLen;
      var fnLen = Math.Min(nameLen, this._data.Length - fnOff);
      if (fnLen <= 0 || fnOff + fnLen > this._data.Length) continue;

      var name = Encoding.UTF8.GetString(this._data, fnOff, fnLen);
      name = name.TrimEnd('\0');
      if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

      var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
      var isDir = fileType == 2;
      long childSize = 0;
      if (!isDir) childSize = this.ReadInodeSize(ino);

      this._entries.Add(new F2fsEntry {
        Name = fullPath,
        Size = isDir ? 0 : childSize,
        IsDirectory = isDir,
        NodeId = ino,
      });

      if (isDir) this.ReadDirectory(ino, fullPath);
    }
  }

  private long ReadInodeSize(uint ino) {
    var childBlock = this.LookupNat(ino);
    if (childBlock <= 0) return 0;
    var childOff = (long)childBlock * this._blockSize;
    if (childOff + InodeSizeOff + 8 > this._data.Length) return 0;
    return (long)BinaryPrimitives.ReadUInt64LittleEndian(this._data.AsSpan((int)childOff + InodeSizeOff));
  }

  public byte[] Extract(F2fsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var blockAddr = this.LookupNat(entry.NodeId);
    if (blockAddr <= 0) return [];
    var nodeOff = (long)blockAddr * this._blockSize;
    if (nodeOff + this._blockSize > this._data.Length) return [];
    var noff = (int)nodeOff;

    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(this._data.AsSpan(noff + InodeSizeOff));
    if (size <= 0) return [];

    using var ms = new MemoryStream();
    for (var i = 0; i < AddrsPerInode && ms.Length < size; ++i) {
      var addrOff = noff + InodeIAddrOff + i * 4;
      if (addrOff + 4 > this._data.Length) break;
      var dataBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(addrOff));
      if (dataBlock == 0) continue;
      var dataOff = (long)dataBlock * this._blockSize;
      var len = (int)Math.Min(this._blockSize, size - ms.Length);
      if (dataOff + len <= this._data.Length)
        ms.Write(this._data, (int)dataOff, len);
    }

    var result = ms.ToArray();
    if (result.Length > size)
      return result.AsSpan(0, (int)size).ToArray();
    return result;
  }

  public void Dispose() { }
}
