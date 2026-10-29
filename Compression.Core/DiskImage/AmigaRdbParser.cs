using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage;

/// <summary>
/// Parses the Amiga Rigid Disk Block (RDB) partition scheme. Big-endian throughout.
/// </summary>
/// <remarks>
/// <para>
/// The <c>RDSK</c> block may sit in any of the first 16 blocks of the disk
/// (<c>RDB_LOCATION_LIMIT</c>). It links, by block number, a chain of <c>PART</c> blocks; each
/// carries the partition's drive name (a BCPL string) and a <c>DosEnvec</c> whose geometry —
/// low/high cylinder, surfaces, blocks per track, block size in longwords — places the
/// partition on the disk, plus the <c>DosType</c> naming its filesystem (<c>DOS\1</c> = FFS,
/// <c>PFS\3</c>, <c>LNX\0</c>, …). Every block carries a checksum: the 32-bit sum of its first
/// <c>SummedLongs</c> longwords is zero.
/// </para>
/// <para>
/// A partition's byte range is <c>LowCyl × Surfaces × BlocksPerTrack × SizeBlock × 4</c> for the
/// start and <c>(HighCyl − LowCyl + 1) × Surfaces × BlocksPerTrack × SizeBlock × 4</c> for the
/// length.
/// </para>
/// References:
/// <list type="bullet">
///   <item><description>AmigaOS NDK, <c>devices/hardblocks.h</c> — <c>struct RigidDiskBlock</c>, <c>struct PartitionBlock</c>, <c>RDB_LOCATION_LIMIT</c>.</description></item>
///   <item><description>AmigaOS NDK, <c>dos/filehandler.h</c> — <c>struct DosEnvec</c> field order.</description></item>
/// </list>
/// </remarks>
public static class AmigaRdbParser {

  private const uint RdskId = 0x5244534B; // "RDSK"
  private const uint PartId = 0x50415254; // "PART"
  private const uint EndOfList = 0xFFFFFFFF;
  private const int SearchBlockSize = 512;
  private const int LocationLimit = 16;
  private const int MaxPartitions = 128;

  // RigidDiskBlock field offsets.
  private const int RdbSummedLongs = 4;
  private const int RdbBlockBytes = 16;
  private const int RdbPartitionList = 28;

  // PartitionBlock field offsets.
  private const int PbNext = 16;
  private const int PbDriveName = 36;
  private const int PbEnvironment = 128;

  // DosEnvec longword indices.
  private const int DeTableSize = 0;
  private const int DeSizeBlock = 1;
  private const int DeSurfaces = 3;
  private const int DeBlocksPerTrack = 5;
  private const int DeLowCyl = 9;
  private const int DeHighCyl = 10;
  private const int DeDosType = 16;

  /// <summary>Reports whether a checksummed <c>RDSK</c> block sits in the first 16 blocks.</summary>
  /// <param name="disk">A seekable stream over the whole disk.</param>
  public static bool IsRdb(Stream disk) => FindRdb(disk) >= 0;

  /// <summary>Parses every partition the RDB's <c>PART</c> chain describes.</summary>
  /// <param name="disk">A seekable stream over the whole disk.</param>
  /// <exception cref="InvalidDataException">No valid RDB is present.</exception>
  public static List<PartitionEntry> Parse(Stream disk) {
    var rdbBlock = FindRdb(disk);
    if (rdbBlock < 0) throw new InvalidDataException("No Amiga Rigid Disk Block (RDSK) in the first 16 blocks.");

    var rdb = ReadBlock(disk, (long)rdbBlock * SearchBlockSize, SearchBlockSize);
    var blockBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(rdb.AsSpan(RdbBlockBytes));
    if (blockBytes is < 256 or > 65536 || (blockBytes & (blockBytes - 1)) != 0) blockBytes = SearchBlockSize;

    var result = new List<PartitionEntry>();
    var seen = new HashSet<uint>();
    var next = BinaryPrimitives.ReadUInt32BigEndian(rdb.AsSpan(RdbPartitionList));
    while (next != EndOfList && result.Count < MaxPartitions && seen.Add(next)) {
      var offset = (long)next * blockBytes;
      if (offset + blockBytes > disk.Length) break;
      var block = ReadBlock(disk, offset, blockBytes);
      if (BinaryPrimitives.ReadUInt32BigEndian(block) != PartId || !ChecksumOk(block)) break;

      uint Env(int i) => BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(PbEnvironment + i * 4));
      var tableSize = Env(DeTableSize);
      if (tableSize >= DeHighCyl) {
        var bytesPerBlock = (long)Env(DeSizeBlock) * 4;
        var cylinderBytes = (long)Env(DeSurfaces) * Env(DeBlocksPerTrack) * bytesPerBlock;
        var low = Env(DeLowCyl);
        var high = Env(DeHighCyl);
        if (cylinderBytes > 0 && high >= low) {
          var dosType = tableSize >= DeDosType ? Env(DeDosType) : 0;
          result.Add(new PartitionEntry {
            Index = result.Count,
            StartOffset = low * cylinderBytes,
            Size = (high - low + 1L) * cylinderBytes,
            TypeName = DosTypeName(dosType),
            TypeCode = $"0x{dosType:X8}",
            Name = DriveName(block.AsSpan(PbDriveName, 32)),
            Source = "RDB"
          });
        }
      }

      next = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(PbNext));
    }
    return result;
  }

  private static int FindRdb(Stream disk) {
    if (!disk.CanSeek) return -1;
    for (var i = 0; i < LocationLimit; ++i) {
      var offset = (long)i * SearchBlockSize;
      if (offset + SearchBlockSize > disk.Length) break;
      var block = ReadBlock(disk, offset, SearchBlockSize);
      if (BinaryPrimitives.ReadUInt32BigEndian(block) == RdskId && ChecksumOk(block))
        return i;
    }
    return -1;
  }

  /// <summary>The block's first <c>SummedLongs</c> longwords sum to zero (mod 2^32).</summary>
  private static bool ChecksumOk(ReadOnlySpan<byte> block) {
    var summed = BinaryPrimitives.ReadUInt32BigEndian(block[RdbSummedLongs..]);
    if (summed < 4 || summed * 4L > block.Length) return false;
    uint sum = 0;
    for (var i = 0; i < summed; ++i)
      sum += BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
    return sum == 0;
  }

  /// <summary>A BCPL string: a length byte followed by that many characters.</summary>
  private static string DriveName(ReadOnlySpan<byte> bstr) {
    var length = Math.Min(bstr[0], (byte)31);
    return Encoding.Latin1.GetString(bstr.Slice(1, length));
  }

  /// <summary>Renders a DosType as its four characters, the last as a digit (<c>DOS\1</c> → <c>DOS1</c>).</summary>
  private static string DosTypeName(uint dosType) {
    if (dosType == 0) return "Amiga";
    Span<char> chars = stackalloc char[4];
    for (var i = 0; i < 4; ++i) {
      var b = (byte)(dosType >> (24 - i * 8));
      chars[i] = b is >= 0x20 and < 0x7F ? (char)b : (char)('0' + b % 10);
    }
    return new string(chars);
  }

  private static byte[] ReadBlock(Stream disk, long offset, int size) {
    var buffer = new byte[size];
    disk.Position = offset;
    _ = disk.ReadAtLeast(buffer, size, throwOnEndOfStream: false);
    return buffer;
  }
}
