namespace Compression.Lib;

/// <summary>
/// A resolved input for archive creation: a file or directory with its archive-relative name.
/// </summary>
/// <param name="FullPath">Absolute path on disk (empty for synthetic directory entries).</param>
/// <param name="EntryName">Name inside the archive (forward slashes, directories end with '/').</param>
internal readonly record struct ArchiveInput(string FullPath, string EntryName) {
  internal bool IsDirectory => EntryName.EndsWith('/');

  /// <summary>
  /// Resolves a mix of files, directories, and wildcards into archive inputs.
  /// Directories are recursed; their contents preserve relative paths.
  /// </summary>
  internal static List<ArchiveInput> Resolve(string[] inputs) {
    var result = new List<ArchiveInput>();
    foreach (var input in inputs) {
      if (input.Contains('*') || input.Contains('?')) {
        var dir = Path.GetDirectoryName(input);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var pattern = Path.GetFileName(input);
        foreach (var match in Directory.GetFiles(dir, pattern))
          result.Add(new(Path.GetFullPath(match), Path.GetFileName(match)));
      }
      else if (Directory.Exists(input)) {
        AddDirectory(result, input);
      }
      else if (File.Exists(input)) {
        result.Add(new(Path.GetFullPath(input), Path.GetFileName(input)));
      }
      else {
        throw new FileNotFoundException($"Not found: {input}");
      }
    }
    return result;
  }

  private static void AddDirectory(List<ArchiveInput> result, string dirPath) {
    // Use the directory name itself as the root inside the archive
    var baseName = Path.GetFileName(Path.GetFullPath(dirPath));
    var parentDir = Path.GetDirectoryName(Path.GetFullPath(dirPath)) ?? ".";

    // Add the directory entry itself
    result.Add(new("", baseName + "/"));

    // Add all subdirectories
    foreach (var sub in Directory.GetDirectories(dirPath, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(parentDir, sub).Replace('\\', '/');
      result.Add(new("", relative + "/"));
    }

    // Add all files
    foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(parentDir, file).Replace('\\', '/');
      result.Add(new(Path.GetFullPath(file), relative));
    }
  }

  /// <summary>
  /// Returns only file entries (non-directories) with their data.
  /// For formats that don't support directory entries.
  /// </summary>
  internal static IEnumerable<(string EntryName, byte[] Data)> FilesOnly(IReadOnlyList<ArchiveInput> inputs)
    => inputs.Where(i => !i.IsDirectory).Select(i => (i.EntryName, File.ReadAllBytes(i.FullPath)));

  /// <summary>
  /// Flattens all entries to root level (filename only).
  /// For formats without path support.
  /// </summary>
  internal static IEnumerable<(string EntryName, byte[] Data)> FlatFiles(IReadOnlyList<ArchiveInput> inputs)
    => inputs.Where(i => !i.IsDirectory).Select(i => (Path.GetFileName(i.EntryName), File.ReadAllBytes(i.FullPath)));
}
