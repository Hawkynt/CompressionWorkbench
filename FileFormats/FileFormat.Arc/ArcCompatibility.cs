namespace FileFormat.Arc;

/// <summary>
/// Historical ARC-family capability profiles. A profile limits the compression
/// method IDs that may be emitted; it does not change the ARC entry layout.
/// </summary>
public enum ArcCompatibilityProfile {
  /// <summary>SEA ARC 2.x: store, pack and squeeze.</summary>
  SeaArc2,

  /// <summary>SEA ARC 4.0: adds old-style Lempel-Ziv crunching (method 5).</summary>
  SeaArc40,

  /// <summary>SEA ARC 4.1: adds new-style crunching (method 6).</summary>
  SeaArc41,

  /// <summary>SEA ARC 4.6: adds the improved-hash cruncher (method 7).</summary>
  SeaArc46,

  /// <summary>SEA ARC 5.0+: adds dynamic LZW with adaptive reset (method 8).</summary>
  SeaArc50,

  /// <summary>PKARC/PKXARC family: ARC methods plus Phil Katz's Squashed method 9.</summary>
  PkArc,

  /// <summary>All ARC-family methods implemented by this writer.</summary>
  Extended,
}

/// <summary>Resolves method availability for historical ARC-family profiles.</summary>
public static class ArcCompatibility {
  /// <summary>Returns whether <paramref name="method"/> can be emitted for <paramref name="profile"/>.</summary>
  public static bool IsSupported(ArcCompatibilityProfile profile, ArcCompressionMethod method) {
    if (!Enum.IsDefined(profile))
      return false;

    return method switch {
      ArcCompressionMethod.Stored or ArcCompressionMethod.Packed or ArcCompressionMethod.Squeezed => true,
      ArcCompressionMethod.Crunched5 => profile is not ArcCompatibilityProfile.SeaArc2,
      ArcCompressionMethod.Crunched6 => profile is ArcCompatibilityProfile.SeaArc41
        or ArcCompatibilityProfile.SeaArc46
        or ArcCompatibilityProfile.SeaArc50
        or ArcCompatibilityProfile.PkArc
        or ArcCompatibilityProfile.Extended,
      ArcCompressionMethod.Crunched7 => profile is ArcCompatibilityProfile.SeaArc46
        or ArcCompatibilityProfile.SeaArc50
        or ArcCompatibilityProfile.PkArc
        or ArcCompatibilityProfile.Extended,
      ArcCompressionMethod.Crunched => profile is ArcCompatibilityProfile.SeaArc50
        or ArcCompatibilityProfile.PkArc
        or ArcCompatibilityProfile.Extended,
      ArcCompressionMethod.Squashed => profile is ArcCompatibilityProfile.PkArc
        or ArcCompatibilityProfile.Extended,
      _ => false,
    };
  }

  /// <summary>Throws when the method is not valid for the selected historical profile.</summary>
  public static void EnsureSupported(ArcCompatibilityProfile profile, ArcCompressionMethod method) {
    if (!Enum.IsDefined(profile))
      throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown ARC compatibility profile.");

    if (IsSupported(profile, method))
      return;

    throw new NotSupportedException($"ARC method {(byte)method} ({method}) is not available in compatibility profile {profile}.");
  }
}
