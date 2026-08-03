#pragma warning disable CS1591
namespace FileSystem.Qnx4;

/// <summary>
/// Where the fields of a QNX4 inode entry actually are, and how its block
/// numbers are counted.
/// </summary>
/// <remarks>
/// <para>These follow <c>linux/qnx4_fs.h</c>. The offsets used here before were
/// not that struct: the mode was read out of the first timestamp and the status
/// byte two bytes early, so a volume this wrote was self-consistent and read by
/// nothing else. Three things follow from the real layout and each one is a
/// reason the driver refused a volume.</para>
///
/// <para>First, block 1 is not the root directory: it is the superblock, and
/// the superblock is four inode entries — the root directory, the inode
/// overflow file, and two boot slots. Second, an extent's block number counts
/// from one, so the block it names is one lower on disk. Third, the root
/// directory has to contain an entry called <c>.bitmap</c>, which the driver
/// looks for before it will mount anything.</para>
/// </remarks>
internal static class Qnx4Layout {

  internal const int BlockSize = 512;
  internal const int InodeSize = 64;
  internal const int InodesPerBlock = BlockSize / InodeSize;   // 8

  /// <summary>The block holding the four inode entries that describe the volume.</summary>
  internal const uint SuperBlock = 1;

  /// <summary>The name the driver insists on finding in the root directory.</summary>
  internal const string BitmapName = ".bitmap";

  internal const string InodesName = ".inodes";

  // ── inode entry field offsets ──────────────────────────────────────────
  internal const int InName = 0x00;
  internal const int NameBytes = 16;
  internal const int InSize = 0x10;
  internal const int InExtentBlock = 0x14;
  internal const int InExtentSize = 0x18;
  internal const int InXblk = 0x1C;
  internal const int InFtime = 0x20;
  internal const int InMtime = 0x24;
  internal const int InAtime = 0x28;
  internal const int InCtime = 0x2C;
  internal const int InNumExtents = 0x30;
  internal const int InMode = 0x32;
  internal const int InUid = 0x34;
  internal const int InGid = 0x36;
  internal const int InNlink = 0x38;
  internal const int InType = 0x3E;
  internal const int InStatus = 0x3F;

  // ── status bits ────────────────────────────────────────────────────────
  internal const byte FileUsed = 0x01;
  internal const byte FileModified = 0x02;
  internal const byte FileBusy = 0x04;
  internal const byte FileLink = 0x08;

  internal const ushort ModeDir = 0x4000;
  internal const ushort ModeReg = 0x8000;

  /// <summary>
  /// What an extent's block number becomes on disk. QNX4 counts these from
  /// one, so the block a value names sits one lower.
  /// </summary>
  internal static long ByteOffsetOf(uint extentBlock) => (extentBlock - 1L) * BlockSize;

  /// <summary>What to store in an extent for a given block on disk.</summary>
  internal static uint ExtentValueFor(long deviceBlock) => (uint)(deviceBlock + 1);
}
