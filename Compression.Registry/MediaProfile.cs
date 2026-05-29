#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Predefined media profiles for disk image resizing. Each profile specifies
/// the canonical byte size of the target medium.
/// </summary>
public enum MediaProfile {
  /// <summary>3.5" HD floppy: 1,474,560 bytes (1.44 MB, FAT12).</summary>
  Floppy35Hd,
  /// <summary>3.5" DD floppy: 737,280 bytes (720 KB).</summary>
  Floppy35Dd,
  /// <summary>5.25" HD floppy: 1,228,800 bytes (1.2 MB).</summary>
  Floppy525Hd,
  /// <summary>5.25" DD floppy: 368,640 bytes (360 KB).</summary>
  Floppy525Dd,
  /// <summary>CD-ROM: 681,984,000 bytes (650 MB, ISO 9660).</summary>
  Cd,
  /// <summary>DVD: 4,700,000,000 bytes (4.7 GB).</summary>
  Dvd,
  /// <summary>Blu-ray Disc: 25,025,314,816 bytes (25 GB).</summary>
  BluRay,
}

/// <summary>
/// Maps <see cref="MediaProfile"/> values to their canonical byte sizes and
/// provides lookup from human-readable profile names.
/// </summary>
public static class MediaProfileLookup {

  /// <summary>Returns the byte size for the given profile.</summary>
  public static long GetSize(MediaProfile profile) => profile switch {
    MediaProfile.Floppy35Hd  => 1_474_560L,
    MediaProfile.Floppy35Dd  => 737_280L,
    MediaProfile.Floppy525Hd => 1_228_800L,
    MediaProfile.Floppy525Dd => 368_640L,
    MediaProfile.Cd           => 681_984_000L,
    MediaProfile.Dvd          => 4_700_000_000L,
    MediaProfile.BluRay       => 25_025_314_816L,
    _ => throw new System.ArgumentOutOfRangeException(nameof(profile), profile, "Unknown media profile."),
  };

  /// <summary>
  /// Tries to parse a profile name (case-insensitive). Recognized names:
  /// <c>3.5-hd</c>, <c>3.5-dd</c>, <c>5.25-hd</c>, <c>5.25-dd</c>,
  /// <c>cd</c>, <c>dvd</c>, <c>bd</c>.
  /// </summary>
  public static bool TryParse(string name, out MediaProfile profile) {
    profile = default;
    if (string.IsNullOrWhiteSpace(name)) return false;
    switch (name.Trim().ToLowerInvariant()) {
      case "3.5-hd": profile = MediaProfile.Floppy35Hd; return true;
      case "3.5-dd": profile = MediaProfile.Floppy35Dd; return true;
      case "5.25-hd": profile = MediaProfile.Floppy525Hd; return true;
      case "5.25-dd": profile = MediaProfile.Floppy525Dd; return true;
      case "cd": profile = MediaProfile.Cd; return true;
      case "dvd": profile = MediaProfile.Dvd; return true;
      case "bd": profile = MediaProfile.BluRay; return true;
      default: return false;
    }
  }

  /// <summary>All known profiles with their names and sizes for display.</summary>
  public static System.Collections.Generic.IReadOnlyList<(string Name, MediaProfile Profile, long Size)> AllProfiles =>
  [
    ("3.5-hd", MediaProfile.Floppy35Hd, 1_474_560L),
    ("3.5-dd", MediaProfile.Floppy35Dd, 737_280L),
    ("5.25-hd", MediaProfile.Floppy525Hd, 1_228_800L),
    ("5.25-dd", MediaProfile.Floppy525Dd, 368_640L),
    ("cd", MediaProfile.Cd, 681_984_000L),
    ("dvd", MediaProfile.Dvd, 4_700_000_000L),
    ("bd", MediaProfile.BluRay, 25_025_314_816L),
  ];
}
