#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.UnixV7;

/// <summary>
/// On-disk constants of the Seventh Edition (V7) UNIX file system, as laid out by
/// the PDP-11 kernel: 512-byte blocks, the superblock (<c>struct filsys</c>) in
/// block 1, 64-byte inodes (<c>struct dinode</c>) from block 2 up to
/// <c>s_isize</c>, 16-byte directory entries (<c>struct direct</c>).
/// <para>
/// Integers are PDP-11 order: 16-bit words little-endian, a 32-bit long is the
/// high word first. Block addresses inside an inode are packed into three bytes
/// (<c>di_addr[40]</c>, 13 addresses: 10 direct, single, double, triple indirect);
/// indirect blocks hold 128 four-byte addresses.
/// </para>
/// <para>References: the V7 headers <c>sys/h/filsys.h</c>, <c>ino.h</c>,
/// <c>dir.h</c>, <c>fblk.h</c> (TUHS); Linux <c>fs/sysv</c>, which mounts V7
/// volumes as file-system type <c>v7</c>.</para>
/// </summary>
internal static class UnixV7Layout {
  public const int BlockSize = 512;
  public const int SuperblockBlock = 1;
  public const int InodeSize = 64;
  public const int InodesPerBlock = BlockSize / InodeSize;
  public const int RootInode = 2;
  public const int DirectEntrySize = 16;
  public const int MaxNameLength = 14;
  public const int NicFree = 50;          // NICFREE: free-block cache in the superblock
  public const int NicInode = 100;        // NICINOD: free-inode cache in the superblock
  public const int AddressCount = 13;     // NADDR
  public const int DirectAddresses = 10;
  public const int AddressesPerBlock = BlockSize / 4;
  public const int MaxVolumeBlocks = 0x00FFFFFF; // 24-bit block addresses (Linux V7_MAXSIZE)

  // struct filsys field offsets.
  public const int SIsize = 0;      // u16  first block past the i-list
  public const int SFsize = 2;      // long volume size in blocks
  public const int SNfree = 6;      // i16
  public const int SFree = 8;       // long[50]
  public const int SNinode = 208;   // i16
  public const int SInode = 210;    // u16[100]
  public const int STime = 414;     // long
  public const int STfree = 418;    // long total free blocks
  public const int STinode = 422;   // u16  total free inodes

  // struct dinode field offsets.
  public const int DiMode = 0;
  public const int DiNlink = 2;
  public const int DiUid = 4;
  public const int DiGid = 6;
  public const int DiSize = 8;
  public const int DiAddr = 12;
  public const int DiAtime = 52;
  public const int DiMtime = 56;
  public const int DiCtime = 60;

  // di_mode type bits.
  public const ushort IfMt = 0xF000;     // 0170000
  public const ushort IfDir = 0x4000;    // 0040000
  public const ushort IfReg = 0x8000;    // 0100000

  public static int PdpInt32(ReadOnlySpan<byte> s)
    => (BinaryPrimitives.ReadUInt16LittleEndian(s) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);

  public static void WritePdpInt32(Span<byte> s, int value) {
    BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)((uint)value >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)value);
  }

  /// <summary>A three-byte inode block address: byte 0 holds bits 16–23, bytes 1–2 bits 0–15 low byte first.</summary>
  public static int Address3(ReadOnlySpan<byte> s) => (s[0] << 16) | s[1] | (s[2] << 8);

  public static void WriteAddress3(Span<byte> s, int value) {
    s[0] = (byte)(value >> 16);
    s[1] = (byte)value;
    s[2] = (byte)(value >> 8);
  }

  /// <summary>Byte offset of inode <paramref name="ino"/> (1-based).</summary>
  public static long InodeOffset(int ino) => 2L * BlockSize + (long)(ino - 1) * InodeSize;
}
