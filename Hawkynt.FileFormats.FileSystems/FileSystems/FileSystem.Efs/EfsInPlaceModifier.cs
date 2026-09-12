#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Efs;

/// <summary>
/// TRUE in-place R/W modifier for the SGI EFS images this project emits.
/// Performs O(touched bytes) random-access mutation: only the affected dinode
/// slot, the root directory block, the file's single data extent and (when the
/// image must grow) the superblock <c>s_size</c>/<c>s_cgfsize</c> fields are
/// read or written. Every other byte stays byte-identical.
///
/// <para>The on-disk model is the one written by <see cref="EfsWriter"/>:
/// sector 0 superblock (big-endian), sector 1.. the 128-byte dinode array
/// (inode <c>N</c> at index <c>N - 2</c>, 4 per 512-byte basic block), then
/// per-file single contiguous extents. There is no free-block bitmap, so fresh
/// extents are appended at the current end of the image — a genuine in-place
/// grow, not a re-pack; removed extents become unreferenced holes a later
/// defrag reclaims.</para>
///
/// <para>Honest scope: files are stored as one contiguous extent in the root
/// directory. Nested-path adds, inode-table exhaustion, or extents longer than
/// 255 basic blocks (the single-extent length ceiling) throw
/// <see cref="IOException"/> so the descriptor can fall back to a full
/// rebuild.</para>
/// </summary>
public static class EfsInPlaceModifier {

  private const int BasicBlock = 512;
  private const int InodeSize = 128;
  private const int InodesPerBlock = BasicBlock / InodeSize; // 4
  /// <summary>
  /// The cylinder group starts at block 2: block 0 is the SGI volume header
  /// and block 1 the superblock.
  /// </summary>
  private const int InodeTableStart = 2;
  private const ushort DirBlockMagic = 0xBEEF;
  private const ushort ModeDir = 0x4000 | 0x1ED;
  private const ushort ModeFile = 0x8000 | 0x1A4;
  private const int RootInode = 2;

  /// <summary>Extents an inode holds before it needs an indirect one.</summary>
  private const int DirectExtents = 12;

  private const int MaxExtentBlocks = 255; // ex_length is a single byte

  private sealed record class Geometry(int FirstCg, int TotalBlocks, int InodeBlocks);

  /// <summary>Adds a regular file to the root directory in-place.</summary>
  /// <exception cref="IOException">Nested path, no free inode slot, no free
  /// root-directory slot, or payload beyond the single-extent ceiling.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Contains('/'))
      throw new IOException("EFS: nested-path add is deferred to rebuild.");

    var geom = ReadGeometry(image);
    var blocks = data.Length == 0 ? 0 : (data.Length + BasicBlock - 1) / BasicBlock;
    if (blocks > MaxExtentBlocks)
      throw new IOException($"EFS: file '{name}' needs {blocks} basic blocks; single-extent ceiling is {MaxExtentBlocks}.");

    var newInode = FindFreeInode(image, geom);
    if (newInode < 0) throw new IOException("EFS: no free inode slot.");

    var firstBlock = blocks == 0 ? 0 : AppendExtent(image, ref geom, blocks);
    if (blocks > 0) WriteExtentData(image, firstBlock, blocks, data);

    WriteFileInode(image, newInode, firstBlock, blocks, data.Length);

    if (!InsertRootDirEntry(image, (ushort)newInode, norm))
      throw new IOException("EFS: root directory block is full; no free slot for new entry.");
  }

  /// <summary>Removes a named regular file from the root directory in-place.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Contains('/')) return false;

    if (!RemoveRootDirEntry(image, norm, out var targetInode)) return false;

    var (isDir, _, firstBlock, blocks) = ReadInode(image, (int)targetInode);
    if (isDir) return false;

    if (wipeData && firstBlock != 0 && blocks > 0)
      WipeBlocks(image, firstBlock, blocks);

    ZeroInode(image, (int)targetInode);
    return true;
  }

  /// <summary>Replaces a named regular file's data in-place.</summary>
  public static bool Replace(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Contains('/')) return false;

    var geom = ReadGeometry(image);
    if (!FindRootDirEntry(image, norm, out var targetInode)) return false;

    var (isDir, _, oldFirst, oldBlocks) = ReadInode(image, (int)targetInode);
    if (isDir) return false;

    var needed = data.Length == 0 ? 0 : (data.Length + BasicBlock - 1) / BasicBlock;
    if (needed > MaxExtentBlocks) return false;

    int firstBlock;
    if (needed <= oldBlocks) {
      firstBlock = needed == 0 ? 0 : oldFirst;
      if (oldFirst != 0 && oldBlocks > 0) WipeBlocks(image, oldFirst, oldBlocks);
      if (needed > 0) WriteExtentData(image, oldFirst, needed, data);
    } else {
      if (oldFirst != 0 && oldBlocks > 0) WipeBlocks(image, oldFirst, oldBlocks);
      firstBlock = AppendExtent(image, ref geom, needed);
      WriteExtentData(image, firstBlock, needed, data);
    }

    WriteFileInode(image, (int)targetInode, firstBlock, needed, data.Length);
    return true;
  }

  // ── Geometry ────────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    // The superblock is at block 1, and its magic at 0x1C — block 0 is the
    // SGI volume header.
    var sb = new byte[64];
    image.Position = (long)EfsWriter.SuperblockBlock * BasicBlock;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(0x1C));
    if (magic != EfsSuperblock.EfsMagic)
      throw new InvalidDataException("EFS: superblock magic mismatch.");
    var totalBlocks = BinaryPrimitives.ReadInt32BigEndian(sb.AsSpan(0x00));
    var firstCg = BinaryPrimitives.ReadInt32BigEndian(sb.AsSpan(0x04));
    var inodeBlocks = BinaryPrimitives.ReadInt16BigEndian(sb.AsSpan(0x0C));
    return new Geometry(firstCg, totalBlocks, inodeBlocks);
  }

  // ── Inode helpers ─────────────────────────────────────────────────────────

  private static long InodeOffset(int inode) {
    // Inode n sits at block n/4 of the table; 0 and 1 are reserved slots.
    var blockOff = inode / InodesPerBlock;
    var slotOff = inode % InodesPerBlock;
    return (long)(InodeTableStart + blockOff) * BasicBlock + (long)slotOff * InodeSize;
  }

  private static (bool IsDir, int Size, int FirstBlock, int Blocks) ReadInode(Stream image, int inode) {
    var ip = new byte[InodeSize];
    image.Position = InodeOffset(inode);
    image.ReadExactly(ip);
    var mode = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(0));
    var size = BinaryPrimitives.ReadInt32BigEndian(ip.AsSpan(8));
    var numExtents = BinaryPrimitives.ReadInt16BigEndian(ip.AsSpan(28));
    var firstBlock = 0;
    var blocks = 0;
    if (numExtents > 0) {
      firstBlock = (ip[33] << 16) | (ip[34] << 8) | ip[35];
      blocks = ip[36];
    }
    return ((mode & 0xF000) == 0x4000, size, firstBlock, blocks);
  }

  // A dinode slot is free when its di_mode (offset 0) is zero. The table runs
  // from the head of the cylinder group for as many blocks as the superblock
  // records, and inode n lives at block n/4 of it — so the last usable number
  // is the capacity itself, not the capacity plus two.
  private static int FindFreeInode(Stream image, Geometry geom) {
    var inodeBlocks = Math.Max(1, (int)geom.InodeBlocks);
    var capacity = inodeBlocks * InodesPerBlock;
    for (var inode = 2; inode < capacity; inode++) {
      image.Position = InodeOffset(inode);
      var mode = new byte[2];
      image.ReadExactly(mode);
      if (BinaryPrimitives.ReadUInt16BigEndian(mode) == 0) return inode;
    }
    return -1;
  }

  private static void WriteFileInode(Stream image, int inode, int firstBlock, int blocks, int size) {
    var ip = new byte[InodeSize];
    BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(0), ModeFile);     // di_mode
    BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), 1);            // di_nlink
    BinaryPrimitives.WriteInt32BigEndian(ip.AsSpan(8), size);          // di_size
    var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteInt32BigEndian(ip.AsSpan(12), now);
    BinaryPrimitives.WriteInt32BigEndian(ip.AsSpan(16), now);
    BinaryPrimitives.WriteInt32BigEndian(ip.AsSpan(20), now);
    ip[30] = 1; // di_version

    // An extent's length is one byte, so a run longer than 255 blocks has to
    // be split across several. Twelve fit in an inode. Writing one extent and
    // truncating its length gave a file of the right size holding the wrong
    // bytes past the first 255 blocks.

    var extents = 0;
    var remaining = blocks;
    var block = firstBlock;
    var fileOffset = 0;
    while (remaining > 0 && extents < DirectExtents) {
      var run = Math.Min(remaining, MaxExtentBlocks);
      var at = 32 + extents * 8;
      ip[at] = 0;                                  // ex_magic
      ip[at + 1] = (byte)(block >> 16);
      ip[at + 2] = (byte)(block >> 8);
      ip[at + 3] = (byte)block;
      ip[at + 4] = (byte)run;                      // ex_length
      ip[at + 5] = (byte)(fileOffset >> 16);
      ip[at + 6] = (byte)(fileOffset >> 8);
      ip[at + 7] = (byte)fileOffset;               // ex_offset, in blocks
      block += run;
      fileOffset += run;
      remaining -= run;
      ++extents;
    }

    if (remaining > 0)
      throw new NotSupportedException(
        $"EFS: a file of {blocks} blocks needs more than the {DirectExtents} extents an inode holds.");

    BinaryPrimitives.WriteInt16BigEndian(ip.AsSpan(28), (short)extents); // di_numextents
    image.Position = InodeOffset(inode);
    image.Write(ip, 0, InodeSize);
  }

  private static void ZeroInode(Stream image, int inode) {
    image.Position = InodeOffset(inode);
    image.Write(new byte[InodeSize], 0, InodeSize);
  }

  // ── Extent allocation (append at EOF) ─────────────────────────────────────

  private static int AppendExtent(Stream image, ref Geometry geom, int blocks) {
    var firstBlock = geom.TotalBlocks;
    var newTotal = geom.TotalBlocks + blocks;
    var newLength = (long)newTotal * BasicBlock;
    if (image.Length < newLength) image.SetLength(newLength);
    geom = geom with { TotalBlocks = newTotal };

    // Update fs_size and fs_cgfsize — in the superblock, which is at block 1.
    // Writing them at offset 0 put them in the volume header and left the
    // superblock saying the volume was smaller than it is.
    var superblockAt = (long)EfsWriter.SuperblockBlock * BasicBlock;
    var hdr = new byte[12];
    image.Position = superblockAt;
    image.ReadExactly(hdr);
    BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(0), newTotal);                 // fs_size
    var cgFreeSize = newTotal - geom.FirstCg;
    BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), cgFreeSize);               // fs_cgfsize
    image.Position = superblockAt;
    image.Write(hdr, 0, 12);
    return firstBlock;
  }

  private static void WriteExtentData(Stream image, int firstBlock, int blocks, byte[] data) {
    var extent = new byte[(long)blocks * BasicBlock];
    Array.Copy(data, extent, Math.Min(data.Length, extent.Length));
    image.Position = (long)firstBlock * BasicBlock;
    image.Write(extent, 0, extent.Length);
  }

  private static void WipeBlocks(Stream image, int firstBlock, int blocks) {
    image.Position = (long)firstBlock * BasicBlock;
    image.Write(new byte[(long)blocks * BasicBlock], 0, blocks * BasicBlock);
  }


  // ── Directory block helpers ─────────────────────────────────────────────
  //
  // A block holds a slot table — one byte per entry giving its offset halved —
  // and the entries themselves packed against the far end. This used to be
  // read and written as a flat list, which nothing but this project could
  // follow.

  private static List<(uint Inode, string Name)> ReadEntries(byte[] blk) {
    var entries = new List<(uint, string)>();
    int slots = blk[3];
    for (var i = 0; i < slots; i++) {
      var slotAt = 4 + i;
      if (slotAt >= blk.Length) break;
      var at = blk[slotAt] << 1;
      if (at + 5 > blk.Length) break;
      int nlen = blk[at + 4];
      if (at + 5 + nlen > blk.Length) break;
      entries.Add((BinaryPrimitives.ReadUInt32BigEndian(blk.AsSpan(at)),
        Encoding.ASCII.GetString(blk, at + 5, nlen)));
    }

    return entries;
  }

  private static byte[]? BuildBlock(List<(uint Inode, string Name)> entries) {
    var blk = new byte[BasicBlock];
    BinaryPrimitives.WriteUInt16BigEndian(blk, DirBlockMagic);

    var cursor = blk.Length;
    var slotAt = 4;
    foreach (var (inode, name) in entries) {
      var nb = Encoding.ASCII.GetBytes(name);
      if (nb.Length > 255) return null;

      var size = (5 + nb.Length + 1) & ~1;
      cursor -= size;
      if (cursor < slotAt + 1) return null;

      BinaryPrimitives.WriteUInt32BigEndian(blk.AsSpan(cursor), inode);
      blk[cursor + 4] = (byte)nb.Length;
      nb.CopyTo(blk, cursor + 5);
      blk[slotAt++] = (byte)(cursor >> 1);
    }

    blk[2] = (byte)(cursor >> 1);
    blk[3] = (byte)entries.Count;
    return blk;
  }

  // ── Root directory helpers ──────────────────────────────────────────────────

  private static byte[] ReadRootDirBlock(Stream image, out int dirBlock) {
    var (_, _, firstBlock, _) = ReadInode(image, RootInode);
    dirBlock = firstBlock;
    if (firstBlock == 0) throw new IOException("EFS: root directory has no data block.");
    var blk = new byte[BasicBlock];
    image.Position = (long)firstBlock * BasicBlock;
    image.ReadExactly(blk);
    if (BinaryPrimitives.ReadUInt16BigEndian(blk) != DirBlockMagic)
      throw new InvalidDataException("EFS: root directory block magic mismatch.");
    return blk;
  }

  private static bool InsertRootDirEntry(Stream image, ushort inode, string name) {
    var blk = ReadRootDirBlock(image, out var dirBlock);
    var entries = ReadEntries(blk);
    entries.Add((inode, name));

    var rebuilt = BuildBlock(entries);
    if (rebuilt == null) return false; // single-block directory full

    image.Position = (long)dirBlock * BasicBlock;
    image.Write(rebuilt, 0, rebuilt.Length);

    // A directory's size is whole blocks; a driver refuses anything else.
    var newDirSize = BasicBlock;
    image.Position = InodeOffset(RootInode) + 8;
    var sz = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sz, newDirSize);
    image.Write(sz, 0, 4);
    return true;
  }

  private static bool FindRootDirEntry(Stream image, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, out _);
    foreach (var (childInode, entryName) in ReadEntries(blk))
      if (entryName is not ("." or "..") && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        inode = childInode;
        return true;
      }

    return false;
  }

  // Removes a dirent by rewriting the dir block without it (entries packed).
  private static bool RemoveRootDirEntry(Stream image, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, out var dirBlock);
    var kept = new List<(uint Inode, string Name)>();
    var found = false;
    foreach (var entry in ReadEntries(blk)) {
      if (!found && entry.Name is not ("." or "..")
          && entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        inode = entry.Inode;
        found = true;
        continue;
      }

      kept.Add(entry);
    }

    if (!found) return false;

    var rebuilt = BuildBlock(kept);
    if (rebuilt == null) return false;

    image.Position = (long)dirBlock * BasicBlock;
    image.Write(rebuilt, 0, rebuilt.Length);

    image.Position = InodeOffset(RootInode) + 8;
    var sz = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sz, BasicBlock);
    image.Write(sz, 0, 4);
    return true;
  }
}
