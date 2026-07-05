namespace Compression.Registry;

/// <summary>
/// Resolves the correct <em>target size</em> of symbolic links inside a single
/// filesystem listing — the headline "show the pointed-to file's size, not the
/// link's own size" behaviour.
/// <para>
/// Given a complete <see cref="ArchiveEntryInfo"/> listing produced by one
/// filesystem, each link's <see cref="ArchiveEntryInfo.LinkTarget"/> is resolved
/// RELATIVE to the directory that holds the link, against the other entries in the
/// same listing, following link chains up to <see cref="MaxHops"/> hops with a
/// cycle guard. When the chain ends at a regular file that is present in the
/// listing, that file's <see cref="ArchiveEntryInfo.OriginalSize"/> is written back
/// as the link's <see cref="ArchiveEntryInfo.TargetSize"/>.
/// </para>
/// <para>
/// Policy — <see cref="ArchiveEntryInfo.TargetSize"/> is deliberately left
/// <c>null</c> (unknown) in every case where the answer cannot be proven from the
/// listing alone: an absolute target (leading <c>/</c> or a drive-letter prefix),
/// a target that escapes the volume root, a target that is not present in the
/// listing (dangling, or pointing outside this filesystem), a target that resolves
/// to a directory, and any cyclic or over-long (&gt; <see cref="MaxHops"/>) chain.
/// Only relative links to a regular file inside the same filesystem yield a size.
/// The link's own <see cref="ArchiveEntryInfo.OriginalSize"/> is never altered — it
/// stays the on-disk target-path byte length.
/// </para>
/// <para>
/// Path matching is ordinal (case-sensitive), matching the dominant Unix
/// filesystem behaviour of the readers this serves (ext/UFS/SquashFS/EROFS); a
/// case-insensitive volume simply resolves fewer links, never wrong ones.
/// </para>
/// </summary>
public static class SymlinkResolver {
  /// <summary>Maximum number of symlink hops followed before a chain is abandoned as too long.</summary>
  public const int MaxHops = 40;

  /// <summary>
  /// Returns a new listing in which every relative symlink that resolves to a
  /// regular file within the same listing has its
  /// <see cref="ArchiveEntryInfo.TargetSize"/> filled in. Non-link entries and
  /// unresolvable links are returned unchanged.
  /// </summary>
  /// <param name="entries">The full listing of one filesystem.</param>
  /// <returns>A listing of the same order with resolved target sizes.</returns>
  public static List<ArchiveEntryInfo> Resolve(List<ArchiveEntryInfo> entries) {
    ArgumentNullException.ThrowIfNull(entries);

    var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < entries.Count; i++)
      byPath[NormalizeKey(entries[i].Name)] = i;

    var result = new List<ArchiveEntryInfo>(entries.Count);
    foreach (var e in entries) {
      if (!e.IsSymlink || string.IsNullOrEmpty(e.LinkTarget)) {
        result.Add(e);
        continue;
      }
      var size = ResolveTargetSize(e, entries, byPath);
      result.Add(size.HasValue ? e with { TargetSize = size } : e);
    }
    return result;
  }

  private static long? ResolveTargetSize(
      ArchiveEntryInfo link, List<ArchiveEntryInfo> entries, Dictionary<string, int> byPath) {
    var current = link;
    var visited = new HashSet<string>(StringComparer.Ordinal);

    for (var hop = 0; hop < MaxHops; hop++) {
      var target = current.LinkTarget;
      if (string.IsNullOrEmpty(target)) return null;

      // Absolute targets (leading '/' or a "X:" drive prefix) are outside the
      // relative-to-this-listing contract — leave the size unknown.
      if (target[0] is '/' or '\\') return null;
      if (target.Length >= 2 && target[1] == ':') return null;

      var linkDir = ParentDir(NormalizeKey(current.Name));
      var resolved = Combine(linkDir, target);
      if (resolved is null) return null;              // escaped the volume root
      if (!visited.Add(resolved)) return null;        // cycle
      if (!byPath.TryGetValue(resolved, out var idx)) return null; // dangling / outside listing

      var tgt = entries[idx];
      if (tgt.IsSymlink) { current = tgt; continue; } // follow the chain
      if (tgt.IsDirectory) return null;               // directory target has no file size
      return tgt.OriginalSize;
    }
    return null; // chain longer than MaxHops
  }

  // Cleans an entry name into a canonical lookup key: backslashes to slashes,
  // no leading slash, "." / ".." / empty segments collapsed.
  private static string NormalizeKey(string name) {
    var combined = Combine("", name);
    return combined ?? "";
  }

  private static string ParentDir(string path) {
    var slash = path.LastIndexOf('/');
    return slash < 0 ? "" : path[..slash];
  }

  // Joins a base directory with a (possibly dotted) relative path and returns the
  // normalized slash-separated result, or null when the path climbs above root.
  private static string? Combine(string baseDir, string relative) {
    var stack = new List<string>();
    if (baseDir.Length > 0)
      foreach (var seg in baseDir.Replace('\\', '/').Split('/'))
        if (seg.Length > 0)
          stack.Add(seg);

    foreach (var seg in relative.Replace('\\', '/').Split('/')) {
      if (seg.Length == 0 || seg == ".") continue;
      if (seg == "..") {
        if (stack.Count == 0) return null; // escapes the volume root
        stack.RemoveAt(stack.Count - 1);
        continue;
      }
      stack.Add(seg);
    }
    return string.Join('/', stack);
  }
}
