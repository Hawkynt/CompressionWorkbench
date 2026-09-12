#pragma warning disable CS1591
namespace FileSystem.LittleFs;

/// <summary>
/// Shared on-disk constants and primitives for the littlefs v2 metadata format,
/// matching the reference implementation
/// (https://github.com/littlefs-project/littlefs/blob/master/SPEC.md and lfs.c/lfs_util.c).
/// </summary>
/// <remarks>
/// <para><b>Tag layout (32-bit big-endian on disk, here held as a host uint).</b></para>
/// <list type="bullet">
///   <item><description>bit 31     : valid bit (0 = valid on disk, because tags are stored XORed)</description></item>
///   <item><description>bits 30-20 : type (11 bits) — 3-bit type1 + 8-bit chunk</description></item>
///   <item><description>bits 19-10 : id (10 bits)</description></item>
///   <item><description>bits 9-0   : length (10 bits)</description></item>
/// </list>
/// <para>Tags are delta-encoded: each on-disk tag word is XORed with the previous
/// tag word (the first with an all-ones seed). A commit is a run of tags+data
/// terminated by a CRC tag whose payload is a 32-bit CRC of the commit so far.</para>
/// </remarks>
internal static class LittleFsFormat {
  // Tag type bytes (the high 8 bits of the 11-bit type field — "type1" nibble in the high 3 bits).
  public const uint TypeName = 0x100;       // LFS_TYPE_NAME family: 0x101 reg file, 0x102 dir, 0x100 superblock
  public const uint TypeReg = 0x101;
  public const uint TypeDir = 0x102;
  public const uint TypeSuperblock = 0x100;

  public const uint TypeStruct = 0x200;     // 0x200 dir-pair struct, 0x201 ctz struct, 0x202 inline struct
  public const uint TypeDirStruct = 0x200;
  public const uint TypeCtzStruct = 0x201;
  public const uint TypeInlineStruct = 0x202;

  public const uint TypeTail = 0x600;       // 0x600 soft tail, 0x601 hard tail
  public const uint TypeSoftTail = 0x600;
  public const uint TypeHardTail = 0x601;

  public const uint TypeCrc = 0x500;        // 0x5xx CRC tag

  public const uint LengthMax = 0x3FF;      // 10-bit length field; 0x3FF means "deleted/undefined"

  public const uint DiskVersion = (2u << 16) | 1u; // littlefs v2.1
  public const uint NameMax = 255;
  public const uint FileMaxValue = 0x7FFFFFFF;
  public const uint AttrMaxValue = 0x3FF;

  /// <summary>Encodes a tag into its host-order 32-bit form.</summary>
  public static uint MakeTag(uint type, uint id, uint length)
    => ((type & 0x7FF) << 20) | ((id & 0x3FF) << 10) | (length & 0x3FF);

  public static uint TagType(uint tag) => (tag >> 20) & 0x7FF;
  public static uint TagId(uint tag) => (tag >> 10) & 0x3FF;
  public static uint TagLength(uint tag) => tag & 0x3FF;

  // littlefs CRC-32: reflected polynomial 0xEDB88320, seed 0xFFFFFFFF, no final XOR.
  private static readonly uint[] CrcTable = BuildCrcTable();

  private static uint[] BuildCrcTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
    return table;
  }

  /// <summary>Continues a littlefs CRC over <paramref name="data"/> starting from <paramref name="crc"/>.</summary>
  public static uint Crc(uint crc, ReadOnlySpan<byte> data) {
    foreach (var b in data)
      crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc;
  }
}
