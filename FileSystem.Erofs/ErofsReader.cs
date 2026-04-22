#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Erofs;

/// <summary>
/// Reads EROFS (Enhanced Read-Only File System) images as used by Android system/APEX
/// partitions since ~2019. Handles the uncompressed-inode layout (and inline-data
/// variant); LZ4 / LZMA compressed clusters and fragments are currently deferred —
/// AOSP ships a mix of both, and an image with exclusively compressed inodes will
/// surface its entries as zero-length payloads rather than failing.
/// <para>
/// Superblock is at file offset 1024; magic <c>0xE0F5E2E0</c>. Block size is
/// <c>2^sb.blkszbits</c> (almost always 4096). Inodes are addressed by nid into a
/// 32-byte-unit array starting at <c>meta_blkaddr * blockSize</c>.
/// </para>
/// </summary>
public sealed class ErofsReader {
  public sealed record Entry(string Path, long Size, bool IsDirectory, ulong Nid);

  public const uint Magic = 0xE0F5E2E0u;

  private readonly byte[] _data;
  private readonly int _blockSize;
  private readonly uint _metaBlkAddr;
  private readonly ulong _rootNid;
  private readonly List<Entry> _entries = [];

  public IReadOnlyList<Entry> Entries => this._entries;

  public ErofsReader(byte[] data) {
    this._data = data;
    if (data.Length < 1024 + 128)
      throw new InvalidDataException("EROFS image too small for superblock.");

    // Superblock at offset 1024.
    var sb = data.AsSpan(1024);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (magic != Magic)
      throw new InvalidDataException($"EROFS: bad superblock magic 0x{magic:X8} (want 0xE0F5E2E0).");

    var blkszbits = sb[12];
    this._blockSize = 1 << blkszbits;
    this._rootNid = BinaryPrimitives.ReadUInt16LittleEndian(sb[16..]);
    this._metaBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(sb[36..]);

    this.Walk(this._rootNid, "");
  }

  private void Walk(ulong nid, string pathPrefix) {
    var inodeOffset = (long)this._metaBlkAddr * this._blockSize + (long)(nid * 32);
    if (inodeOffset + 32 > this._data.Length) return;

    var inode = this._data.AsSpan((int)inodeOffset);
    var format = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var isExtended = (format & 0x01) != 0;
    var layout = (format >> 1) & 0x07;

    ushort mode;
    long size;
    uint rawBlkAddr;
    int inodeHeaderSize;

    if (isExtended) {
      // erofs_inode_extended (64 bytes)
      mode = BinaryPrimitives.ReadUInt16LittleEndian(inode[4..]);
      size = BinaryPrimitives.ReadInt64LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      inodeHeaderSize = 64;
    } else {
      // erofs_inode_compact (32 bytes)
      mode = BinaryPrimitives.ReadUInt16LittleEndian(inode[4..]);
      size = BinaryPrimitives.ReadUInt32LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      inodeHeaderSize = 32;
    }

    var isDir = (mode & 0xF000) == 0x4000; // S_IFDIR
    var isReg = (mode & 0xF000) == 0x8000; // S_IFREG
    if (!isDir && !isReg) return;  // skip devices/symlinks/sockets for this pass

    if (isDir) {
      // Walk directory entries. Data layout 0 (plain) points at rawBlkAddr; layout 2
      // (inline) places the tail inside the same block as the inode.
      var dirData = this.ReadInodeData(inodeOffset, inodeHeaderSize, size, layout, rawBlkAddr);
      this.WalkDirBlock(dirData, pathPrefix, nid);
    } else {
      // Regular file — register an entry with the whole computed path.
      this._entries.Add(new Entry(pathPrefix.TrimEnd('/'), size, IsDirectory: false, nid));
    }
  }

  // Layouts: 0 = plain (contiguous blocks starting at rawBlkAddr),
  // 2 = inline (last chunk lives in the same block as the inode after the header).
  // Compressed layouts (1, 4) return an empty buffer for now.
  private byte[] ReadInodeData(long inodeOffset, int headerSize, long size, int layout, uint rawBlkAddr) {
    if (size == 0) return [];
    if (size > int.MaxValue) throw new InvalidDataException("EROFS: file too large.");

    return layout switch {
      0 => this.ReadPlain((int)rawBlkAddr * (long)this._blockSize, (int)size),
      2 => this.ReadInline(inodeOffset, headerSize, rawBlkAddr, (int)size),
      _ => [],  // compressed layouts not yet supported
    };
  }

  private byte[] ReadPlain(long offset, int length) {
    if (offset + length > this._data.Length)
      length = (int)Math.Max(0, this._data.Length - offset);
    var buf = new byte[length];
    this._data.AsSpan((int)offset, length).CopyTo(buf);
    return buf;
  }

  private byte[] ReadInline(long inodeOffset, int headerSize, uint rawBlkAddr, int size) {
    // Full blocks (if any) live at rawBlkAddr; the tail fragment sits immediately after
    // the inode header within the meta block.
    var fullBlocks = size / this._blockSize;
    var tail = size - fullBlocks * this._blockSize;
    var buf = new byte[size];

    if (fullBlocks > 0) {
      var src = (long)rawBlkAddr * this._blockSize;
      var take = Math.Min(fullBlocks * this._blockSize, this._data.Length - src);
      if (take > 0)
        this._data.AsSpan((int)src, (int)take).CopyTo(buf);
    }
    if (tail > 0) {
      var tailSrc = inodeOffset + headerSize;
      var take = (int)Math.Min(tail, this._data.Length - tailSrc);
      if (take > 0)
        this._data.AsSpan((int)tailSrc, take).CopyTo(buf.AsSpan(fullBlocks * this._blockSize));
    }
    return buf;
  }

  // Each directory block is packed as: [erofs_dirent[] headers][name bytes].
  // Headers are 12 bytes each; nameoff points into the block; names extend to the next
  // entry's nameoff (or block end for the last entry).
  private void WalkDirBlock(byte[] dirData, string pathPrefix, ulong selfNid) {
    if (dirData.Length < 12) return;

    var firstNameOff = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(8));
    var entryCount = firstNameOff / 12;

    for (var i = 0; i < entryCount; ++i) {
      var eOff = i * 12;
      if (eOff + 12 > dirData.Length) break;
      var nid = BinaryPrimitives.ReadUInt64LittleEndian(dirData.AsSpan(eOff));
      var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(eOff + 8));

      int nameEnd;
      if (i + 1 < entryCount)
        nameEnd = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(eOff + 12 + 8));
      else
        nameEnd = dirData.Length;
      // Names are NUL-padded at end; find the real terminator.
      var rawName = dirData.AsSpan(nameOff, nameEnd - nameOff);
      var zero = rawName.IndexOf((byte)0);
      if (zero >= 0) rawName = rawName[..zero];
      var name = Encoding.UTF8.GetString(rawName);

      // Skip self/parent entries.
      if (name is "." or "..") continue;
      if (nid == selfNid) continue;

      this.Walk(nid, pathPrefix + name + "/");
      // If Walk determined it was a regular file, the entry was added with the pathPrefix+name
      // joined; but Walk also trims trailing slash. For files the "/" we appended won't hurt
      // because the file Walk trims it. For directories we want to keep descending into them,
      // which Walk does via recursion.
    }
  }

  /// <summary>
  /// Extracts the raw bytes of a given entry. Throws if the entry points at a
  /// compressed-layout inode (until LZ4 support lands).
  /// </summary>
  public byte[] ExtractFile(Entry entry) {
    var inodeOffset = (long)this._metaBlkAddr * this._blockSize + (long)(entry.Nid * 32);
    var inode = this._data.AsSpan((int)inodeOffset);
    var format = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var isExtended = (format & 0x01) != 0;
    var layout = (format >> 1) & 0x07;

    long size;
    uint rawBlkAddr;
    int headerSize;
    if (isExtended) {
      size = BinaryPrimitives.ReadInt64LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      headerSize = 64;
    } else {
      size = BinaryPrimitives.ReadUInt32LittleEndian(inode[8..]);
      rawBlkAddr = BinaryPrimitives.ReadUInt32LittleEndian(inode[16..]);
      headerSize = 32;
    }

    return layout switch {
      0 or 2 => this.ReadInodeData(inodeOffset, headerSize, size, layout, rawBlkAddr),
      _ => throw new NotSupportedException(
        $"EROFS inode at nid {entry.Nid} uses compressed layout {layout}; decompression not yet implemented."),
    };
  }
}
