#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Efs;

/// <summary>
/// Parsed SGI EFS superblock. All multi-byte fields are big-endian (MIPS native).
///
/// Layout (selected — first ~128 bytes per Linux <c>efs_fs_sb.h</c>):
///   0x00  s_size      i32 BE — total size of volume in BB (basic blocks = 512 B)
///   0x04  s_firstcg   i32 BE — first cylinder group BB
///   0x08  s_cgfsize   i32 BE — cylinder group size in BB
///   0x0C  s_cgisize   i16 BE — cylinder group inode-table size in BB
///   0x0E  s_sectors   i16 BE — sectors per track
///   0x10  s_heads     i16 BE — heads per cylinder
///   0x12  s_ncg       i16 BE — number of cylinder groups
///   0x14  s_dirty     i16 BE — fs needs check?
///   0x16  s_pad0      i16 BE
///   0x18  s_magic     u32 BE — 0x00072959
///   0x1C  s_fname     char[6] — volume name
///   ...
/// </summary>
internal sealed class EfsSuperblock {
  public const uint EfsMagic = 0x00072959u;

  public bool Valid { get; init; }
  public int SizeBlocks { get; init; }
  public int FirstCg { get; init; }
  public int CgSize { get; init; }
  public short CgIsize { get; init; }
  public short Sectors { get; init; }
  public short Heads { get; init; }
  public short NumCg { get; init; }
  public short Dirty { get; init; }
  public uint Magic { get; init; }
  public uint Time { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static EfsSuperblock TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 0x80) return new EfsSuperblock();
    var magic = ReadU32Be(image, 0x18);
    if (magic != EfsMagic) return new EfsSuperblock();

    var raw = image.Slice(0, Math.Min(512, image.Length)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new EfsSuperblock {
      Valid = true,
      SizeBlocks = ReadI32Be(image, 0x00),
      FirstCg = ReadI32Be(image, 0x04),
      CgSize = ReadI32Be(image, 0x08),
      CgIsize = ReadI16Be(image, 0x0C),
      Sectors = ReadI16Be(image, 0x0E),
      Heads = ReadI16Be(image, 0x10),
      NumCg = ReadI16Be(image, 0x12),
      Dirty = ReadI16Be(image, 0x14),
      Magic = magic,
      Time = ReadU32Be(image, 0x1C),
      RawBytes = raw,
    };
  }

  private static short ReadI16Be(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadInt16BigEndian(s.Slice(off, 2)) : (short)0;
  private static int ReadI32Be(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadInt32BigEndian(s.Slice(off, 4)) : 0;
  private static uint ReadU32Be(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(off, 4)) : 0u;
}
