namespace FileFormat.InnoSetup;

/// <summary>
/// Represents a file or directory entry listed in an Inno Setup installer.
/// </summary>
/// <param name="FileName">The source filename or a generated name when parsing fails.</param>
/// <param name="DestDir">The destination directory string from the installer header, or empty.</param>
/// <param name="Size">The uncompressed file size in bytes, or -1 when unknown.</param>
/// <param name="CompressedSize">The compressed size in bytes, or -1 when unknown.</param>
/// <param name="IsDirectory">Whether this entry represents a directory rather than a file.</param>
public sealed record InnoSetupEntry(
  string FileName,
  string DestDir,
  long Size,
  long CompressedSize,
  bool IsDirectory
);
