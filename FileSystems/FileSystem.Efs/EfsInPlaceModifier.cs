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
  private const int InodeTableStart = 1;
  private const ushort DirBlockMagic = 0xBEEF;
  private const ushort ModeDir = 0x4000 | 0x1ED;
  private const ushort ModeFile = 0x8000 | 0x1A4;
  private const int RootInode = 2;
  private const int MaxExtentBlocks = 255; // ex_length is a single byte

  private sealed record class Geometry(int FirstCg, int TotalBlocks);

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
    var sb = new byte[32];
    image.Position = 0;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(0x18));
    if (magic != EfsSuperblock.EfsMagic)
      throw new InvalidDataException("EFS: superblock magic mismatch.");
    var totalBlocks = BinaryPrimitives.ReadInt32BigEndian(sb.AsSpan(0x00));
    var firstCg = BinaryPrimitives.ReadInt32BigEndian(sb.AsSpan(0x04));
    return new Geometry(firstCg, totalBlocks);
  }

  // ── Inode helpers ─────────────────────────────────────────────────────────

  private static long InodeOffset(int inode) {
    var blockOff = (inode - 2) / InodesPerBlock;
    var slotOff = (inode - 2) % InodesPerBlock;
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

  // A dinode slot is free when its di_mode (offset 0) is zero. The inode array
  // spans sector 1 .. (firstCg - 1).
  private static int FindFreeInode(Stream image, Geometry geom) {
    var inodeBlocks = Math.Max(1, geom.FirstCg - InodeTableStart);
    var capacity = inodeBlocks * InodesPerBlock;
    for (var idx = 0; idx < capacity; idx++) {
      var inode = idx + 2;
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
    BinaryPrimitives.WriteInt16BigEndian(ip.AsSpan(28), (short)(blocks > 0 ? 1 : 0)); // di_numextents
    ip[30] = 1; // di_version
    if (blocks > 0) {
      ip[32] = 0; // ex_magic
      ip[33] = (byte)(firstBlock >> 16);
      ip[34] = (byte)(firstBlock >> 8);
      ip[35] = (byte)firstBlock;
      ip[36] = (byte)blocks; // ex_length
    }
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

    // Update superblock s_size and s_cgfsize (both grow by the appended blocks).
    var hdr = new byte[12];
    image.Position = 0;
    image.ReadExactly(hdr);
    BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(0), newTotal);                 // s_size
    var cgFreeSize = newTotal - geom.FirstCg;
    BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(8), cgFreeSize);               // s_cgfsize
    image.Position = 0;
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
    int slots = blk[2];
    var nb = Encoding.UTF8.GetBytes(name);
    if (nb.Length > 255) return false;

    var cur = 3;
    for (var i = 0; i < slots && cur + 3 <= blk.Length; i++) {
      int nlen = blk[cur + 2];
      if (cur + 3 + nlen > blk.Length) return false;
      cur += 3 + nlen;
    }
    var slotLen = 3 + nb.Length;
    if (cur + slotLen > blk.Length) return false; // single-block dir full

    BinaryPrimitives.WriteUInt16BigEndian(blk.AsSpan(cur), inode);
    blk[cur + 2] = (byte)nb.Length;
    nb.CopyTo(blk, cur + 3);
    blk[2] = (byte)(slots + 1);

    image.Position = (long)dirBlock * BasicBlock;
    image.Write(blk, 0, blk.Length);

    // Grow the root directory inode's di_size to cover the new dirent.
    var newDirSize = cur + slotLen;
    image.Position = InodeOffset(RootInode) + 8;
    var sz = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sz, newDirSize);
    image.Write(sz, 0, 4);
    return true;
  }

  private static bool FindRootDirEntry(Stream image, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, out _);
    int slots = blk[2];
    var cur = 3;
    for (var i = 0; i < slots && cur + 3 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt16BigEndian(blk.AsSpan(cur));
      int nlen = blk[cur + 2];
      if (cur + 3 + nlen > blk.Length) break;
      var entryName = Encoding.UTF8.GetString(blk, cur + 3, nlen);
      if (entryName is not ("." or "..") && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        inode = childInode;
        return true;
      }
      cur += 3 + nlen;
    }
    return false;
  }

  // Removes a dirent by rewriting the dir block without it (entries packed).
  private static bool RemoveRootDirEntry(Stream image, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, out var dirBlock);
    int slots = blk[2];

    var rebuilt = new byte[BasicBlock];
    BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(0), DirBlockMagic);
    var outOff = 3;
    var kept = 0;
    var found = false;
    var cur = 3;
    for (var i = 0; i < slots && cur + 3 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt16BigEndian(blk.AsSpan(cur));
      int nlen = blk[cur + 2];
      if (cur + 3 + nlen > blk.Length) break;
      var entryName = Encoding.UTF8.GetString(blk, cur + 3, nlen);
      var slotLen = 3 + nlen;
      if (!found && entryName is not ("." or "..") && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        inode = childInode;
        found = true;
      } else {
        Array.Copy(blk, cur, rebuilt, outOff, slotLen);
        outOff += slotLen;
        kept++;
      }
      cur += slotLen;
    }
    if (!found) return false;

    rebuilt[2] = (byte)kept;
    image.Position = (long)dirBlock * BasicBlock;
    image.Write(rebuilt, 0, rebuilt.Length);

    image.Position = InodeOffset(RootInode) + 8;
    var sz = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(sz, outOff);
    image.Write(sz, 0, 4);
    return true;
  }
}
