#pragma warning disable CS1591
namespace FileSystem.Ntfs;

/// <summary>
/// The NTFS volume versions this writer produces and this reader accepts, as the
/// <c>$VOLUME_INFORMATION</c> major/minor pair that names them.
/// </summary>
/// <remarks>
/// The enum value is the on-disk pair packed as <c>(major &lt;&lt; 8) | minor</c>, so the
/// two bytes $Volume carries and the option the caller picked are one value rather
/// than two that can drift apart.
/// </remarks>
public enum NtfsVersion {

  /// <summary>NTFS 1.2 — Windows NT 3.51 and NT 4.0.</summary>
  V12 = 0x0102,

  /// <summary>NTFS 3.0 — Windows 2000.</summary>
  V30 = 0x0300,

  /// <summary>NTFS 3.1 — Windows XP and later.</summary>
  V31 = 0x0301,
}

/// <summary>
/// What a declared <see cref="NtfsVersion"/> obliges the volume's content to be.
/// </summary>
/// <remarks>
/// <para>
/// Every version-sensitive structure is derived here rather than at its write site,
/// because the defect this guards against is a volume whose stamp says one thing and
/// whose bytes say another: a 1.2 stamp over $Secure and $Extend, or a 3.1 stamp over
/// pre-3.1 records. One version value feeds the record header, the metadata file set,
/// the $AttrDef table and the $STANDARD_INFORMATION shape.
/// </para>
/// <para>
/// The per-version facts come from the reference formatter. <c>mkntfs</c> creates
/// 16 system files for a 1.2 volume and 27 from 3.0 on, names record 9 <c>$Quota</c>
/// before 3.0 and <c>$Secure</c> from 3.0, leaves record 11 empty before 3.0 and makes
/// it the <c>$Extend</c> directory from 3.0, and picks between two compiled-in
/// $AttrDef tables on <c>major_ver &lt; 3</c>. The 1.2 table's own
/// <c>$STANDARD_INFORMATION</c> entry gives minimum and maximum 48, where the 3.x one
/// gives 48 and 72 — which is what makes the 72-byte shape a 3.x form and the 48-byte
/// shape the only one 1.2 permits.
/// </para>
/// </remarks>
public static class NtfsVersions {

  /// <summary>The default a caller gets who does not choose: NTFS 3.1.</summary>
  public const NtfsVersion Default = NtfsVersion.V31;

  /// <summary>The <c>$VOLUME_INFORMATION</c> major version byte.</summary>
  public static byte Major(this NtfsVersion version) => (byte)((int)version >> 8);

  /// <summary>The <c>$VOLUME_INFORMATION</c> minor version byte.</summary>
  public static byte Minor(this NtfsVersion version) => (byte)((int)version & 0xFF);

  /// <summary>The version as the option and the tooling spell it — "1.2", "3.0", "3.1".</summary>
  public static string ToVersionText(this NtfsVersion version)
    => $"{version.Major()}.{version.Minor()}";

  /// <summary>Every version this implementation writes and reads, oldest first.</summary>
  public static IReadOnlyList<NtfsVersion> All { get; } = [NtfsVersion.V12, NtfsVersion.V30, NtfsVersion.V31];

  /// <summary>Recognises an on-disk major/minor pair, refusing one we do not implement.</summary>
  public static bool TryFromOnDisk(byte major, byte minor, out NtfsVersion version) {
    version = (NtfsVersion)((major << 8) | minor);
    return Enum.IsDefined(version);
  }

  /// <summary>Parses the "1.2"/"3.0"/"3.1" spelling the creation option uses.</summary>
  public static bool TryParse(string? text, out NtfsVersion version) {
    version = Default;
    var parts = text?.Trim().Split('.');
    if (parts is not { Length: 2 }
        || !byte.TryParse(parts[0], out var major)
        || !byte.TryParse(parts[1], out var minor))
      return false;

    return TryFromOnDisk(major, minor, out version);
  }

  /// <summary>
  /// Whether the volume centralises security descriptors in <c>$Secure</c> (MFT record 9)
  /// and carries the <c>$Extend</c> directory (record 11) — both arrivals of NTFS 3.0.
  /// </summary>
  /// <remarks>
  /// Before 3.0 record 9 is <c>$Quota</c>, which NT never used, and record 11 is an empty
  /// record with no name in the root index. This is also why declaring 1.2 correctly is
  /// what makes omitting $Secure safe: ntfs-3g only calls <c>ntfs_open_secure()</c> on a
  /// volume whose major version is at least 3.
  /// </remarks>
  public static bool HasCentralisedSecurity(this NtfsVersion version) => version.Major() >= 3;

  /// <summary>
  /// Whether MFT records carry the NTFS 3.1 record-number field at offset 44, which puts
  /// their update-sequence array at 48 instead of 42.
  /// </summary>
  public static bool UsesExtendedRecordHeader(this NtfsVersion version) => version == NtfsVersion.V31;

  /// <summary>
  /// The <c>$STANDARD_INFORMATION</c> value length this version writes: 48 bytes up to
  /// NTFS 1.2, 72 from 3.0 on, where OwnerId, SecurityId, QuotaCharged and the USN were
  /// appended.
  /// </summary>
  public static int StandardInformationLength(this NtfsVersion version) => version.Major() >= 3 ? 72 : 48;

  /// <summary>The name MFT record 9 carries: <c>$Quota</c> before NTFS 3.0, <c>$Secure</c> from it.</summary>
  public static string Record9Name(this NtfsVersion version) => version.HasCentralisedSecurity() ? "$Secure" : "$Quota";
}
