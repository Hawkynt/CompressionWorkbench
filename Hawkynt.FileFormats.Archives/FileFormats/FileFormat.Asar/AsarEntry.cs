#pragma warning disable CS1591
namespace FileFormat.Asar;

/// <summary>
/// One node in an Electron <c>.asar</c> archive: either a file (with a byte
/// range relative to the end of the header) or a directory. Paths use forward
/// slashes and are relative to the archive root.
/// </summary>
public sealed record AsarEntry(
  string Path,
  long Offset,
  long Size,
  bool Executable,
  bool IsDirectory
);
