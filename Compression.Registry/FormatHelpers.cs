namespace Compression.Registry;

/// <summary>
/// Shared utility methods for format descriptors (path sanitization, filtering, etc.).
/// </summary>
public static class FormatHelpers {

  /// <summary>
  /// Sanitizes an entry name and writes its data to disk under <paramref name="baseDir"/>.
  /// Prevents path traversal attacks.
  /// </summary>
  public static void WriteFile(string baseDir, string entryName, byte[] data) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    var fullPath = Path.Combine(baseDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);
    File.WriteAllBytes(fullPath, data);
  }

  /// <summary>
  /// Returns true if <paramref name="name"/> matches any of the <paramref name="filters"/>
  /// by exact name, trailing path segment, or filename-only comparison.
  /// </summary>
  public static bool MatchesFilter(string name, string[] filters)
    => filters.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(name).Equals(f, StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Returns only file entries (non-directories) with their data, preserving paths.
  /// </summary>
  public static IEnumerable<(string Name, byte[] Data)> FilesOnly(IReadOnlyList<ArchiveInputInfo> inputs)
    => inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, File.ReadAllBytes(i.FullPath)));

  /// <summary>
  /// Flattens all entries to root level (filename only) with their data.
  /// For formats without path support.
  /// </summary>
  public static IEnumerable<(string Name, byte[] Data)> FlatFiles(IReadOnlyList<ArchiveInputInfo> inputs)
    => inputs.Where(i => !i.IsDirectory).Select(i => (Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath)));
}
