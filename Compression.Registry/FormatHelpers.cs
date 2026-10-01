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
    safeName = string.Join('/', safeName.Split('/').Select(static s => HostFileName(s, OperatingSystem.IsWindows())));
    var fullPath = Path.Combine(baseDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);
    return File.Create(fullPath);
  }

  private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase) {
    "CON", "PRN", "AUX", "NUL",
    "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
  };

  /// <summary>
  /// <paramref name="name"/> as one path component the host can create: every character it
  /// cannot hold becomes '_'. Everywhere that is U+0000; on Windows also the control
  /// characters and <c>&lt; &gt; : " | ? *</c>, a trailing dot or blank (which Windows would
  /// silently drop) and a device name such as <c>CON</c> (which it would open instead). An
  /// entry name that is legal in its own file system — HFS+'s <c>forward:slash</c>, a
  /// metadata name with U+0000 or a carriage return in it — therefore extracts under a
  /// recognisable name instead of failing the extraction or, for ':' on NTFS, landing in an
  /// alternate data stream.
  /// </summary>
  internal static string HostFileName(string name, bool windows) {
    if (name is "" or "." or "..") return name;
    var chars = name.ToCharArray();
    for (var i = 0; i < chars.Length; ++i)
      if (chars[i] == '\0' || (windows && (chars[i] < ' ' || chars[i] is '<' or '>' or ':' or '"' or '|' or '?' or '*')))
        chars[i] = '_';
    if (windows) {
      for (var i = chars.Length - 1; i >= 0 && chars[i] is '.' or ' '; --i)
        chars[i] = '_';
      var result = new string(chars);
      return WindowsDeviceNames.Contains(result.Split('.')[0].TrimEnd()) ? "_" + result : result;
    }
    return new string(chars);
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
