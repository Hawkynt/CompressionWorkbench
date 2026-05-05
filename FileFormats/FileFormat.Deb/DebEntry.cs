namespace FileFormat.Deb;

/// <summary>
/// Represents a file entry extracted from a Debian package's data archive.
/// </summary>
/// <param name="Path">The file path within the package.</param>
/// <param name="Data">The file contents, or empty for directories.</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
public sealed record DebEntry(string Path, byte[] Data, bool IsDirectory);
