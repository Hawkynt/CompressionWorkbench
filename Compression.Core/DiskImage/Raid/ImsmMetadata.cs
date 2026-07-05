namespace Compression.Core.DiskImage.Raid;

/// <summary>A physical disk record in an IMSM container.</summary>
public sealed class ImsmDisk {
  /// <summary>Disk serial string (trimmed).</summary>
  public required string Serial { get; init; }

  /// <summary>Total disk size in 512-byte blocks.</summary>
  public required long TotalBlocks { get; init; }

  /// <summary>Raw disk status flags.</summary>
  public uint Status { get; init; }
}

/// <summary>A RAID volume (imsm_dev/imsm_map) defined inside an IMSM container.</summary>
public sealed class ImsmVolume {
  /// <summary>Volume name.</summary>
  public required string Name { get; init; }

  /// <summary>Mapped RAID personality.</summary>
  public required RaidLevel Level { get; init; }

  /// <summary>Raw IMSM raid_level byte (0=RAID0, 1=RAID1/RAID10, 5=RAID5).</summary>
  public required int RawLevel { get; init; }

  /// <summary>Stripe unit (chunk) size in bytes (blocks_per_strip &#215; 512).</summary>
  public required long ChunkSizeBytes { get; init; }

  /// <summary>Number of member disks in the volume map.</summary>
  public required int NumMembers { get; init; }

  /// <summary>Usable data size of the volume in 512-byte blocks.</summary>
  public long BlocksPerMember { get; init; }

  /// <summary>Disk-order table: map slot &#8594; index into the container disk array.</summary>
  public required IReadOnlyList<int> DiskOrder { get; init; }
}

/// <summary>A parsed IMSM (Intel Matrix Storage Manager) container.</summary>
public sealed class ImsmContainer {
  /// <summary>Container signature/version string (after the fixed prefix).</summary>
  public required string Version { get; init; }

  /// <summary>Family number identifying the container across its member disks.</summary>
  public required uint FamilyNum { get; init; }

  /// <summary>Generation counter (incremented each metadata write).</summary>
  public required uint GenerationNum { get; init; }

  /// <summary>Physical disks in the container.</summary>
  public required IReadOnlyList<ImsmDisk> Disks { get; init; }

  /// <summary>RAID volumes defined on the container.</summary>
  public required IReadOnlyList<ImsmVolume> Volumes { get; init; }
}
