#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ext1;

/// <summary>
/// Reads ext1 (1992) filesystem images — the predecessor of ext2 by Rémy Card.
/// Identical to GOOD_OLD-revision ext2 byte-for-byte except:
/// <list type="bullet">
///   <item><description>Magic at superblock offset 56 is <c>0xEF51</c> (not <c>0xEF53</c>).</description></item>
///   <item><description>Directory entries use rev-0 layout: <c>inode(4) + rec_len(2) +
///   name_len(2) + name[]</c> — the 16-bit <c>name_len</c> is NOT split into
///   <c>name_len(8) + file_type(8)</c>.</description></item>
///   <item><description>Inodes are a fixed 128 bytes (no <c>s_inode_size</c> field — rev-0
///   does not honour dynamic-rev fields).</description></item>
/// </list>
/// <para>
/// Only direct + indirect block pointers are honoured (no extents, since extents arrived
/// with ext4). Use <see cref="Ext1Reader"/> for full file content extraction; the broader
/// <see cref="Ext1FormatDescriptor"/> still surfaces a <c>FULL.ext1</c> + metadata view.
/// </para>
/// </summary>
public sealed class Ext1Reader : IDisposable {
  /// <summary>Random-access view; the image is never copied into an array.</summary>
  private readonly ImageAccessor _data;
  private readonly List<Ext1Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Ext1Entry> Entries => this._entries;

  // Superblock fields
  private uint _inodesCount;
  private uint _blocksCount;
  private int _blockSize;
  private uint _blocksPerGroup;
  private uint _inodesPerGroup;
  private uint _firstDataBlock;
  private const ushort InodeSize = 128; // rev-0: hard-coded

  // Block group descriptor table
  private uint[] _bgInodeTableBlock = [];

  // Constants
  private const int SuperblockOffset = 1024;
  private const ushort Ext1Magic = 0xEF51;
  private const ushort InodeModeDir = 0x4000;
  private const uint RootInode = 2;

  /// <summary>
  /// Initializes a new instance of <see cref="Ext1Reader"/>.
  /// </summary>
public Ext1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext1: image too small for superblock.");

    var sb = this._data.Read(SuperblockOffset, 1024).AsSpan();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(56));
    if (magic != Ext1Magic)
      throw new InvalidDataException($"ext1: invalid magic 0x{magic:X4}, expected 0xEF51.");

    this._inodesCount = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    this._blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4));
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24));
    this._blockSize = 1024 << (int)logBlockSize;
    this._blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32));
    this._inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(40));
    this._firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20));

    var bgdtBlock = this._firstDataBlock + 1;
    var bgdtOffset = (long)bgdtBlock * this._blockSize;
    var groupCount = this._blocksPerGroup == 0 ? 1u : (this._blocksCount + this._blocksPerGroup - 1) / this._blocksPerGroup;
    this._bgInodeTableBlock = new uint[groupCount];

    for (uint g = 0; g < groupCount; g++) {
      var bgOffset = bgdtOffset + g * 32;
      if (bgOffset + 32 > this._data.Length) break;
      this._bgInodeTableBlock[g] = BinaryPrimitives.ReadUInt32LittleEndian(
        this._data.Read(bgOffset + 8, 8).AsSpan());
    }

    var rootInodeData = this.ReadInode(RootInode);
    if (rootInodeData == null) return;

    var rootMode = BinaryPrimitives.ReadUInt16LittleEndian(rootInodeData);
    if ((rootMode & InodeModeDir) == 0) return;

    var rootBlocks = this.ReadInodeBlocks(rootInodeData);
    this.ReadDirectoryEntries(rootBlocks, "");
  }

  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0 || this._inodesPerGroup == 0) return null;
    var group = (inodeNum - 1) / this._inodesPerGroup;
    var index = (inodeNum - 1) % this._inodesPerGroup;

    if (group >= this._bgInodeTableBlock.Length) return null;
    var tableBlock = this._bgInodeTableBlock[group];
    var offset = (long)tableBlock * this._blockSize + (long)index * InodeSize;

    if (offset + InodeSize > this._data.Length) return null;
    return this._data.Read(offset, InodeSize);
  }

  private byte[] ReadInodeBlocks(byte[] inode) {
    using var ms = new MemoryStream();
    this.WriteInodeBlocks(inode, ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Walks the inode's block map and copies the file's bytes into
  /// <paramref name="destination" />. Nothing larger than one block is held, so a
  /// file past what a byte[] can carry is read the same way as any other.
  /// </summary>
  private void WriteInodeBlocks(byte[] inode, Stream destination) {
    var sizelow = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(4));
    this.ReadBlockPointers(inode, sizelow, destination);
  }


  /// <summary>
  /// Emits the zeros a hole stands for, and counts them against what is left.
  /// </summary>
  /// <remarks>
  /// A zero pointer in one of these block maps does not mean the file ends there.
  /// It means the file holds nothing in that block, and every reader of the format
  /// hands back zeros for it and carries on. Stopping instead cut a file off at
  /// its first hole -- which is how a volume any of the reference tools left
  /// sparse would have been read here, not merely one this project wrote.
  /// </remarks>
  private static void AppendHole(Stream ms, ref long remaining, long bytes) {
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

    // 12 direct block pointers at inode offset 40
    for (var i = 0; i < 12 && remaining > 0; i++) {
      var blockNum = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(40 + i * 4));
      var toRead = (int)Math.Min(remaining, this._blockSize);
      // A zero pointer is a hole, not the end of the file.
      if (blockNum == 0) { AppendHole(ms, ref remaining, toRead); continue; }

      var offset = (long)blockNum * this._blockSize;
      if (offset + toRead > this._data.Length) break;
      this._data.CopyTo(offset, ms, toRead);
      remaining -= toRead;
    }

    if (remaining > 0) {
      var indirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(88));
      if (indirectBlock != 0) this.ReadIndirectBlock(indirectBlock, ms, ref remaining, 1);
      // A zero root is a hole as wide as the level addresses, not a stop.
      else AppendHole(ms, ref remaining, (long)(this._blockSize / 4) * this._blockSize);
    }

    if (remaining > 0) {
      var dindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(92));
      if (dindirectBlock != 0) this.ReadIndirectBlock(dindirectBlock, ms, ref remaining, 2);
      // A zero root is a hole as wide as the level addresses, not a stop.
      else AppendHole(ms, ref remaining, (long)(this._blockSize / 4) * this._blockSize * (this._blockSize / 4));
    }

    if (remaining > 0) {
      var tindirectBlock = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(96));
      if (tindirectBlock != 0) this.ReadIndirectBlock(tindirectBlock, ms, ref remaining, 3);
      // A zero root is a hole as wide as the level addresses, not a stop.
      else AppendHole(ms, ref remaining, (long)(this._blockSize / 4) * this._blockSize * (this._blockSize / 4) * (this._blockSize / 4));
    }
  }

  private void ReadIndirectBlock(uint blockNum, Stream ms, ref long remaining, int level) {
    if (blockNum == 0 || remaining <= 0) return;
    var offset = (long)blockNum * this._blockSize;
    if (offset + this._blockSize > this._data.Length) return;

    var pointersPerBlock = this._blockSize / 4;
    for (var i = 0; i < pointersPerBlock && remaining > 0; i++) {
      var ptr = this._data.ReadUInt32(offset + i * 4);
      if (ptr == 0) {
        // Everything this pointer would have addressed is hole.
        var span = (long)this._blockSize;
        for (var l = 1; l < level; ++l) span *= pointersPerBlock;
        AppendHole(ms, ref remaining, span);
        continue;
      }
      if (level == 1) {
        var toRead = (int)Math.Min(remaining, this._blockSize);
        var dataOff = (long)ptr * this._blockSize;
        if (dataOff + toRead > this._data.Length) break;
        this._data.CopyTo(dataOff, ms, toRead);
        remaining -= toRead;
      } else
        this.ReadIndirectBlock(ptr, ms, ref remaining, level - 1);
    }
  }

  /// <summary>
  /// Walks rev-0 directory entries: 8-byte fixed header (inode, rec_len, 16-bit name_len)
  /// plus name. The rev-1 file_type byte is absent — the full 16-bit name_len occupies
  /// offsets +6..+7 of the entry header.
  /// </summary>
  private void ReadDirectoryEntries(byte[] dirData, string path) {
    var offset = 0;
    var seen = new HashSet<uint>();

    while (offset + 8 <= dirData.Length) {
      var inodeNum = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(offset));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(offset + 4));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(offset + 6));

      if (recLen == 0) break;
      if (offset + 8 + nameLen > dirData.Length) break;

      if (inodeNum != 0 && nameLen > 0) {
        var name = Encoding.UTF8.GetString(dirData, offset + 8, nameLen);
        if (name is not ("." or "..")) {
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

          long fileSize = 0;
          DateTime? lastMod = null;
          var isDir = false;

          var inodeData = this.ReadInode(inodeNum);
          if (inodeData != null) {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(inodeData);
            isDir = (mode & InodeModeDir) != 0;
            fileSize = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(4));
            var mtime = BinaryPrimitives.ReadUInt32LittleEndian(inodeData.AsSpan(16));
            if (mtime != 0) {
              try { lastMod = DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime; } catch { /* ignore */ }
            }
          }

          this._entries.Add(new Ext1Entry {
            Name = fullPath,
            Size = isDir ? 0 : fileSize,
            IsDirectory = isDir,
            LastModified = lastMod,
            Inode = inodeNum,
          });

          if (isDir && inodeData != null && seen.Add(inodeNum)) {
            var subDirData = this.ReadInodeBlocks(inodeData);
            this.ReadDirectoryEntries(subDirData, fullPath);
          }
        }
      }
      offset += recLen;
    }
  }

  /// <summary>
  /// Copies <paramref name="entry" />'s bytes into <paramref name="destination" />
  /// without buffering the whole file, which an entry approaching ext1's 4 GB
  /// i_size ceiling would not survive.
  /// </summary>
  public void ExtractTo(Ext1Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory || entry.Inode == 0) return;
    var inodeData = this.ReadInode(entry.Inode);
    if (inodeData == null) return;
    this.WriteInodeBlocks(inodeData, destination);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Ext1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Inode == 0) return [];

    var inodeData = this.ReadInode(entry.Inode);
    if (inodeData == null) return [];

    var data = this.ReadInodeBlocks(inodeData);
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}

/// <summary>
/// Single entry returned by <see cref="Ext1Reader"/>.
/// </summary>
public sealed class Ext1Entry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public required string Name { get; init; }
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }
  /// <summary>
  /// Gets or sets the inode.
  /// </summary>
public uint Inode { get; init; }
}
