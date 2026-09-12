#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.LittleFs;

/// <summary>
/// Parsed LittleFS superblock as described by the official SPEC.md
/// (https://github.com/littlefs-project/littlefs/blob/master/SPEC.md).
/// The superblock lives at block 0 (and is mirrored to block 1 for redundancy);
/// it is itself a metadata-pair encoded as tag-data pairs.
///
/// Tag layout (32-bit big-endian):
///   bit 31     : valid bit (1 = tag valid)
///   bits 30-20 : type (11 bits)
///   bits 19-10 : id (10 bits)
///   bits 9-0   : size (10 bits)
///
/// Inline-struct tag (type 0x0022) carries the inline_struct payload after which
/// we expect: u32 version, u32 block_size, u32 block_count, u32 name_max,
/// u32 file_max, u32 attr_max — all little-endian.
/// </summary>
internal sealed class LittleFsSuperblock {
  public static readonly byte[] LittleFsAscii = "littlefs"u8.ToArray();

  public bool Valid { get; init; }
  public int SuperblockOffset { get; init; }
  public uint VersionMajor { get; init; }
  public uint VersionMinor { get; init; }
  public uint BlockSize { get; init; }
  public uint BlockCount { get; init; }
  public uint NameMax { get; init; }
  public uint FileMax { get; init; }
  public uint AttrMax { get; init; }
  public uint Revision { get; init; }
  public byte[] RawBytes { get; init; } = [];

  /// <summary>
  /// Locate the "littlefs" ASCII signature anywhere in the first 64 KB and parse
  /// the surrounding superblock. We don't try to decode the full tag stream — that
  /// requires walking the metadata-pair commit log with CRC validation, which is
  /// the embedded-RTOS LittleFS reference implementation in 1500 LOC. We do crack
  /// the inline_struct payload because it's at a fixed offset relative to the
  /// signature in every observed image.
  /// </summary>
  public static LittleFsSuperblock TryParse(ReadOnlySpan<byte> image) {
    var scanLen = Math.Min(image.Length, 65536);
    var scan = image.Slice(0, scanLen);
    var pos = scan.IndexOf(LittleFsAscii.AsSpan());
    if (pos < 0) return new LittleFsSuperblock();

    // The reference implementation lays out the superblock as:
    //   block_revision (u32 LE)  — first thing in every metadata block
    //   ... tag stream ...
    //   tag(SUPERBLOCK)  — id 0, size variable
    //   "littlefs" ASCII (8 bytes)
    //   [inline_struct tag word (u32) — present when the magic and the inline
    //    struct are separate tags, as the reference writer emits them]
    //   inline_struct payload  — u32 version, u32 block_size, u32 block_count,
    //                            u32 name_max, u32 file_max, u32 attr_max
    //
    // Some synthesised images pack the payload directly after the magic; the
    // reference layout interposes a 4-byte inline-struct tag word. Try the tight
    // layout first, then the tag-interposed one, accepting whichever yields a
    // power-of-two block size and a plausible block count.
    if (TryReadPayload(image, pos, pos + 8, out var sb) || TryReadPayload(image, pos, pos + 12, out sb))
      return sb!;

    return new LittleFsSuperblock();
  }

  private static bool TryReadPayload(ReadOnlySpan<byte> image, int magicPos, int payloadOffset, out LittleFsSuperblock? sb) {
    sb = null;
    if (payloadOffset + 24 > image.Length) return false;

    var version = ReadU32(image, payloadOffset + 0);
    var blockSize = ReadU32(image, payloadOffset + 4);
    var blockCount = ReadU32(image, payloadOffset + 8);
    var nameMax = ReadU32(image, payloadOffset + 12);
    var fileMax = ReadU32(image, payloadOffset + 16);
    var attrMax = ReadU32(image, payloadOffset + 20);

    // Sanity-check: block_size must be a power of two between 128 and 64 KB.
    if (blockSize is < 128u or > 65536u || (blockSize & (blockSize - 1)) != 0)
      return false;
    if (blockCount == 0 || blockCount > (1u << 24)) return false;

    // The block revision is the very first u32 of the metadata block — find the
    // start of the block by rounding the magic position down to a block boundary.
    var blockStart = magicPos / (int)blockSize * (int)blockSize;
    var revision = ReadU32(image, blockStart);

    // Capture a reasonable raw slice for downstream tools.
    var rawLen = Math.Min(512, image.Length - magicPos + 8);
    var raw = image.Slice(Math.Max(0, magicPos - 16), rawLen).ToArray();

    sb = new LittleFsSuperblock {
      Valid = true,
      SuperblockOffset = magicPos,
      VersionMajor = (version >> 16) & 0xFFFF,
      VersionMinor = version & 0xFFFF,
      BlockSize = blockSize,
      BlockCount = blockCount,
      NameMax = nameMax,
      FileMax = fileMax,
      AttrMax = attrMax,
      Revision = revision,
      RawBytes = raw,
    };
    return true;
  }

  private static uint ReadU32(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4)) : 0u;
}
