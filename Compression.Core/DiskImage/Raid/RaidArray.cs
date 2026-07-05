namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Fully described, ready-to-read RAID array: its personality, geometry and the
/// ordered set of member devices. Feed one of these to
/// <see cref="RaidAssembledStream"/> to obtain the virtual guest-disk stream.
/// </summary>
public sealed class RaidArray {
  /// <summary>RAID personality.</summary>
  public required RaidLevel Level { get; init; }

  /// <summary>Stripe unit (chunk) size in bytes. Unused for RAID1/Linear.</summary>
  public required long ChunkSizeBytes { get; init; }

  /// <summary>Number of member slots (roles 0..RaidDisks-1).</summary>
  public required int RaidDisks { get; init; }

  /// <summary>Mirror copies for RAID10 near layout (2 for a standard mirror-of-stripes).</summary>
  public int NearCopies { get; init; } = 1;

  /// <summary>Raw md layout code, interpreted per level.</summary>
  public int Layout { get; init; }

  /// <summary>Members ordered by role; index equals <see cref="RaidMember.Role"/>. Missing roles carry a placeholder with no stream.</summary>
  public required IReadOnlyList<RaidMember> Members { get; init; }

  /// <summary>Usable data length of a single member in bytes.</summary>
  public required long PerDeviceDataBytes { get; init; }

  /// <summary>Array identity (UUID / family signature).</summary>
  public string ArrayUuid { get; init; } = "";

  /// <summary>Array name, when available.</summary>
  public string ArrayName { get; init; } = "";

  /// <summary>Number of data-bearing disks (excludes parity), per level.</summary>
  public int DataDisks => this.Level switch {
    RaidLevel.Raid4 or RaidLevel.Raid5 => this.RaidDisks - 1,
    RaidLevel.Raid6 => this.RaidDisks - 2,
    RaidLevel.Raid10 => this.RaidDisks / Math.Max(1, this.NearCopies),
    RaidLevel.Raid1 => 1,
    _ => this.RaidDisks,
  };

  /// <summary>Number of member slots currently backed by a device.</summary>
  public int PresentCount => this.Members.Count(m => m.IsPresent);
}
