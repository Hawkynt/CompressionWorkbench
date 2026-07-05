using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Decodes a Linux md <c>mdp_superblock_1</c> (metadata version 1.x) from a member
/// device. The superblock lives at one of three sub-version locations, all probed
/// here and validated against the self-describing <c>super_offset</c> field:
/// <list type="bullet">
///   <item><description><b>1.2</b> — 4&#160;KiB from the start of the device.</description></item>
///   <item><description><b>1.1</b> — at the very start of the device.</description></item>
///   <item><description><b>1.0</b> — 4&#160;KiB-aligned, ~8&#160;KiB from the end of the device.</description></item>
/// </list>
/// Field offsets follow the kernel layout in <c>drivers/md/md_p.h</c> and mdadm's
/// <c>super1.c</c>.
/// </summary>
public static class Md1SuperblockParser {
  /// <summary>md superblock magic (<c>MD_SB_MAGIC</c>), little-endian.</summary>
  public const uint MdMagic = 0xA92B4EFC;

  private const int SuperblockSize = 4096;
  private const int SectorSize = 512;

  // Field byte offsets within mdp_superblock_1.
  private const int OffMagic = 0x00;
  private const int OffSetUuid = 0x10;   // 16 bytes
  private const int OffSetName = 0x20;   // 32 bytes
  private const int OffLevel = 0x48;
  private const int OffLayout = 0x4C;
  private const int OffSize = 0x50;      // used component size, 512-byte sectors (u64)
  private const int OffChunk = 0x58;     // chunk size in 512-byte sectors
  private const int OffRaidDisks = 0x5C;
  private const int OffDataOffset = 0x80; // data start, 512-byte sectors (u64)
  private const int OffDataSize = 0x88;   // usable data on this device, 512-byte sectors (u64)
  private const int OffSuperOffset = 0x90;// this superblock's sector (u64)
  private const int OffDevNumber = 0xA0;
  private const int OffMaxDev = 0xDC;
  private const int OffDevRoles = 0x100;  // le16 per device, indexed by dev_number

  /// <summary>
  /// Tries to read a 1.x superblock from <paramref name="member"/>. Returns the decoded
  /// per-member metadata, or <c>null</c> when no valid superblock is present.
  /// </summary>
  /// <param name="member">Seekable member device stream.</param>
  public static RaidMemberMetadata? TryParse(Stream member) {
    ArgumentNullException.ThrowIfNull(member);
    if (!member.CanSeek || member.Length < SuperblockSize) return null;

    foreach (var candidate in CandidateOffsets(member.Length)) {
      if (candidate < 0 || candidate + SuperblockSize > member.Length) continue;
      var sb = new byte[SuperblockSize];
      member.Position = candidate;
      if (!TryReadExact(member, sb)) continue;

      if (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffMagic)) != MdMagic) continue;

      // Validate against the self-describing super_offset (in sectors).
      var superOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(OffSuperOffset)) * SectorSize;
      if (superOffset != candidate) continue;

      var rawLevel = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffLevel)));
      var level = RaidLevelMap.FromMd(rawLevel);
      if (level is null) continue;

      var layout = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffLayout)));
      // `size` (0x50) is the array's used per-device size, but mdadm leaves it 0 for
      // RAID0/Linear; fall back to `data_size` (0x88), the device's usable data extent.
      var componentSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(OffSize));
      if (componentSectors == 0)
        componentSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(OffDataSize));
      var chunkSectors = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffChunk));
      var raidDisks = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffRaidDisks));
      var dataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(OffDataOffset)) * SectorSize;
      var devNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffDevNumber));
      var maxDev = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffMaxDev));

      if (raidDisks <= 0 || raidDisks > 1024) continue;

      // This device's role is dev_roles[dev_number].
      var role = -1;
      if (devNumber >= 0 && devNumber < maxDev) {
        var rolePos = OffDevRoles + devNumber * 2;
        if (rolePos + 2 <= sb.Length)
          role = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(rolePos));
      }
      // 0xFFFF = spare, 0xFFFE = faulty: not a data-bearing role.
      if (role < 0 || role >= raidDisks) continue;

      var uuid = FormatUuid(sb.AsSpan(OffSetUuid, 16));
      var name = Encoding.ASCII.GetString(sb.AsSpan(OffSetName, 32)).TrimEnd('\0');

      return new RaidMemberMetadata {
        Format = RaidMetadataFormat.Mdraid1x,
        ArrayUuid = uuid,
        ArrayName = name,
        Level = level.Value,
        RaidDisks = raidDisks,
        ChunkSizeBytes = (long)chunkSectors * SectorSize,
        Layout = layout,
        NearCopies = level == RaidLevel.Raid10 ? (layout & 0xFF) : 1,
        Role = role,
        DataOffsetBytes = dataOffset,
        DataSizeBytes = componentSectors * SectorSize,
      };
    }
    return null;
  }

  private static IEnumerable<long> CandidateOffsets(long deviceLength) {
    yield return 4096;                                   // 1.2
    yield return 0;                                      // 1.1
    var sectors = deviceLength / SectorSize;             // 1.0: (sectors - 16) & ~7, in bytes
    var sbStart = (sectors - 8 * 2) & ~(4L * 2 - 1);
    yield return sbStart * SectorSize;
  }

  private static bool TryReadExact(Stream s, byte[] buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var n = s.Read(buffer, total, buffer.Length - total);
      if (n == 0) return false;
      total += n;
    }
    return true;
  }

  private static string FormatUuid(ReadOnlySpan<byte> raw) => Convert.ToHexString(raw).ToLowerInvariant();
}
