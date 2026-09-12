namespace FileFormat.Zoo;

/// <summary>
/// Historical Zoo writer-generation target. This controls archive-header and
/// directory-entry capabilities; per-entry extraction requirements are derived
/// independently from the compression method actually emitted.
/// </summary>
public enum ZooCompatibilityProfile {
  /// <summary>
  /// Zoo 1.40-compatible archive manipulation: 34-byte type-0 archive header
  /// and type-1 directory entries with DOS-style short names only.
  /// </summary>
  Zoo140,

  /// <summary>
  /// Zoo 2.00-compatible archive manipulation: extended 42-byte archive header
  /// and type-2 directory entries for portable long names and paths.
  /// </summary>
  Zoo200,
}

/// <summary>Compatibility rules for Zoo archive and entry versions.</summary>
public static class ZooCompatibility {
  /// <summary>Returns the minimum Zoo version required to manipulate the archive form.</summary>
  public static (byte Major, byte Minor) GetArchiveVersion(ZooCompatibilityProfile profile) => profile switch {
    ZooCompatibilityProfile.Zoo140 => (1, 40),
    ZooCompatibilityProfile.Zoo200 => (2, 0),
    _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Zoo compatibility profile."),
  };

  /// <summary>Returns whether the archive generation supports type-2 long-name directory entries.</summary>
  public static bool SupportsLongNames(ZooCompatibilityProfile profile) => profile switch {
    ZooCompatibilityProfile.Zoo140 => false,
    ZooCompatibilityProfile.Zoo200 => true,
    _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Zoo compatibility profile."),
  };

  /// <summary>Returns the minimum Zoo version required to extract an emitted packing method.</summary>
  public static (byte Major, byte Minor) GetExtractVersion(ZooCompressionMethod method) => method switch {
    ZooCompressionMethod.Store or ZooCompressionMethod.Lzw => (1, 0),
    _ => throw new NotSupportedException($"Zoo packing method {(byte)method} has no known extraction compatibility level."),
  };
}
