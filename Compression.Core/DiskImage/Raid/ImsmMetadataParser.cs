using System.Buffers.Binary;
using System.Text;

namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Parses Intel Matrix Storage Manager / IMSM (isw) container metadata. The
/// metadata block (<c>imsm_super</c>) is written near the end of every member
/// disk and begins with the signature <c>"Intel Raid ISM Cfg Sig. "</c>. Field
/// offsets follow the on-disk structs documented in mdadm's <c>super-intel.c</c>
/// (<c>imsm_super</c> / <c>imsm_disk</c> / <c>imsm_dev</c> / <c>imsm_vol</c> /
/// <c>imsm_map</c>) for the classic non-migrating, single-map layout.
/// </summary>
/// <remarks>
/// The absolute offsets of the volume/vol/map sub-structs have drifted across
/// mdadm releases (RWH journal, 64-bit block splits). This parser targets the
/// classic layout and is verified against a hand-built superblock; wiring IMSM
/// into <see cref="RaidAssembler"/> for cross-file assembly is a documented
/// follow-on because a raw member file cannot reliably self-identify its slot.
/// </remarks>
public static class ImsmMetadataParser {
  /// <summary>The fixed 24-byte IMSM signature prefix.</summary>
  public static ReadOnlySpan<byte> SignaturePrefix => "Intel Raid ISM Cfg Sig. "u8;

  private const int SignatureFieldLength = 32;

  // imsm_super header offsets.
  private const int OffFamilyNum = 0x28;
  private const int OffGenerationNum = 0x2C;
  private const int OffNumDisks = 0x38;
  private const int OffNumRaidDevs = 0x39;
  private const int OffDiskArray = 0xD8;

  private const int ImsmDiskSize = 48;
  private const int DiskOffSerial = 0x00;
  private const int DiskOffTotalBlocksLo = 0x10;
  private const int DiskOffStatus = 0x18;
  private const int DiskOffTotalBlocksHi = 0x20;

  // imsm_dev (classic, single non-migrating map).
  private const int DevOffName = 0x00;
  private const int DevHeaderSize = 0x44; // imsm_dev header up to the imsm_vol
  private const int VolHeaderSize = 0x20; // imsm_vol header up to imsm_map[0]

  // imsm_map offsets.
  private const int MapOffBlocksPerMemberLo = 0x04;
  private const int MapOffBlocksPerStrip = 0x0C; // u16, in 512-byte blocks
  private const int MapOffRaidLevel = 0x0F;       // u8
  private const int MapOffNumMembers = 0x10;      // u8
  private const int MapOffDiskOrdTable = 0x30;    // u32 per member

  /// <summary>
  /// Scans the tail of <paramref name="member"/> for IMSM metadata and parses the
  /// container. Returns <c>null</c> when no IMSM signature is found.
  /// </summary>
  /// <param name="member">Seekable member device stream.</param>
  public static ImsmContainer? TryParse(Stream member) {
    ArgumentNullException.ThrowIfNull(member);
    if (!member.CanSeek || member.Length < SignatureFieldLength) return null;

    var scanLen = (int)Math.Min(member.Length, 128 * 1024);
    var tail = new byte[scanLen];
    member.Position = member.Length - scanLen;
    if (!TryReadExact(member, tail)) return null;

    var mpbStart = FindSignature(tail);
    if (mpbStart < 0) return null;

    return Parse(tail.AsSpan(mpbStart));
  }

  /// <summary>
  /// Parses an IMSM container from a buffer positioned at the <c>imsm_super</c>
  /// signature. Returns <c>null</c> if the buffer is too short or malformed.
  /// </summary>
  /// <param name="mpb">Buffer beginning at the IMSM signature.</param>
  public static ImsmContainer? Parse(ReadOnlySpan<byte> mpb) {
    if (mpb.Length < OffDiskArray) return null;
    if (!mpb[..SignaturePrefix.Length].SequenceEqual(SignaturePrefix)) return null;

    var version = Encoding.ASCII
      .GetString(mpb.Slice(SignaturePrefix.Length, SignatureFieldLength - SignaturePrefix.Length))
      .TrimEnd('\0', ' ');
    var familyNum = BinaryPrimitives.ReadUInt32LittleEndian(mpb.Slice(OffFamilyNum));
    var generationNum = BinaryPrimitives.ReadUInt32LittleEndian(mpb.Slice(OffGenerationNum));
    var numDisks = mpb[OffNumDisks];
    var numRaidDevs = mpb[OffNumRaidDevs];

    var disks = new List<ImsmDisk>(numDisks);
    for (var i = 0; i < numDisks; i++) {
      var d = OffDiskArray + i * ImsmDiskSize;
      if (d + ImsmDiskSize > mpb.Length) return null;
      var disk = mpb.Slice(d, ImsmDiskSize);
      var serial = Encoding.ASCII.GetString(disk.Slice(DiskOffSerial, 16)).TrimEnd('\0', ' ');
      var lo = BinaryPrimitives.ReadUInt32LittleEndian(disk.Slice(DiskOffTotalBlocksLo));
      var hi = BinaryPrimitives.ReadUInt32LittleEndian(disk.Slice(DiskOffTotalBlocksHi));
      disks.Add(new ImsmDisk {
        Serial = serial,
        TotalBlocks = (long)hi << 32 | lo,
        Status = BinaryPrimitives.ReadUInt32LittleEndian(disk.Slice(DiskOffStatus)),
      });
    }

    var volumes = new List<ImsmVolume>(numRaidDevs);
    var devOffset = OffDiskArray + numDisks * ImsmDiskSize;
    for (var v = 0; v < numRaidDevs; v++) {
      if (devOffset + DevHeaderSize + VolHeaderSize + MapOffDiskOrdTable > mpb.Length) return null;
      var name = Encoding.ASCII.GetString(mpb.Slice(devOffset + DevOffName, 16)).TrimEnd('\0', ' ');

      var mapOffset = devOffset + DevHeaderSize + VolHeaderSize;
      var map = mpb.Slice(mapOffset);
      var blocksPerStrip = BinaryPrimitives.ReadUInt16LittleEndian(map.Slice(MapOffBlocksPerStrip));
      var rawLevel = map[MapOffRaidLevel];
      var numMembers = map[MapOffNumMembers];
      var blocksPerMember = BinaryPrimitives.ReadUInt32LittleEndian(map.Slice(MapOffBlocksPerMemberLo));

      if (numMembers == 0 || numMembers > 32) return null;
      var ordTableEnd = mapOffset + MapOffDiskOrdTable + numMembers * 4;
      if (ordTableEnd > mpb.Length) return null;

      var order = new int[numMembers];
      for (var m = 0; m < numMembers; m++) {
        // Low 24 bits index the disk array; high 8 bits are ownership flags.
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(map.Slice(MapOffDiskOrdTable + m * 4));
        order[m] = (int)(raw & 0x00FFFFFF);
      }

      volumes.Add(new ImsmVolume {
        Name = name,
        RawLevel = rawLevel,
        Level = MapLevel(rawLevel, numMembers),
        ChunkSizeBytes = (long)blocksPerStrip * 512,
        NumMembers = numMembers,
        BlocksPerMember = blocksPerMember,
        DiskOrder = order,
      });

      devOffset = ordTableEnd; // classic single-map imsm_dev ends after the ord table.
    }

    return new ImsmContainer {
      Version = version,
      FamilyNum = familyNum,
      GenerationNum = generationNum,
      Disks = disks,
      Volumes = volumes,
    };
  }

  private static RaidLevel MapLevel(int rawLevel, int numMembers) => rawLevel switch {
    0 => RaidLevel.Raid0,
    5 => RaidLevel.Raid5,
    1 => numMembers >= 4 ? RaidLevel.Raid10 : RaidLevel.Raid1,
    10 => RaidLevel.Raid10,
    _ => RaidLevel.Raid0,
  };

  private static int FindSignature(ReadOnlySpan<byte> haystack) {
    for (var i = 0; i + SignaturePrefix.Length <= haystack.Length; i++)
      if (haystack.Slice(i, SignaturePrefix.Length).SequenceEqual(SignaturePrefix))
        return i;
    return -1;
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
