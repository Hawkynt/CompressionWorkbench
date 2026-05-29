namespace Compression.Core.DiskImage;

/// <summary>
/// Logical partition type. Used by partition-table editors so callers can
/// describe a partition without caring whether the on-disk table is MBR
/// (1-byte type code) or GPT (16-byte type GUID); the editor maps both
/// directions automatically.
/// </summary>
public enum PartitionType {
  /// <summary>Unallocated / empty slot.</summary>
  Empty = 0,

  /// <summary>FAT12 (MBR 0x01).</summary>
  Fat12,

  /// <summary>FAT16 below 32MB (MBR 0x04).</summary>
  Fat16Small,

  /// <summary>FAT16 above 32MB (MBR 0x06).</summary>
  Fat16,

  /// <summary>FAT16 with LBA addressing (MBR 0x0E).</summary>
  Fat16Lba,

  /// <summary>FAT32 with CHS addressing (MBR 0x0B).</summary>
  Fat32Chs,

  /// <summary>FAT32 with LBA addressing (MBR 0x0C). Default for FAT32.</summary>
  Fat32Lba,

  /// <summary>NTFS / exFAT / HPFS (MBR 0x07).</summary>
  NtfsExfat,

  /// <summary>Linux native filesystem (MBR 0x83 / GPT 0FC63DAF-…).</summary>
  Linux,

  /// <summary>Linux swap (MBR 0x82 / GPT 0657FD6D-…).</summary>
  LinuxSwap,

  /// <summary>Linux LVM (MBR 0x8E / GPT E6D6D379-…).</summary>
  LinuxLvm,

  /// <summary>Linux RAID (MBR 0xFD / GPT A19D880F-…).</summary>
  LinuxRaid,

  /// <summary>Apple HFS+ (MBR 0xAF / GPT 48465300-…).</summary>
  AppleHfsPlus,

  /// <summary>Apple UFS (MBR 0xA8 / GPT 55465300-…).</summary>
  AppleUfs,

  /// <summary>Apple APFS (GPT 7C3457EF-…).</summary>
  AppleApfs,

  /// <summary>Microsoft Basic Data (GPT EBD0A0A2-…). Maps to MBR 0x07.</summary>
  MicrosoftBasicData,

  /// <summary>Microsoft Reserved (GPT E3C9E316-…).</summary>
  MicrosoftReserved,

  /// <summary>EFI System Partition (MBR 0xEF / GPT C12A7328-…).</summary>
  EfiSystem,

  /// <summary>BIOS Boot Partition (GPT 21686148-…).</summary>
  BiosBoot,

  /// <summary>MBR extended partition (LBA, type 0x0F). Acts as a container for logical partitions chained through EBRs. No GPT equivalent.</summary>
  ExtendedLba,

  /// <summary>Unknown or unmapped type.</summary>
  Unknown
}

/// <summary>
/// Translation tables between <see cref="PartitionType"/> and on-disk MBR
/// bytes / GPT GUIDs. Centralised so the MBR↔GPT conversion paths stay in
/// sync.
/// </summary>
public static class PartitionTypeMapping {

  /// <summary>Returns the MBR partition-type byte for the given logical type.</summary>
  public static byte ToMbrByte(PartitionType type) => type switch {
    PartitionType.Empty => 0x00,
    PartitionType.Fat12 => 0x01,
    PartitionType.Fat16Small => 0x04,
    PartitionType.Fat16 => 0x06,
    PartitionType.Fat16Lba => 0x0E,
    PartitionType.Fat32Chs => 0x0B,
    PartitionType.Fat32Lba => 0x0C,
    PartitionType.NtfsExfat => 0x07,
    PartitionType.MicrosoftBasicData => 0x07,
    PartitionType.Linux => 0x83,
    PartitionType.LinuxSwap => 0x82,
    PartitionType.LinuxLvm => 0x8E,
    PartitionType.LinuxRaid => 0xFD,
    PartitionType.AppleHfsPlus => 0xAF,
    PartitionType.AppleUfs => 0xA8,
    PartitionType.AppleApfs => 0xAF, // No standard MBR code; fall back to HFS+.
    PartitionType.MicrosoftReserved => 0x07,
    PartitionType.EfiSystem => 0xEF,
    PartitionType.BiosBoot => 0xDA, // Non-FS data marker.
    PartitionType.ExtendedLba => 0x0F, // MBR extended (LBA).
    _ => 0x83 // unknown → Linux native (most permissive fallback)
  };

  /// <summary>Returns the GPT type GUID for the given logical type.</summary>
  public static Guid ToGptGuid(PartitionType type) => type switch {
    PartitionType.Empty => Guid.Empty,
    PartitionType.Fat12 or PartitionType.Fat16 or PartitionType.Fat16Lba or
    PartitionType.Fat16Small or PartitionType.Fat32Chs or PartitionType.Fat32Lba or
    PartitionType.NtfsExfat or PartitionType.MicrosoftBasicData
      => new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"),
    PartitionType.MicrosoftReserved => new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE"),
    PartitionType.Linux => new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"),
    PartitionType.LinuxSwap => new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F"),
    PartitionType.LinuxLvm => new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928"),
    PartitionType.LinuxRaid => new Guid("A19D880F-05FC-4D3B-A006-743F0F84911E"),
    PartitionType.AppleHfsPlus => new Guid("48465300-0000-11AA-AA11-00306543ECAC"),
    PartitionType.AppleUfs => new Guid("55465300-0000-11AA-AA11-00306543ECAC"),
    PartitionType.AppleApfs => new Guid("7C3457EF-0000-11AA-AA11-00306543ECAC"),
    PartitionType.EfiSystem => new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"),
    PartitionType.BiosBoot => new Guid("21686148-6449-6E6F-744E-656564454649"),
    _ => new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4") // unknown → Linux filesystem
  };

  /// <summary>Best-effort reverse lookup from an MBR byte to a logical type.</summary>
  public static PartitionType FromMbrByte(byte b) => b switch {
    0x00 => PartitionType.Empty,
    0x01 => PartitionType.Fat12,
    0x04 => PartitionType.Fat16Small,
    0x06 => PartitionType.Fat16,
    0x0E => PartitionType.Fat16Lba,
    0x0B => PartitionType.Fat32Chs,
    0x0C => PartitionType.Fat32Lba,
    0x07 => PartitionType.NtfsExfat,
    0x82 => PartitionType.LinuxSwap,
    0x83 => PartitionType.Linux,
    0x8E => PartitionType.LinuxLvm,
    0xAF => PartitionType.AppleHfsPlus,
    0xA8 => PartitionType.AppleUfs,
    0xEF => PartitionType.EfiSystem,
    0xFD => PartitionType.LinuxRaid,
    0x05 or 0x0F or 0x85 => PartitionType.ExtendedLba,
    _ => PartitionType.Unknown
  };

  /// <summary>Best-effort reverse lookup from a GPT type GUID to a logical type.</summary>
  public static PartitionType FromGptGuid(Guid guid) {
    var key = guid.ToString("D").ToUpperInvariant();
    return key switch {
      "00000000-0000-0000-0000-000000000000" => PartitionType.Empty,
      "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => PartitionType.MicrosoftBasicData,
      "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => PartitionType.MicrosoftReserved,
      "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => PartitionType.Linux,
      "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => PartitionType.LinuxSwap,
      "E6D6D379-F507-44C2-A23C-238F2A3DF928" => PartitionType.LinuxLvm,
      "A19D880F-05FC-4D3B-A006-743F0F84911E" => PartitionType.LinuxRaid,
      "48465300-0000-11AA-AA11-00306543ECAC" => PartitionType.AppleHfsPlus,
      "55465300-0000-11AA-AA11-00306543ECAC" => PartitionType.AppleUfs,
      "7C3457EF-0000-11AA-AA11-00306543ECAC" => PartitionType.AppleApfs,
      "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => PartitionType.EfiSystem,
      "21686148-6449-6E6F-744E-656564454649" => PartitionType.BiosBoot,
      _ => PartitionType.Unknown
    };
  }
}
