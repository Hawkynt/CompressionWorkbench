namespace FileFormat.InnoSetup;

/// <summary>
/// A file an Inno Setup installer installs.
/// </summary>
/// <param name="FileName">
/// The destination path with Inno constants spelled as directories, as innoextract writes it:
/// <c>{app}\bin\x.dll</c> becomes <c>app/bin/x.dll</c>.
/// </param>
/// <param name="Size">The uncompressed size in bytes.</param>
/// <param name="LastModified">
/// The file time the compiler recorded, or null: UTC, except in 16-bit (1.2.x) installers, which
/// record a DOS date and time without a zone (<see cref="DateTimeKind.Unspecified"/>).
/// </param>
/// <param name="DataIndex">Index of the file location (data entry) holding the bytes.</param>
public sealed record InnoSetupEntry(string FileName, long Size, DateTime? LastModified, int DataIndex) {
  /// <summary>Gets whether the entry is a directory; Inno [Files] entries never are.</summary>
  public bool IsDirectory => false;
}
