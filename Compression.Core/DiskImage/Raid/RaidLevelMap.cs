namespace Compression.Core.DiskImage.Raid;

/// <summary>
/// Translates the numeric md <c>level</c> field into a <see cref="RaidLevel"/>.
/// Values follow the Linux md convention (negative levels denote linear/multipath).
/// </summary>
internal static class RaidLevelMap {
  /// <summary>Maps an md numeric level to a <see cref="RaidLevel"/>, or <c>null</c> if unsupported.</summary>
  public static RaidLevel? FromMd(int mdLevel) => mdLevel switch {
    -1 => RaidLevel.Linear, // LEVEL_LINEAR
    0 => RaidLevel.Raid0,
    1 => RaidLevel.Raid1,
    4 => RaidLevel.Raid4,
    5 => RaidLevel.Raid5,
    6 => RaidLevel.Raid6,
    10 => RaidLevel.Raid10,
    _ => null,
  };
}
