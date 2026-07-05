namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// On-disk RAID metadata format a member superblock was recognised as.
/// </summary>
public enum RaidMetadataFormat {
  /// <summary>Linux md superblock version 0.90 (fixed-size block near end of device).</summary>
  Mdraid090,

  /// <summary>Linux md superblock version 1.x (1.0 end / 1.1 start / 1.2 4K-from-start).</summary>
  Mdraid1x,

  /// <summary>Intel Matrix Storage Manager / IMSM (isw) container metadata.</summary>
  Imsm,
}

/// <summary>
/// The geometry and identity a single member device contributes to an array,
/// as decoded from that member's RAID superblock. Members carrying the same
/// <see cref="ArrayUuid"/> (and <see cref="Format"/>) belong to one array;
/// <see cref="Role"/> gives the member's slot within it.
/// </summary>
public sealed class RaidMemberMetadata {
  /// <summary>Metadata format this member was decoded from.</summary>
  public required RaidMetadataFormat Format { get; init; }

  /// <summary>Stable identifier grouping members of the same array (array UUID / family signature).</summary>
  public required string ArrayUuid { get; init; }

  /// <summary>Human-readable array name, when the metadata carries one.</summary>
  public string ArrayName { get; init; } = "";

  /// <summary>RAID personality of the array.</summary>
  public required RaidLevel Level { get; init; }

  /// <summary>Total number of member slots the array was created with.</summary>
  public required int RaidDisks { get; init; }

  /// <summary>Stripe unit (chunk) size in bytes. Zero/irrelevant for RAID1 and Linear.</summary>
  public required long ChunkSizeBytes { get; init; }

  /// <summary>Raw layout code (md <c>layout</c> field); interpretation depends on level.</summary>
  public int Layout { get; init; }

  /// <summary>Number of mirror copies (RAID10 near layout); 2 for a standard mirror-of-stripes.</summary>
  public int NearCopies { get; init; } = 1;

  /// <summary>This member's zero-based role/slot in the array.</summary>
  public required int Role { get; init; }

  /// <summary>Byte offset within this member at which array data begins.</summary>
  public required long DataOffsetBytes { get; init; }

  /// <summary>Usable data length (per member) in bytes.</summary>
  public required long DataSizeBytes { get; init; }
}
