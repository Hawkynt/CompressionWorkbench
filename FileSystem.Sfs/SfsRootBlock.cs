#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Sfs;

/// <summary>
/// Parsed Amiga Smart Filesystem (SFS) root block. SFS replaces OFS/FFS on AmigaOS
/// 4 and AROS, and is documented at http://www.xs4all.nl/~hjohn/SFS/ (Amiga SFS spec).
///
/// All multi-byte fields are big-endian (Amiga native).
///
/// Root block layout (selected — first 512 bytes):
///   0x00 ID            "SFS\0" (0x53 0x46 0x53 0x00) — primary magic
///   0x04 chksum        u32 BE — block checksum
///   0x08 ownblock      u32 BE — block number of this root block
///   0x0C version       u16 BE — version (1 or 2)
///   0x0E sequencenumber u16 BE — root block generation
///   0x10 datecreated   u32 BE — Amiga timestamp
///   0x14 bits          u8     — flag bits
///   0x15 reserved      u8 × 3
///   0x18 reserved2     u32 BE
///   0x1C firstbyteh    u32 BE — first byte of partition (high)
///   0x20 firstbyte     u32 BE — first byte (low / for old images)
///   0x24 lastbyteh     u32 BE
///   0x28 lastbyte      u32 BE
///   0x2C totalblocks   u32 BE — total blocks in volume
///   0x30 blocksize     u32 BE — block size in bytes (typically 512)
///   0x34 ...           index/admin/bitmap pointers
/// </summary>
internal sealed class SfsRootBlock {
  public static readonly byte[] SfsMagic = [0x53, 0x46, 0x53, 0x00]; // "SFS\0"

  public bool Valid { get; init; }
  public uint Checksum { get; init; }
  public uint OwnBlock { get; init; }
  public ushort Version { get; init; }
  public ushort SequenceNumber { get; init; }
  public uint DateCreated { get; init; }
  public uint TotalBlocks { get; init; }
  public uint BlockSize { get; init; }
  public uint RootBlockOffset { get; init; }
  public byte[] RawBytes { get; init; } = [];

  public static SfsRootBlock TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 512) return new SfsRootBlock();
    // Probe canonical offsets — root block usually at 0, but on Amiga partitioned
    // disks the partition payload starts mid-disk.
    foreach (var offset in (int[])[0, 512, 1024]) {
      if (offset + 4 > image.Length) continue;
      if (!image.Slice(offset, 4).SequenceEqual(SfsMagic.AsSpan())) continue;
      return Parse(image, offset);
    }
    return new SfsRootBlock();
  }

  private static SfsRootBlock Parse(ReadOnlySpan<byte> image, int offset) {
    var raw = image.Slice(offset, Math.Min(512, image.Length - offset)).ToArray();
    if (raw.Length < 512) {
      var padded = new byte[512];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    var blockSize = ReadU32Be(image, offset + 0x30);
    // Sanity-check block size — must be a sensible power-of-two between 256 and 32768.
    // Fall back to 512 (the SFS default) if the field is bogus.
    if (blockSize is < 256u or > 32768u || (blockSize & (blockSize - 1)) != 0)
      blockSize = 512;

    return new SfsRootBlock {
      Valid = true,
      Checksum = ReadU32Be(image, offset + 0x04),
      OwnBlock = ReadU32Be(image, offset + 0x08),
      Version = ReadU16Be(image, offset + 0x0C),
      SequenceNumber = ReadU16Be(image, offset + 0x0E),
      DateCreated = ReadU32Be(image, offset + 0x10),
      TotalBlocks = ReadU32Be(image, offset + 0x2C),
      BlockSize = blockSize,
      RootBlockOffset = (uint)offset,
      RawBytes = raw,
    };
  }

  private static ushort ReadU16Be(ReadOnlySpan<byte> s, int off) =>
    off + 2 <= s.Length ? BinaryPrimitives.ReadUInt16BigEndian(s.Slice(off, 2)) : (ushort)0;

  private static uint ReadU32Be(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(off, 4)) : 0u;
}
