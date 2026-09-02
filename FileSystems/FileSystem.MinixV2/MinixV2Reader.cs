#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV2;

/// <summary>
/// Reads Minix v2 filesystem images (1991). v2 extended the original
/// Minix layout to support large files: 64-byte inodes (replacing v1's
/// 32-byte), 32-bit zone numbers, and a triple-indirect block. The
/// superblock layout is the same as v1 (1024-byte blocks). Magic 0x2468
/// (14-byte names) or 0x2478 (30-byte names).
/// </summary>
public sealed class MinixV2Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<MinixV2Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<MinixV2Entry> Entries => _entries;

  /// <summary>
  /// Gets or sets the magic.
  /// </summary>
  public ushort Magic { get; private set; }

  /// <summary>Blocks the inode bitmap occupies, from the superblock.</summary>
  public ushort InodeBitmapBlocks => this._imapBlocks;

  /// <summary>Blocks the zone bitmap occupies, from the superblock.</summary>
  public ushort ZoneBitmapBlocks => this._zmapBlocks;

  /// <summary>Bytes per block, which is also a zone at this zone size.</summary>
  public static int ZoneSize => BlockSize;

  /// <summary>Byte offset of the zone bitmap.</summary>
  public long ZoneBitmapOffset => 2L * BlockSize + (long)this._imapBlocks * BlockSize;

  /// <summary>First zone that may hold file data: the one past the inode table.</summary>
  public long FirstDataZoneOffset {
    get {
      var inodeCount = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset, 4));
      var tableBytes = (long)inodeCount * InodeSize;
      var end = this.InodeTableOffset() + tableBytes;
      return (end + BlockSize - 1) / BlockSize * BlockSize;
    }
  }
  /// <summary>
  /// Gets or sets the name length.
  /// </summary>
  public int NameLength { get; private set; }

  private ushort _imapBlocks;
  private ushort _zmapBlocks;
  private const int BlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int InodeSize = 64;

  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;

  /// <summary>
  /// Initializes a new instance of <see cref="MinixV2Reader"/>.
  /// </summary>
  public MinixV2Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 32)
      throw new InvalidDataException("MinixV2: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16));
    if (magic != MagicV2_14 && magic != MagicV2_30)
      throw new InvalidDataException($"MinixV2: invalid magic 0x{magic:X4} at superblock+16 (expected 0x2468 or 0x2478).");

    this.Magic = magic;
    this.NameLength = magic == MagicV2_30 ? 30 : 14;

    _imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4));
    _zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));

    ReadDirectory(1, "");
  }

  private long ZoneOffset(uint zone) => (long)zone * BlockSize;

  private long InodeTableOffset() =>
    2L * BlockSize + (long)_imapBlocks * BlockSize + (long)_zmapBlocks * BlockSize;

  private byte[]? ReadInode(uint inodeNum) {
    if (inodeNum == 0) return null;
    var offset = InodeTableOffset() + (long)(inodeNum - 1) * InodeSize;
    if (offset + InodeSize > _data.Length) return null;
    return _data.AsSpan((int)offset, InodeSize).ToArray();
  }

  // V2 inode (64 bytes):
  //   u16 mode      (0)
  //   u16 nlinks    (2)
  //   u16 uid       (4)
  //   u16 gid       (6)
  //   u32 size      (8)
  //   u32 atime     (12)
  //   u32 mtime     (16)
  //   u32 ctime     (20)
  //   u32[10] zones (24..63) — 7 direct + 1 ind + 1 dind + 1 tind
  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[10];
    for (var i = 0; i < 10; i++)
      zones[i] = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(24 + i * 4));
    return (mode, size, zones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inodeNum) {
    var inode = ReadInode(inodeNum);
    if (inode == null) return [];
    var (_, size, zones) = ParseInode(inode);
    return ReadZones(zones, size);
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

  private byte[] ReadZones(uint[] zones, uint size) {
    if (size == 0) return [];
    using var ms = new MemoryStream();
    var remaining = (long)size;

    for (var i = 0; i < 7 && remaining > 0; i++) {
      // A zero zone is a hole the width of one block, not the end of the file.
      if (zones[i] == 0) { AppendHole(ms, ref remaining, BlockSize); continue; }
      AppendZone(ms, zones[i], ref remaining);
    }

    if (remaining > 0) {
      if (zones[7] != 0) ReadIndirect(ms, zones[7], ref remaining, 1);
      else AppendHole(ms, ref remaining, (long)(BlockSize / 4) * BlockSize);
    }
    if (remaining > 0) {
      if (zones[8] != 0) ReadIndirect(ms, zones[8], ref remaining, 2);
      else AppendHole(ms, ref remaining, (long)(BlockSize / 4) * BlockSize * (BlockSize / 4));
    }
    if (remaining > 0) {
      if (zones[9] != 0) ReadIndirect(ms, zones[9], ref remaining, 3);
      else AppendHole(ms, ref remaining, (long)(BlockSize / 4) * BlockSize * (BlockSize / 4) * (BlockSize / 4));
    }

    return ms.ToArray();
  }

  /// <summary>
  /// Where on disk <paramref name="entry" />'s bytes actually sit, as runs of
  /// whole zones, along with the byte offset of the first pointer that names
  /// each run.
  /// </summary>
  /// <remarks>
  /// The zone bitmap says which zones are taken and nothing about by whom.
  /// Reporting a layout without that leaves anything trying to move a file
  /// with nothing to repoint, so the runs are read from the inode's zone
  /// pointers — and from the indirect blocks those point at, whose entries are
  /// pointers in their own right.
  /// </remarks>
  public IEnumerable<(long Offset, long Length, long PointerOffset)> EnumerateDataExtents(MinixV2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) yield break;

    var inode = this.ReadInode((uint)entry.InodeNumber);
    if (inode == null) yield break;
    var (_, size, zones) = ParseInode(inode);
    if (size == 0) yield break;

    var inodeOffset = this.InodeTableOffset() + (long)(entry.InodeNumber - 1) * InodeSize;
    var remaining = (long)size;

    var runFirstZone = 0u;
    var runPointer = -1L;
    var runZones = 0;

    foreach (var (zone, pointerOffset) in this.EnumerateZonePointers(zones, inodeOffset)) {
      if (remaining <= 0) break;
      remaining -= BlockSize;

      // A hole owns nothing, so there is nothing to report and nothing to move.
      // It still ends whatever run was being gathered: the zones on either side
      // of it are not adjacent in the file even where they are on the disk.
      if (zone == 0) {
        if (runPointer >= 0)
          yield return ((long)runFirstZone * BlockSize, (long)runZones * BlockSize, runPointer);
        runPointer = -1;
        continue;
      }

      // A run continues only while the zones stay consecutive and so do the
      // pointers naming them: a move rewrites the pointers in order, so a gap
      // in either would put the wrong zones under the wrong pointers.
      if (runPointer >= 0 && zone == runFirstZone + runZones
          && pointerOffset == runPointer + (long)runZones * 4) {
        ++runZones;
        continue;
      }

      if (runPointer >= 0)
        yield return ((long)runFirstZone * BlockSize, (long)runZones * BlockSize, runPointer);

      runFirstZone = zone;
      runPointer = pointerOffset;
      runZones = 1;
    }

    if (runPointer >= 0)
      yield return ((long)runFirstZone * BlockSize, (long)runZones * BlockSize, runPointer);
  }

  /// <summary>
  /// The data zones a file's pointers name, in order, each with the byte offset
  /// of the pointer itself — in the inode for the direct ones, in an indirect
  /// block for the rest.
  /// </summary>
  /// <remarks>
  /// Holes are reported too, as a zone of zero. They own nothing, but they hold
  /// their place in the file — and this used to stop at the first one it met,
  /// which left every zone after a hole invisible to anything asking where a
  /// file's bytes are. A defragmentation reads this to know what to move.
  /// </remarks>
  private IEnumerable<(uint Zone, long PointerOffset)> EnumerateZonePointers(uint[] zones, long inodeOffset) {
    for (var i = 0; i < 7; ++i)
      yield return (zones[i], inodeOffset + 24 + (long)i * 4);

    for (var level = 1; level <= 3; ++level)
      foreach (var pair in this.EnumerateIndirectPointers(zones[6 + level], level))
        yield return pair;
  }

  /// <summary>The zones an indirect block names, descending as many levels as asked.</summary>
  /// <remarks>
  /// A pointer of zero is a hole as wide as everything it would have addressed —
  /// one zone at the bottom level, and a whole subtree above it — so an absent
  /// block still accounts for the part of the file that sits behind it.
  /// </remarks>
  private IEnumerable<(uint Zone, long PointerOffset)> EnumerateIndirectPointers(uint zone, int levels) {
    var reach = 1L;
    for (var i = 0; i < levels; ++i) reach *= BlockSize / 4;

    var blockOffset = zone == 0 ? -1 : this.ZoneOffset(zone);
    if (blockOffset < 0 || blockOffset + BlockSize > _data.Length) {
      for (var i = 0L; i < reach; ++i) yield return (0, -1);
      yield break;
    }

    for (var i = 0; i < BlockSize / 4; ++i) {
      var at = blockOffset + (long)i * 4;
      var pointer = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)at, 4));
      if (levels <= 1) {
        yield return (pointer, at);
        continue;
      }
      foreach (var pair in this.EnumerateIndirectPointers(pointer, levels - 1))
        yield return pair;
    }
  }

  private void AppendZone(MemoryStream ms, uint zone, ref long remaining) {
    var offset = ZoneOffset(zone);
    if (offset + BlockSize > _data.Length) return;
    var toRead = (int)Math.Min(remaining, BlockSize);
    ms.Write(_data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectZone, ref long remaining, int level) {
    if (indirectZone == 0 || remaining <= 0) return;
    var offset = ZoneOffset(indirectZone);
    if (offset + BlockSize > _data.Length) return;

    var ptrsPerBlock = BlockSize / 4; // 32-bit zone pointers in V2
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)offset + i * 4));
      if (ptr == 0) {
        // Everything this pointer would have addressed is hole.
        var span = (long)BlockSize;
        for (var l = 1; l < level; ++l) span *= ptrsPerBlock;
        AppendHole(ms, ref remaining, span);
        continue;
      }
      if (level == 1) AppendZone(ms, ptr, ref remaining);
      else ReadIndirect(ms, ptr, ref remaining, level - 1);
    }
  }

  private void ReadDirectory(uint inodeNum, string path) {
    var dirData = ReadInodeData(inodeNum);
    if (dirData.Length == 0) return;

    var seen = new HashSet<uint>();
    var entrySize = 2 + this.NameLength;
    for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var name = ReadNullTermString(dirData, off + 2, this.NameLength);
      if (name is "." or "..") continue;
      ProcessDirEntry(ino, name, path, seen);
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = ReadInode(ino);
    if (inode == null) return;

    var (mode, size, _) = ParseInode(inode);
    var isDir = IsDirectory(mode);
    _entries.Add(new MinixV2Entry {
      Name = fullPath,
      Size = isDir ? 0 : (int)size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });

    if (isDir && seen.Add(ino))
      ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(MinixV2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var inode = ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];

    var (_, size, _) = ParseInode(inode);
    var data = ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size)
      return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
