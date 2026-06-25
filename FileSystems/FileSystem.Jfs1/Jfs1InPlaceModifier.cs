#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs1;

/// <summary>
/// TRUE in-place R/W modifier for the OS/2 JFS1 images this project emits.
/// Performs O(touched bytes) random-access mutation: only the affected dinode
/// slot, the root directory block, the file's single data extent and (when the
/// image must grow) the superblock <c>s_size</c> field are read or written.
/// Every other byte stays byte-identical.
///
/// <para>The on-disk model is the one written by <see cref="Jfs1Writer"/>:
/// block 0 superblock, blocks 1..inodeBlocks the 256-byte dinode array (inode
/// <c>N</c> at array index <c>N - 2</c>), then per-file single contiguous data
/// extents. There is no free-block bitmap, so fresh extents are appended at the
/// current end of the image — a genuine in-place grow, not a re-pack; removed
/// extents simply become unreferenced holes a later defrag reclaims.</para>
///
/// <para>Honest scope: files are stored as one contiguous extent in the root
/// directory. Nested-path adds and inode-table exhaustion throw
/// <see cref="IOException"/> so the descriptor can fall back to a full
/// rebuild.</para>
/// </summary>
public static class Jfs1InPlaceModifier {

  private const int InodeSize = 256;
  private const ushort DirBlockMagic = 0xD1F1;
  private const uint ModeDir = 0x4000u | 0x1EDu;
  private const uint ModeFile = 0x8000u | 0x1A4u;
  private const int RootInode = 2;

  private sealed record class Geometry(int BlockSize, int InodesPerBlock, int InodeStartBlock, ulong TotalBlocks);

  /// <summary>Adds a regular file to the root directory in-place.</summary>
  /// <exception cref="IOException">Nested path, no free inode slot, or no free
  /// root-directory slot.</exception>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Contains('/'))
      throw new IOException("JFS1: nested-path add is deferred to rebuild.");

    var geom = ReadGeometry(image);

    var newInode = FindFreeInode(image, geom);
    if (newInode < 0) throw new IOException("JFS1: no free inode slot.");

    var blocks = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;
    var firstBlock = blocks == 0 ? 0 : AppendExtent(image, ref geom, blocks);
    if (blocks > 0) WriteExtentData(image, geom, firstBlock, blocks, data);

    WriteFileInode(image, geom, newInode, (uint)firstBlock, (uint)blocks, (ulong)data.Length);

    if (!InsertRootDirEntry(image, geom, (uint)newInode, norm))
      throw new IOException("JFS1: root directory block is full; no free slot for new entry.");
  }

  /// <summary>Removes a named regular file from the root directory in-place.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var norm = name.Replace('\\', '/').Trim('/');
    if (norm.Contains('/')) return false;

    var geom = ReadGeometry(image);
    if (!RemoveRootDirEntry(image, geom, norm, out var targetInode)) return false;

    var (isDir, _, firstBlock, blocks) = ReadInode(image, geom, (int)targetInode);
    if (isDir) return false;

    if (wipeData && firstBlock != 0 && blocks > 0)
      WipeBlocks(image, geom, firstBlock, blocks);

    ZeroInode(image, geom, (int)targetInode);
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
    if (!FindRootDirEntry(image, geom, norm, out var targetInode)) return false;

    var (isDir, _, oldFirst, oldBlocks) = ReadInode(image, geom, (int)targetInode);
    if (isDir) return false;

    var needed = data.Length == 0 ? 0 : (data.Length + geom.BlockSize - 1) / geom.BlockSize;

    int firstBlock;
    if (needed <= oldBlocks) {
      // Fits inside the existing extent — overwrite in place (zones unchanged).
      firstBlock = needed == 0 ? 0 : oldFirst;
      if (oldFirst != 0 && oldBlocks > 0) WipeBlocks(image, geom, oldFirst, oldBlocks); // clear stale tail
      if (needed > 0) WriteExtentData(image, geom, oldFirst, needed, data);
    } else {
      // Grow: append a fresh contiguous extent, abandon the old one.
      if (oldFirst != 0 && oldBlocks > 0) WipeBlocks(image, geom, oldFirst, oldBlocks);
      firstBlock = AppendExtent(image, ref geom, needed);
      WriteExtentData(image, geom, firstBlock, needed, data);
    }

    WriteFileInode(image, geom, (int)targetInode, (uint)firstBlock, (uint)needed, (ulong)data.Length);
    return true;
  }

  // ── Geometry ────────────────────────────────────────────────────────────

  private static Geometry ReadGeometry(Stream image) {
    var sb = new byte[64];
    image.Position = 0;
    image.ReadExactly(sb);
    if (!sb.AsSpan(0, 4).SequenceEqual(Jfs1Superblock.Jfs1Magic))
      throw new InvalidDataException("JFS1: superblock magic mismatch.");
    var totalBlocks = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(8));
    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x10));
    if (blockSize <= 0) blockSize = Jfs1Writer.DefaultBlockSize;
    var inodesPerBlock = blockSize / InodeSize;
    return new Geometry(blockSize, inodesPerBlock, InodeStartBlock: 1, totalBlocks);
  }

  // ── Inode helpers ─────────────────────────────────────────────────────────

  private static long InodeOffset(Geometry geom, int inode) {
    var blockOff = (inode - 2) / geom.InodesPerBlock;
    var slotOff = (inode - 2) % geom.InodesPerBlock;
    return (long)(geom.InodeStartBlock + blockOff) * geom.BlockSize + (long)slotOff * InodeSize;
  }

  private static (bool IsDir, ulong Size, int FirstBlock, int Blocks) ReadInode(Stream image, Geometry geom, int inode) {
    var ip = new byte[InodeSize];
    image.Position = InodeOffset(geom, inode);
    image.ReadExactly(ip);
    var firstBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(ip.AsSpan(16));
    var blocks = (int)BinaryPrimitives.ReadUInt32LittleEndian(ip.AsSpan(20));
    var size = BinaryPrimitives.ReadUInt64LittleEndian(ip.AsSpan(24));
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(ip.AsSpan(52));
    return ((mode & 0xF000) == 0x4000, size, firstBlock, blocks);
  }

  // A dinode slot is free when its di_number (offset 8) is zero. The inode array
  // is laid out contiguously from block 1 up to the first data block; the writer
  // puts the root directory in that first data block, so the root inode's extent
  // address is exactly (inodeStartBlock + inodeBlocks).
  private static int FindFreeInode(Stream image, Geometry geom) {
    var inodeBlocks = InodeArrayBlocks(image, geom);
    var capacity = inodeBlocks * geom.InodesPerBlock;
    for (var idx = 0; idx < capacity; idx++) {
      var inode = idx + 2;
      image.Position = InodeOffset(geom, inode) + 8; // di_number
      var num = new byte[4];
      image.ReadExactly(num);
      if (BinaryPrimitives.ReadUInt32LittleEndian(num) == 0) return inode;
    }
    return -1;
  }

  private static int InodeArrayBlocks(Stream image, Geometry geom) {
    var (_, _, rootFirst, _) = ReadInode(image, geom, RootInode);
    return rootFirst > geom.InodeStartBlock ? rootFirst - geom.InodeStartBlock : 1;
  }

  private static void WriteFileInode(Stream image, Geometry geom, int inode, uint firstBlock, uint blocks, ulong size) {
    var ip = new byte[InodeSize];
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(0), 1);              // di_inostamp
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(8), (uint)inode);    // di_number
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(12), 1);            // di_gen
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(16), firstBlock);   // extent address
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(20), blocks);       // extent length
    BinaryPrimitives.WriteUInt64LittleEndian(ip.AsSpan(24), size);         // di_size
    BinaryPrimitives.WriteUInt64LittleEndian(ip.AsSpan(32), blocks);       // di_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(40), 1);            // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(ip.AsSpan(52), ModeFile);     // di_mode
    var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt64LittleEndian(ip.AsSpan(56), now);
    BinaryPrimitives.WriteUInt64LittleEndian(ip.AsSpan(64), now);
    BinaryPrimitives.WriteUInt64LittleEndian(ip.AsSpan(72), now);
    image.Position = InodeOffset(geom, inode);
    image.Write(ip, 0, InodeSize);
  }

  private static void ZeroInode(Stream image, Geometry geom, int inode) {
    image.Position = InodeOffset(geom, inode);
    image.Write(new byte[InodeSize], 0, InodeSize);
  }

  // ── Extent allocation (append at EOF) ─────────────────────────────────────

  private static int AppendExtent(Stream image, ref Geometry geom, int blocks) {
    var firstBlock = (int)geom.TotalBlocks;
    var newTotal = geom.TotalBlocks + (ulong)blocks;
    var newLength = (long)newTotal * geom.BlockSize;
    if (image.Length < newLength) image.SetLength(newLength);
    geom = geom with { TotalBlocks = newTotal };
    // Update superblock s_size.
    image.Position = 8;
    var buf = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buf, newTotal);
    image.Write(buf, 0, 8);
    return firstBlock;
  }

  private static void WriteExtentData(Stream image, Geometry geom, int firstBlock, int blocks, byte[] data) {
    var extent = new byte[(long)blocks * geom.BlockSize];
    Array.Copy(data, extent, Math.Min(data.Length, extent.Length));
    image.Position = (long)firstBlock * geom.BlockSize;
    image.Write(extent, 0, extent.Length);
  }

  private static void WipeBlocks(Stream image, Geometry geom, int firstBlock, int blocks) {
    image.Position = (long)firstBlock * geom.BlockSize;
    image.Write(new byte[(long)blocks * geom.BlockSize], 0, blocks * geom.BlockSize);
  }

  // ── Root directory helpers ──────────────────────────────────────────────────

  private static byte[] ReadRootDirBlock(Stream image, Geometry geom, out int dirBlock) {
    var (_, _, firstBlock, _) = ReadInode(image, geom, RootInode);
    dirBlock = firstBlock;
    if (firstBlock == 0) throw new IOException("JFS1: root directory has no data block.");
    var blk = new byte[geom.BlockSize];
    image.Position = (long)firstBlock * geom.BlockSize;
    image.ReadExactly(blk);
    if (BinaryPrimitives.ReadUInt16LittleEndian(blk) != DirBlockMagic)
      throw new InvalidDataException("JFS1: root directory block magic mismatch.");
    return blk;
  }

  private static bool InsertRootDirEntry(Stream image, Geometry geom, uint inode, string name) {
    var blk = ReadRootDirBlock(image, geom, out var dirBlock);
    var slots = BinaryPrimitives.ReadUInt16LittleEndian(blk.AsSpan(2));
    var nb = Encoding.UTF8.GetBytes(name);
    if (nb.Length > 250) return false;

    // Walk to the end of the live dirents (the writer packs them contiguously).
    var cur = 4;
    for (var i = 0; i < slots && cur + 5 <= blk.Length; i++) {
      int nlen = blk[cur + 4];
      if (cur + 5 + nlen > blk.Length) return false;
      cur += 5 + nlen;
    }
    var slotLen = 5 + nb.Length;
    if (cur + slotLen > blk.Length) return false; // single-block dir full

    BinaryPrimitives.WriteUInt32LittleEndian(blk.AsSpan(cur), inode);
    blk[cur + 4] = (byte)nb.Length;
    nb.CopyTo(blk, cur + 5);
    BinaryPrimitives.WriteUInt16LittleEndian(blk.AsSpan(2), (ushort)(slots + 1));

    image.Position = (long)dirBlock * geom.BlockSize;
    image.Write(blk, 0, blk.Length);

    // Grow the root directory inode's di_size to cover the new dirent.
    var newDirSize = (ulong)(cur + slotLen);
    image.Position = InodeOffset(geom, RootInode) + 24;
    var sz = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(sz, newDirSize);
    image.Write(sz, 0, 8);
    return true;
  }

  private static bool FindRootDirEntry(Stream image, Geometry geom, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, geom, out _);
    var slots = BinaryPrimitives.ReadUInt16LittleEndian(blk.AsSpan(2));
    var cur = 4;
    for (var i = 0; i < slots && cur + 5 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt32LittleEndian(blk.AsSpan(cur));
      int nlen = blk[cur + 4];
      if (cur + 5 + nlen > blk.Length) break;
      var entryName = Encoding.UTF8.GetString(blk, cur + 5, nlen);
      if (entryName is not ("." or "..") && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
        inode = childInode;
        return true;
      }
      cur += 5 + nlen;
    }
    return false;
  }

  // Removes a dirent by rewriting the dir block without it (slots packed). Keeps
  // the block contiguous so the reader's sequential walk stays correct.
  private static bool RemoveRootDirEntry(Stream image, Geometry geom, string name, out uint inode) {
    inode = 0;
    var blk = ReadRootDirBlock(image, geom, out var dirBlock);
    var slots = BinaryPrimitives.ReadUInt16LittleEndian(blk.AsSpan(2));

    var rebuilt = new byte[geom.BlockSize];
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(0), DirBlockMagic);
    var outOff = 4;
    var kept = 0;
    var found = false;
    var cur = 4;
    for (var i = 0; i < slots && cur + 5 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt32LittleEndian(blk.AsSpan(cur));
      int nlen = blk[cur + 4];
      if (cur + 5 + nlen > blk.Length) break;
      var entryName = Encoding.UTF8.GetString(blk, cur + 5, nlen);
      var slotLen = 5 + nlen;
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

    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(2), (ushort)kept);
    image.Position = (long)dirBlock * geom.BlockSize;
    image.Write(rebuilt, 0, rebuilt.Length);

    image.Position = InodeOffset(geom, RootInode) + 24;
    var sz = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(sz, (ulong)outOff);
    image.Write(sz, 0, 8);
    return true;
  }
}
