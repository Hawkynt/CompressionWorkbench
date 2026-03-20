namespace FileFormat.Deb;

/// <summary>
/// Constants for the Debian package (.deb) file format.
/// </summary>
public static class DebConstants {
  /// <summary>Expected content of the debian-binary member.</summary>
  public const string VersionString = "2.0\n";

  /// <summary>Name of the version member in the ar archive.</summary>
  public const string VersionMemberName = "debian-binary";

  /// <summary>Prefix for the control archive member name.</summary>
  public const string ControlPrefix = "control.tar";

  /// <summary>Prefix for the data archive member name.</summary>
  public const string DataPrefix = "data.tar";
}
