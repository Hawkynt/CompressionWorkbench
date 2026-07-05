using System.Buffers.Binary;

namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Decodes a Linux md <c>mdp_superblock_t</c> (metadata version 0.90). The 4&#160;KiB
/// superblock sits at a 64&#160;KiB-aligned offset near the end of the device
/// (<c>MD_NEW_SIZE_SECTORS</c>), and array data starts at device offset 0. Field
/// offsets follow the section layout in <c>drivers/md/md_p.h</c>.
/// </summary>
public static class Md09SuperblockParser {
  private const int SectorSize = 512;
  private const int ReservedSectors = 128; // MD_RESERVED_SECTORS (64 KiB)
  private const int SuperblockSize = 4096;

  // Generic constant section (byte 0).
  private const int OffMagic = 0;
  private const int OffMajor = 4;
  private const int OffLevel = 28;
  private const int OffSize = 32;      // apparent per-disk size, 1 KiB blocks
  private const int OffRaidDisks = 40;
  private const int OffSetUuid0 = 20;
  private const int OffSetUuid1 = 52;
  private const int OffSetUuid2 = 56;
  private const int OffSetUuid3 = 60;

  // Personality section (byte 256).
  private const int OffLayout = 256;
  private const int OffChunkSize = 260; // in bytes

  // Active descriptor "this_disk" (byte 3968 = word 992).
  private const int OffThisDiskRaidDisk = 3968 + 12; // number,major,minor, then raid_disk

  /// <summary>
  /// Tries to read a 0.90 superblock from <paramref name="member"/>. Returns the decoded
  /// per-member metadata, or <c>null</c> when no valid superblock is present.
  /// </summary>
  /// <param name="member">Seekable member device stream.</param>
  public static RaidMemberMetadata? TryParse(Stream member) {
    ArgumentNullException.ThrowIfNull(member);
    if (!member.CanSeek || member.Length < SuperblockSize + SectorSize) return null;

    var offset = SuperblockOffset(member.Length);
    if (offset < 0 || offset + SuperblockSize > member.Length) return null;

    var sb = new byte[SuperblockSize];
    member.Position = offset;
    if (!TryReadExact(member, sb)) return null;

    if (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffMagic)) != Md1SuperblockParser.MdMagic) return null;
    if (BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffMajor)) != 0) return null; // major_version 0

    var rawLevel = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffLevel)));
    var level = RaidLevelMap.FromMd(rawLevel);
    if (level is null) return null;

    var raidDisks = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffRaidDisks));
    if (raidDisks <= 0 || raidDisks > 1024) return null;

    var sizeKb = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSize));
    var chunkBytes = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffChunkSize));
    var layout = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffLayout)));
    var role = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffThisDiskRaidDisk)));
    if (role < 0 || role >= raidDisks) return null;

    var uuid = string.Concat(
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSetUuid0)).ToString("x8"),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSetUuid1)).ToString("x8"),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSetUuid2)).ToString("x8"),
      BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(OffSetUuid3)).ToString("x8"));

    return new RaidMemberMetadata {
      Format = RaidMetadataFormat.Mdraid090,
      ArrayUuid = uuid,
      ArrayName = "",
      Level = level.Value,
      RaidDisks = raidDisks,
      ChunkSizeBytes = chunkBytes,
      Layout = layout,
      NearCopies = level == RaidLevel.Raid10 ? (layout & 0xFF) : 1,
      Role = role,
      DataOffsetBytes = 0, // 0.90 data begins at device start; superblock is at the end.
      DataSizeBytes = (long)sizeKb * 1024,
    };
  }

  /// <summary>Computes the 0.90 superblock byte offset for a device of the given length.</summary>
  public static long SuperblockOffset(long deviceLength) {
    var sectors = deviceLength / SectorSize;
    var sbSectors = (sectors & ~((long)ReservedSectors - 1)) - ReservedSectors;
    return sbSectors * SectorSize;
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
}
