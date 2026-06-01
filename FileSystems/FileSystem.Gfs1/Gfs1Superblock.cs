#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Gfs1;

/// <summary>
/// Parsed Sistina GFS (pre-GFS2) superblock surface. All multi-byte fields are
/// big-endian. The Sistina/OpenGFS on-disk layout uses meta-header
/// <c>mh_magic = 0x01161970</c> at the start of every metadata block; the
/// GFS-vs-GFS2 distinction lives in <c>sb_fs_format</c> /
/// <c>sb_multihost_format</c> (GFS = 1900, GFS2 = 1901).
///
/// Layout (selected — first 192 bytes of the GFS superblock):
///   0x00  mh_magic         u32 BE  — 0x01161970
///   0x04  mh_type          u32 BE
///   0x08  mh_generation    u64 BE
///   0x10  mh_format        u32 BE
///   0x14  mh_incarn        u32 BE
///   0x18  sb_fs_format     u32 BE  — 1309 (GFS) / 1801 (GFS2)
///   0x1C  sb_multihost_fmt u32 BE  — 1900 (GFS) / 1901 (GFS2)
///   ...
/// </summary>
internal sealed class Gfs1Superblock {
  public const uint MhMagicConst = 0x01161970u;
  public const long SuperblockOffset = 65536;

  public bool Valid { get; init; }
  public uint MhMagic { get; init; }
  public uint FsFormat { get; init; }
  public uint MultihostFormat { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static Gfs1Superblock TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < SuperblockOffset + 0x80) return new Gfs1Superblock();
    var off = (int)SuperblockOffset;
    var magic = ReadU32Be(image, off);
    if (magic != MhMagicConst) return new Gfs1Superblock();

    var raw = image.Slice(off, Math.Min(512, image.Length - off)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new Gfs1Superblock {
      Valid = true,
      MhMagic = magic,
      FsFormat = ReadU32Be(image, off + 0x18),
      MultihostFormat = ReadU32Be(image, off + 0x1C),
      RawBytes = raw,
    };
  }

  private static uint ReadU32Be(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(off, 4)) : 0u;
}
