using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Svr4Bfs;

/// <summary>
/// On-disk constants and structures of the SVR4 / UnixWare Boot File System
/// (<c>/stand</c>). Every value here is documented in <c>docs/SVR4-BFS-ON-DISK.md</c>
/// together with the measurement that confirmed it.
/// </summary>
/// <remarks>
/// The whole volume is little-endian and addressed in 512-byte blocks: block 0 holds
/// the superblock, blocks 1.. hold 64-byte inodes up to <c>s_start</c>, and every
/// file — the root directory included — occupies one contiguous run of blocks.
/// </remarks>
public static class Svr4BfsLayout {
  /// <summary>Superblock magic, little-endian at offset 0.</summary>
  public const uint Magic = 0x1BADFACE;

  /// <summary>The only block size the format knows.</summary>
  public const int BlockSize = 512;

  /// <summary>Bytes per inode.</summary>
  public const int InodeSize = 64;

  /// <summary>Inodes per 512-byte block.</summary>
  public const int InodesPerBlock = BlockSize / InodeSize;

  /// <summary>Bytes per directory entry: a 16-bit inode number and a 14-byte name.</summary>
  public const int DirentSize = 16;

  /// <summary>Longest name a directory entry holds.</summary>
  public const int NameMax = 14;

  /// <summary>Inode number of the root (and only) directory; inode slot 0.</summary>
  public const int RootIno = 2;

  /// <summary><c>i_vtype</c> of a regular file.</summary>
  public const uint VReg = 1;

  /// <summary><c>i_vtype</c> of a directory.</summary>
  public const uint VDir = 2;

  /// <summary>Value of the compaction fields (<c>s_from</c>, <c>s_to</c>, …) on a clean volume.</summary>
  public const uint NoCompaction = 0xFFFFFFFF;

  /// <summary>Byte offset of inode <paramref name="ino"/> inside the image.</summary>
  public static long InodeOffset(int ino) => BlockSize + (long)(ino - RootIno) * InodeSize;

  /// <summary>Highest inode number an inode area ending at <paramref name="start"/> can hold.</summary>
  public static int LastInode(uint start) => (int)((start - BlockSize) / InodeSize) + RootIno - 1;

  /// <summary>Encodes a name as stored in a directory entry, or returns null when it cannot be stored.</summary>
  public static byte[]? EncodeName(string name) {
    if (string.IsNullOrEmpty(name) || name is "." or "..") return null;
    if (name.IndexOfAny(['/', '\\', '\0']) >= 0) return null;
    var bytes = Encoding.UTF8.GetBytes(name);
    return bytes.Length <= NameMax ? bytes : null;
  }

  /// <summary>Decodes a NUL-padded directory-entry name.</summary>
  public static string DecodeName(ReadOnlySpan<byte> raw) {
    var end = raw.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? raw : raw[..end]);
  }
}

/// <summary>The 512-byte BFS superblock.</summary>
/// <param name="Start">Byte offset of the first data block (end of the inode area).</param>
/// <param name="End">Byte offset of the last byte of the volume.</param>
/// <param name="From">Compaction source (<c>s_from</c>); all ones when clean.</param>
/// <param name="To">Compaction target (<c>s_to</c>); all ones when clean.</param>
/// <param name="BFrom">Compaction backup source (<c>s_bfrom</c>).</param>
/// <param name="BTo">Compaction backup target (<c>s_bto</c>).</param>
/// <param name="FsName">Six-byte file-system name, space padded.</param>
/// <param name="Volume">Six-byte volume name, space padded.</param>
public sealed record Svr4BfsSuperblock(uint Start, uint End, uint From, uint To, uint BFrom, uint BTo, string FsName, string Volume) {
  /// <summary>True when no compaction was interrupted; only a clean volume is edited.</summary>
  public bool IsClean => this.From == Svr4BfsLayout.NoCompaction && this.To == Svr4BfsLayout.NoCompaction;

  /// <summary>First block a file may occupy.</summary>
  public uint FirstDataBlock => this.Start / Svr4BfsLayout.BlockSize;

  /// <summary>Last whole block inside the volume.</summary>
  public uint LastBlock => (uint)(((ulong)this.End + 1) / Svr4BfsLayout.BlockSize) - 1;

  /// <summary>Parses block 0, or returns null when it is not a BFS superblock.</summary>
  public static Svr4BfsSuperblock? TryParse(ReadOnlySpan<byte> block) {
    if (block.Length < Svr4BfsLayout.BlockSize) return null;
    if (BinaryPrimitives.ReadUInt32LittleEndian(block) != Svr4BfsLayout.Magic) return null;
    var start = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
    var end = BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
    // The inode area needs at least the root inode, and the data area at least one block.
    if (start < 2 * Svr4BfsLayout.BlockSize || start % Svr4BfsLayout.BlockSize != 0 || end < start) return null;
    return new(
      start, end,
      BinaryPrimitives.ReadUInt32LittleEndian(block[12..]),
      BinaryPrimitives.ReadUInt32LittleEndian(block[16..]),
      BinaryPrimitives.ReadUInt32LittleEndian(block[20..]),
      BinaryPrimitives.ReadUInt32LittleEndian(block[24..]),
      Encoding.ASCII.GetString(block.Slice(28, 6)),
      Encoding.ASCII.GetString(block.Slice(34, 6)));
  }

  /// <summary>Serialises the superblock into a zeroed 512-byte block.</summary>
  public void WriteTo(Span<byte> block) {
    block[..Svr4BfsLayout.BlockSize].Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(block, Svr4BfsLayout.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(block[4..], this.Start);
    BinaryPrimitives.WriteUInt32LittleEndian(block[8..], this.End);
    BinaryPrimitives.WriteUInt32LittleEndian(block[12..], this.From);
    BinaryPrimitives.WriteUInt32LittleEndian(block[16..], this.To);
    BinaryPrimitives.WriteUInt32LittleEndian(block[20..], this.BFrom);
    BinaryPrimitives.WriteUInt32LittleEndian(block[24..], this.BTo);
    PadField(this.FsName, block.Slice(28, 6));
    PadField(this.Volume, block.Slice(34, 6));
  }

  private static void PadField(string value, Span<byte> field) {
    field.Fill((byte)' ');
    var bytes = Encoding.ASCII.GetBytes(value);
    bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
  }
}

/// <summary>One 64-byte BFS inode.</summary>
public record struct Svr4BfsInode(
  ushort Ino, uint StartBlock, uint EndBlock, uint EndOffset, uint VType, uint Mode,
  uint Uid, uint Gid, uint Nlink, uint Atime, uint Mtime, uint Ctime) {

  /// <summary>True when the slot is free.</summary>
  public readonly bool IsFree => this.Ino == 0;

  /// <summary>True when the inode owns no data blocks (an empty file stores start block 0).</summary>
  public readonly bool HasNoBlocks => this.StartBlock == 0;

  /// <summary>File size in bytes: the end offset names the last byte, relative to the volume.</summary>
  public readonly long Size => this.HasNoBlocks ? 0 : (long)this.EndOffset + 1 - (long)this.StartBlock * Svr4BfsLayout.BlockSize;

  /// <summary>Parses one inode.</summary>
  public static Svr4BfsInode Parse(ReadOnlySpan<byte> raw) => new(
    BinaryPrimitives.ReadUInt16LittleEndian(raw),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[24..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[28..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[32..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[36..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[40..]),
    BinaryPrimitives.ReadUInt32LittleEndian(raw[44..]));

  /// <summary>Serialises the inode into a 64-byte slot (padding zeroed).</summary>
  public readonly void WriteTo(Span<byte> raw) {
    raw[..Svr4BfsLayout.InodeSize].Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(raw, this.Ino);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[4..], this.StartBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[8..], this.EndBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[12..], this.EndOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[16..], this.VType);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[20..], this.Mode);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[24..], this.Uid);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[28..], this.Gid);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[32..], this.Nlink);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[36..], this.Atime);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[40..], this.Mtime);
    BinaryPrimitives.WriteUInt32LittleEndian(raw[44..], this.Ctime);
  }

  /// <summary>The end offset an inode stores for <paramref name="size"/> bytes from <paramref name="startBlock"/>.</summary>
  public static uint EndOffsetFor(uint startBlock, long size)
    => unchecked((uint)((long)startBlock * Svr4BfsLayout.BlockSize + size - 1));

  /// <summary>Blocks needed for <paramref name="size"/> bytes.</summary>
  public static uint BlocksFor(long size) => (uint)((size + Svr4BfsLayout.BlockSize - 1) / Svr4BfsLayout.BlockSize);
}
