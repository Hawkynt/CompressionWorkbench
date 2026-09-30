namespace Compression.NativeUI.Navigation;

/// <summary>One breadcrumb: what it says, and the absolute path it leads to.</summary>
internal readonly record struct PathSegment(string Label, string Path);

/// <summary>
/// Breaks a host path into breadcrumb segments, each an absolute path that can be navigated to.
/// </summary>
/// <remarks>
/// Every root is its own segment: <c>/</c> on POSIX, <c>C:\</c> for a drive, <c>\\server\share</c>
/// for a UNC share. Building crumbs by splitting on the separator gets POSIX wrong — the first crumb
/// of <c>/home/me</c> came out as the relative <c>home/</c>, which leads nowhere — so the root is
/// taken off first and the rest accumulates onto it. The platform is a parameter so both rule sets
/// are testable from either operating system.
/// </remarks>
internal static class HostPathSegments {
  /// <summary>Segments <paramref name="path"/> by this platform's rules.</summary>
  public static IReadOnlyList<PathSegment> Split(string path) => Split(path, OperatingSystem.IsWindows());

  /// <summary>Segments <paramref name="path"/> by Windows or POSIX rules.</summary>
  public static IReadOnlyList<PathSegment> Split(string path, bool windows) {
    if (string.IsNullOrWhiteSpace(path)) return [];

    var separator = windows ? '\\' : '/';
    var normalized = windows ? path.Replace('/', '\\') : path;

    var (rootLabel, rootPath, rest) = SplitRoot(normalized, windows);
    var segments = new List<PathSegment>();
    if (rootPath.Length > 0) segments.Add(new(rootLabel, rootPath));

    var accumulated = rootPath;
    foreach (var part in rest.Split(separator, StringSplitOptions.RemoveEmptyEntries)) {
      accumulated = accumulated.Length == 0
        ? part
        : accumulated.EndsWith(separator) ? accumulated + part : accumulated + separator + part;
      segments.Add(new(part, accumulated));
    }

    return segments;
  }

  private static (string Label, string Path, string Remainder) SplitRoot(string path, bool windows) {
    if (!windows)
      return path.StartsWith('/') ? ("/", "/", path[1..]) : ("", "", path);

    // \\server\share is one root: nothing above the share can be navigated to.
    if (path.StartsWith(@"\\")) {
      var parts = path[2..].Split('\\', 3, StringSplitOptions.None);
      if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0) {
        var share = $@"\\{parts[0]}\{parts[1]}";
        return (share, share, parts.Length == 3 ? parts[2] : "");
      }
    }

    // "C:" alone means the drive's current directory, so the root keeps its separator.
    if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
      return (path[..2], path[..2] + '\\', path.Length > 2 ? path[2..].TrimStart('\\') : "");

    return ("", "", path);
  }
}
