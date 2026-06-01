#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Htfs;

/// <summary>
/// Parsed SCO HTFS superblock. S5-style: superblock at sector 1 followed by an
/// inode array and per-block FAT-style free-list.
///
/// Layout (selected — first ~64 bytes per SCO <c>sys/fs/htfs/htfs_fs.h</c>):
///   0x00  s_magic   u32 LE — 0x012FD15D
///   0x04  s_isize   u32 LE — size in blocks of inode-list area
///   0x08  s_fsize   u32 LE — size in blocks of entire volume
///   0x0C  s_nfree   u16 LE — number of free blocks in s_free[]
///   ...   s_free[]  block numbers of free blocks (cache)
///   ...   s_ninode  u16 LE — count of free inodes cached
///   ...
/// </summary>
internal sealed class HtfsSuperblock {
  public const uint HtfsMagic = 0x012FD15Du;

  public bool Valid { get; init; }
  public uint Magic { get; init; }
  public uint Isize { get; init; }
  public uint Fsize { get; init; }
  public ushort Nfree { get; init; }
  public ushort Ninode { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static HtfsSuperblock TryParse(ReadOnlySpan<byte> image) {
    const int sbOffset = 512;
    if (image.Length < sbOffset + 64) return new HtfsSuperblock();
    var magic = ReadU32Le(image, sbOffset + 0x00);
    if (magic != HtfsMagic) return new HtfsSuperblock();

    var raw = image.Slice(sbOffset, Math.Min(512, image.Length - sbOffset)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new HtfsSuperblock {
      Valid = true,
      Magic = magic,
      Isize = ReadU32Le(image, sbOffset + 0x04),
      Fsize = ReadU32Le(image, sbOffset + 0x08),
      Nfree = ReadU16Le(image, sbOffset + 0x0C),
      // s_free[] occupies up to 50 entries × 4 bytes from 0x0E; place ninode
      // after that (the precise offset varies by HTFS revision; this is a
      // soft surface for detection-tier metadata).
      Ninode = ReadU16Le(image, sbOffset + 0xD6),
      RawBytes = raw,
    };
  }

  private static ushort ReadU16Le(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(off, 2)) : (ushort)0;
  private static uint ReadU32Le(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4)) : 0u;
}
