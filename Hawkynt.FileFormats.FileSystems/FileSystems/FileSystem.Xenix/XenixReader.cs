#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Xenix;

/// <summary>
/// Reader for Microsoft / SCO Xenix System V file system (1980-1989, Microsoft's
/// licensed UNIX). Xenix is a System V variant with two superblock structures
/// ("Xenix-4 V" and "Xenix-5 V"); we target the more common Xenix V (3/V) layout.
///
/// On-disk layout (little-endian, 1024-byte blocks by default — adjustable via
/// s_type at sb+508):
///   Block 0      bootstrap
///   Block 1      superblock @ file offset 1024
///   Block 2..    inode table (64-byte inodes, 10 direct + 1+2+3 indirect ptrs
///                stored as 3-byte addresses)
///   data blocks  follow
///
/// Superblock field of interest:
///   u32 s_magic       (sb+504) 0xFD187E20  (same magic as s5fs — distinguished
///                                            from SysV/Coherent by extension)
///   u32 s_type        (sb+508) 1=512B/2=1024B/3=2048B blocks
///
/// Root inode = 2. Directory entry: u16 inode + 14-char name.
///
/// Spec source: SCO XENIX System V Programmer's Reference (1989) Appendix C;
/// Linux kernel fs/sysv/super.c which historically mounted Xenix volumes via
/// the sysv driver.
/// </summary>
public sealed class XenixReader : IDisposable {

  private readonly byte[] _data;
  private readonly List<XenixEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<XenixEntry> Entries => this._entries;
  /// <summary>
  /// Gets or sets the magic.
  /// </summary>
  public uint Magic { get; private set; }
  /// <summary>
  /// Gets or sets the block size.
  /// </summary>
  public int BlockSize { get; private set; } = 1024;

  internal const int SuperblockOffset = 1024;
  private const int InodeSize = 64;
  // Genuine Xenix superblock magic (s_magic @ struct offset 0x3F8 = 0x2B5544),
  // as written by mkfs.xenix and matched verbatim by the Linux sysv driver's
  // detect_xenix(); s_type at 0x3FC selects the block size.
  internal const uint MagicXenix = 0x002B5544;
  internal const int MagicOffset = 0x3F8;
  internal const int TypeOffset = 0x3FC;
  private const int RootInode = 2;

  /// <summary>
  /// Initializes a new instance of <see cref="XenixReader"/>.
  /// </summary>
  public XenixReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < SuperblockOffset + 1024)
      throw new InvalidDataException("Xenix: image too small for superblock.");
    var sb = this._data.AsSpan(SuperblockOffset);
    this.Magic = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(MagicOffset));
    if (this.Magic != MagicXenix)
      throw new InvalidDataException($"Xenix: invalid magic 0x{this.Magic:X8} (expected 0x{MagicXenix:X8}).");
    var typeCode = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(TypeOffset));
    this.BlockSize = typeCode switch {
      1 => 512,
      2 => 1024,
      3 => 2048,
      _ => 1024,
    };
    this.ReadDirectory(RootInode, "");
  }

  internal long BlockOffset(uint block) => (long)block * this.BlockSize;
  internal long InodeTableOffset() => 2L * this.BlockSize;

  private byte[]? ReadInode(uint inum) {
    if (inum == 0) return null;
    var offset = this.InodeTableOffset() + (long)(inum - 1) * InodeSize;
    if (offset < 0 || offset + InodeSize > this._data.Length) return null;
    return this._data.AsSpan((int)offset, InodeSize).ToArray();
  }

  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[0] | ((uint)s[1] << 8) | ((uint)s[2] << 16);

  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(8));
    var zones = new uint[13];
    for (var i = 0; i < 13; i++) zones[i] = Read24(inode.AsSpan(12 + i * 3));
    return (mode, size, zones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inum) {
    var inode = this.ReadInode(inum);
    if (inode == null) return [];
    var (_, size, zones) = ParseInode(inode);
    return this.ReadZones(zones, size);
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
    long remaining = size;
    for (var i = 0; i < 10 && remaining > 0; i++) {
      // A zero zone is a hole the width of one block, not the end of the file.
      if (zones[i] == 0) { AppendHole(ms, ref remaining, this.BlockSize); continue; }
      this.AppendBlock(ms, zones[i], ref remaining);
    }
    if (remaining > 0) {
      if (zones[10] != 0) this.ReadIndirect(ms, zones[10], ref remaining, 1);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 4) * this.BlockSize);
    }
    if (remaining > 0) {
      if (zones[11] != 0) this.ReadIndirect(ms, zones[11], ref remaining, 2);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 4) * this.BlockSize * (this.BlockSize / 4));
    }
    if (remaining > 0) {
      if (zones[12] != 0) this.ReadIndirect(ms, zones[12], ref remaining, 3);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 4) * this.BlockSize * (this.BlockSize / 4) * (this.BlockSize / 4));
    }
    return ms.ToArray();
  }

  private void AppendBlock(MemoryStream ms, uint block, ref long remaining) {
    var offset = this.BlockOffset(block);
    if (offset < 0 || offset + this.BlockSize > this._data.Length) return;
    var toRead = (int)Math.Min(remaining, this.BlockSize);
    ms.Write(this._data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectBlock, ref long remaining, int level) {
    if (indirectBlock == 0 || remaining <= 0) return;
    var offset = this.BlockOffset(indirectBlock);
    if (offset + this.BlockSize > this._data.Length) return;
    // Four bytes per pointer, not the three an inode packs them into. The
    // inode's thirteen block numbers are squeezed into thirty-nine bytes; the
    // blocks below it hold plain little-endian words, and this used to read them
    // three bytes at a time. Everything past the first pointer of an indirect
    // block came back as the wrong block, and every file large enough to need
    // one was wrong from its eleventh block on. The kernel driver reads a volume
    // written the other way and hands back every byte, which is what settled it.
    var ptrsPerBlock = this.BlockSize / 4;
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan((int)offset + i * 4));
      if (ptr == 0) {
        // Everything this pointer would have addressed is hole.
        var span = (long)this.BlockSize;
        for (var l = 1; l < level; ++l) span *= ptrsPerBlock;
        AppendHole(ms, ref remaining, span);
        continue;
      }
      if (level == 1) this.AppendBlock(ms, ptr, ref remaining);
      else this.ReadIndirect(ms, ptr, ref remaining, level - 1);
    }
  }

  private void ReadDirectory(uint inum, string path) {
    var dirData = this.ReadInodeData(inum);
    if (dirData.Length == 0) return;
    var seen = new HashSet<uint>();
    const int entrySize = 16;
    for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var name = ReadNullTermString(dirData, off + 2, 14);
      if (name is "." or "..") continue;
      this.ProcessDirEntry(ino, name, path, seen);
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = this.ReadInode(ino);
    if (inode == null) return;
    var (mode, size, _) = ParseInode(inode);
    var isDir = IsDirectory(mode);
    this._entries.Add(new XenixEntry {
      Name = fullPath,
      Size = isDir ? 0 : size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });
    if (isDir && seen.Add(ino)) this.ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

  /// <summary>
  /// Where on disk everything the volume holds actually sits: each file's data
  /// blocks under its name, and every directory block and indirect block as
  /// structure, with the byte offset of the pointer that names each run.
  /// </summary>
  /// <remarks>
  /// This filesystem tracks its free space with a chained cache in the
  /// superblock rather than a bitmap, so what is taken can only be answered by
  /// walking the inodes. Doing so also answers by whom, which is what anything
  /// moving a file needs.
  /// </remarks>
  public IEnumerable<(long Offset, long Length, long PointerOffset, string? Owner)> EnumerateLayout() {
    // The root's own blocks belong to nobody in the entry list — it is what the
    // list was walked from — so it is claimed first. Leaving it out reported
    // the root directory as space nothing holds, and a wipe then zeroed it.
    var inodes = new List<(int Number, bool IsDirectory, string Name)> { (RootInode, true, "") };
    foreach (var entry in this._entries)
      inodes.Add((entry.InodeNumber, entry.IsDirectory, entry.Name));

    foreach (var (inodeNumber, isDirectory, name) in inodes) {
      var inode = this.ReadInode((uint)inodeNumber);
      if (inode == null) continue;
      var (_, size, zones) = ParseInode(inode);
      if (size == 0) continue;

      var inodeOffset = this.InodeTableOffset() + (long)(inodeNumber - 1) * InodeSize;
      var owner = isDirectory ? null : name;
      var remaining = (long)size;

      var runFirstBlock = 0u;
      var runPointer = -1L;
      var runBlocks = 0;

      foreach (var (block, pointerOffset, isPointerBlock) in this.EnumerateBlockPointers(zones, inodeOffset)) {
        // An indirect block is the volume's own bookkeeping wherever it sits,
        // and it is never part of a file's run.
        if (isPointerBlock) {
          yield return (this.BlockOffset(block), this.BlockSize, -1, null);
          continue;
        }

        if (remaining <= 0) break;
        remaining -= this.BlockSize;

        // A hole owns nothing, so there is nothing to report and nothing to
        // move. It still ends whatever run was being gathered: the blocks on
        // either side of it are not adjacent in the file even where they are on
        // the disk.
        if (block == 0) {
          if (runPointer >= 0)
            yield return (this.BlockOffset(runFirstBlock), (long)runBlocks * this.BlockSize, runPointer, owner);
          runPointer = -1;
          continue;
        }

        // A run continues only while the blocks stay consecutive and so do the
        // pointers naming them: a move rewrites the pointers in order, so a gap
        // in either would put the wrong blocks under the wrong pointers.
        if (runPointer >= 0 && block == runFirstBlock + runBlocks
            && pointerOffset == runPointer + (long)runBlocks * 3) {
          ++runBlocks;
          continue;
        }

        if (runPointer >= 0)
          yield return (this.BlockOffset(runFirstBlock), (long)runBlocks * this.BlockSize, runPointer, owner);

        runFirstBlock = block;
        runPointer = pointerOffset;
        runBlocks = 1;
      }

      if (runPointer >= 0)
        yield return (this.BlockOffset(runFirstBlock), (long)runBlocks * this.BlockSize, runPointer, owner);
    }
  }

  /// <summary>
  /// The blocks a file's pointers name, in order, each with the byte offset of
  /// the pointer itself and whether the block is a pointer block rather than
  /// data.
  /// </summary>
  private IEnumerable<(uint Block, long PointerOffset, bool IsPointerBlock)> EnumerateBlockPointers(
      uint[] zones, long inodeOffset) {
    // Holes are reported too, as a block of zero. They own nothing, but they
    // hold their place in the file -- and this used to stop at the first one it
    // met, which left every block after a hole invisible to anything asking
    // where a file's bytes are. A defragmentation reads this to know what to
    // move.
    for (var i = 0; i < 10; ++i)
      yield return (zones[i], inodeOffset + 12 + (long)i * 3, false);

    for (var level = 1; level <= 3; ++level) {
      var root = zones[9 + level];
      if (root != 0) yield return (root, -1, true);
      foreach (var triple in this.EnumerateIndirectPointers(root, level))
        yield return triple;
    }
  }

  /// <summary>The blocks an indirect block names, descending as many levels as asked.</summary>
  private IEnumerable<(uint Block, long PointerOffset, bool IsPointerBlock)> EnumerateIndirectPointers(
      uint block, int levels) {
    // A pointer of zero is a hole as wide as everything it would have addressed
    // -- one block at the bottom level, and a whole subtree above it -- so an
    // absent block still accounts for the part of the file behind it.
    var reach = 1L;
    for (var i = 0; i < levels; ++i) reach *= this.BlockSize / 4;

    var blockOffset = block == 0 ? -1 : this.BlockOffset(block);
    if (blockOffset < 0 || blockOffset + this.BlockSize > this._data.Length) {
      for (var i = 0L; i < reach; ++i) yield return (0, -1, false);
      yield break;
    }

    for (var i = 0; i < this.BlockSize / 4; ++i) {
      var at = blockOffset + (long)i * 4;
      var pointer = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan((int)at, 4));
      if (levels <= 1) {
        yield return (pointer, at, false);
        continue;
      }
      if (pointer != 0) yield return (pointer, -1, true);
      foreach (var triple in this.EnumerateIndirectPointers(pointer, levels - 1))
        yield return triple;
    }
  }

  /// <summary>Bytes a pointer to a data block occupies inside an inode.</summary>
  public const int InodePointerBytes = 3;

  /// <summary>Bytes a pointer occupies inside an indirect block.</summary>
  public const int IndirectPointerBytes = 4;

  /// <summary>
  /// First byte a file may occupy. <c>s_isize</c> is the number of the first
  /// data block, not how many blocks the inode list spans — reading it the
  /// other way put the boundary past the root directory and the first file,
  /// and everything below it was then reported as the volume's own.
  /// </summary>
  public long FirstDataByte {
    get {
      var firstDataBlock = BinaryPrimitives.ReadUInt16LittleEndian(
        this._data.AsSpan(1024, 2));
      return (long)firstDataBlock * this.BlockSize;
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(XenixEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inode = this.ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];
    var (_, size, _) = ParseInode(inode);
    var data = this.ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size) return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
