namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// RAID personality of an assembled array. Mirrors the small set of levels the
/// Linux md stack and Intel IMSM/Matrix metadata can describe. The numeric
/// meaning of striping/parity is implemented by <see cref="RaidAssembledStream"/>.
/// </summary>
public enum RaidLevel {
  /// <summary>Concatenation (JBOD/linear): member data regions are joined end to end.</summary>
  Linear,

  /// <summary>Striping with no redundancy: virtual chunks round-robin across all members.</summary>
  Raid0,

  /// <summary>Mirroring: every member holds an identical copy of the data.</summary>
  Raid1,

  /// <summary>Striping with a single dedicated parity disk (parity fixed on the last member).</summary>
  Raid4,

  /// <summary>Striping with distributed single parity (rotating, mdadm default left-symmetric).</summary>
  Raid5,

  /// <summary>Striping with distributed dual parity (P + Q).</summary>
  Raid6,

  /// <summary>Stripe over mirrored pairs (mirror-of-stripes, near layout).</summary>
  Raid10,
}
