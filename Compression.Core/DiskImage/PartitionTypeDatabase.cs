namespace Compression.Core.DiskImage;

/// <summary>
/// Maps MBR partition type bytes and GPT partition type GUIDs to human-readable names.
/// </summary>
public static class PartitionTypeDatabase {

  /// <summary>
  /// Gets the filesystem name for an MBR partition type byte.
  /// </summary>
  public static string GetMbrTypeName(byte type) => type switch {
    0x00 => "Empty",
    0x01 => "FAT12",
    0x04 => "FAT16 (<32MB)",
    0x05 => "Extended (CHS)",
    0x06 => "FAT16 (>32MB)",
    0x07 => "NTFS/exFAT/HPFS",
    0x0B => "FAT32 (CHS)",
    0x0C => "FAT32 (LBA)",
    0x0E => "FAT16 (LBA)",
    0x0F => "Extended (LBA)",
    0x11 => "Hidden FAT12",
    0x14 => "Hidden FAT16 (<32MB)",
    0x16 => "Hidden FAT16 (>32MB)",
    0x17 => "Hidden NTFS/HPFS",
    0x1B => "Hidden FAT32 (CHS)",
    0x1C => "Hidden FAT32 (LBA)",
    0x1E => "Hidden FAT16 (LBA)",
    0x27 => "Windows Recovery",
    0x42 => "Dynamic Disk",
    0x63 => "Unix",
    0x7E => "GPT Protective",
    0x7F => "Chromium OS kernel",
    0x81 => "Minix",
    0x82 => "Linux swap",
    0x83 => "Linux",
    0x85 => "Linux extended",
    0x8E => "Linux LVM",
    0xA5 => "FreeBSD",
    0xA6 => "OpenBSD",
    0xA8 => "macOS",
    0xA9 => "NetBSD",
    0xAB => "macOS Boot",
    0xAF => "macOS HFS+",
    0xBE => "Solaris boot",
    0xBF => "Solaris",
    0xEB => "BeOS/Haiku",
    0xEE => "GPT Protective MBR",
    0xEF => "EFI System Partition",
    0xFB => "VMware VMFS",
    0xFC => "VMware swap",
    0xFD => "Linux RAID",
    _ => $"Unknown (0x{type:X2})"
  };

  /// <summary>
  /// Gets the filesystem name for a GPT partition type GUID.
  /// </summary>
  public static string GetGptTypeName(Guid typeGuid) {
    var key = typeGuid.ToString("D").ToUpperInvariant();
    return key switch {
      "00000000-0000-0000-0000-000000000000" => "Unused",
      "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI System Partition",
      "21686148-6449-6E6F-744E-656564454649" => "BIOS Boot",
      "024DEE41-33E7-11D3-9D69-0008C781F39F" => "MBR Partition Scheme",
      // Microsoft
      "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft Reserved",
      "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft Basic Data",
      "5808C8AA-7E8F-42E0-85D2-E1E90434CFB3" => "Microsoft LDM Metadata",
      "AF9B60A0-1431-4F62-BC68-3311714A69AD" => "Microsoft LDM Data",
      "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC" => "Windows Recovery",
      // Linux
      "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux Filesystem",
      "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux Swap",
      "E6D6D379-F507-44C2-A23C-238F2A3DF928" => "Linux LVM",
      "A19D880F-05FC-4D3B-A006-743F0F84911E" => "Linux RAID",
      "933AC7E1-2EB4-4F13-B844-0E14E2AEF915" => "Linux /home",
      "3B8F8425-20E0-4F3B-907F-1A25A76F98E8" => "Linux /srv",
      // Apple
      "48465300-0000-11AA-AA11-00306543ECAC" => "Apple HFS+",
      "55465300-0000-11AA-AA11-00306543ECAC" => "Apple UFS",
      "7C3457EF-0000-11AA-AA11-00306543ECAC" => "Apple APFS",
      "426F6F74-0000-11AA-AA11-00306543ECAC" => "Apple Boot",
      // FreeBSD
      "83BD6B9D-7F41-11DC-BE0B-001560B84F0F" => "FreeBSD Boot",
      "516E7CB4-6ECF-11D6-8FF8-00022D09712B" => "FreeBSD Data",
      "516E7CB5-6ECF-11D6-8FF8-00022D09712B" => "FreeBSD Swap",
      "516E7CB6-6ECF-11D6-8FF8-00022D09712B" => "FreeBSD UFS",
      "516E7CB8-6ECF-11D6-8FF8-00022D09712B" => "FreeBSD ZFS",
      _ => $"Unknown ({key})"
    };
  }
}
