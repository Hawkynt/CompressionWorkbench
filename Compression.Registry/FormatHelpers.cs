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
    using var target = CreateEntryFile(baseDir, entryName);
    target.Write(data, 0, data.Length);
  }

  /// <summary>
  /// Opens the destination file for <paramref name="entryName"/> under
  /// <paramref name="baseDir"/>, applying the same traversal sanitising as
  /// <see cref="WriteFile" />. Lets a caller stream an entry straight to disk
  /// instead of materialising it, which an entry larger than a byte[] requires.
  /// </summary>
  public static FileStream CreateEntryFile(string baseDir, string entryName) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    var fullPath = Path.Combine(baseDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);
    return File.Create(fullPath);
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
    => inputs.Where(i => !i.IsDirectory).Select(i => (i.ArchiveName, i.ReadContent()));

  /// <summary>
  /// Flattens all entries to root level (filename only) with their data.
  /// For formats without path support.
  /// </summary>
  public static IEnumerable<(string Name, byte[] Data)> FlatFiles(IReadOnlyList<ArchiveInputInfo> inputs)
    => inputs.Where(i => !i.IsDirectory).Select(i => (Path.GetFileName(i.ArchiveName), i.ReadContent()));

  /// <summary>
  /// The method name to hand a writer that reads its own effort tier out of the
  /// name, with the "+" run the caller asked for restored.
  /// </summary>
  /// <remarks>
  /// <see cref="FormatCreateOptions.MethodName"/> carries the base name because
  /// that is what a writer switching on a codec name needs to match, and the "+"
  /// run travels beside it in <see cref="FormatCreateOptions.OptimizeLevel"/>. A
  /// writer that parses the name with <see cref="MethodNameParser"/> instead wants
  /// them back together, and putting them back together here is what keeps the two
  /// conventions from disagreeing about the same request.
  /// </remarks>
  public static string? MethodWithEffort(FormatCreateOptions options, string? method = null) {
    ArgumentNullException.ThrowIfNull(options);
    var requested = method ?? options.MethodName;
    if (string.IsNullOrWhiteSpace(requested)) return requested;
    var (baseMethod, plus) = MethodNameParser.Parse(requested);
    var level = Math.Max(plus, options.OptimizeLevel);
    return level > 0 ? baseMethod + new string('+', level) : baseMethod;
  }
}
