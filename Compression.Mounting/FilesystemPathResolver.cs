using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Resolves the path-oriented names supplied by kernel mount APIs onto the
/// stable node-id model exposed by <see cref="IFilesystemSession"/>.
/// </summary>
public static class FilesystemPathResolver {
  private static readonly char[] PathSeparators = ['/', '\\'];

  /// <summary>
  /// Resolves a mount-relative path to a stable filesystem node id. Both Unix
  /// and Windows separators are accepted so backend adapters share identical
  /// traversal semantics. Parent traversal is rejected instead of escaping the
  /// mounted root.
  /// </summary>
  public static bool TryResolve(
    IFilesystemSession filesystem,
    string path,
    out FilesystemNodeId nodeId
  ) {
    ArgumentNullException.ThrowIfNull(filesystem);
    ArgumentNullException.ThrowIfNull(path);

    nodeId = filesystem.RootNodeId;
    var segments = GetSegments(path);
    if (segments is null)
      return false;

    for (var i = 0; i < segments.Length; ++i) {
      if (filesystem.Stat(nodeId).Kind != FilesystemNodeKind.Directory)
        return false;

      var child = filesystem.Lookup(nodeId, segments[i]);
      if (child is null)
        return false;

      nodeId = child.Value;
    }

    return true;
  }

  /// <summary>
  /// Resolves the containing directory and leaf name for a namespace mutation
  /// such as create, delete, or rename. Root has no parent inside the mount and
  /// therefore cannot be resolved by this method.
  /// </summary>
  public static bool TryResolveParent(
    IFilesystemSession filesystem,
    string path,
    out FilesystemNodeId parentDirectory,
    out string name
  ) {
    ArgumentNullException.ThrowIfNull(filesystem);
    ArgumentNullException.ThrowIfNull(path);

    parentDirectory = filesystem.RootNodeId;
    name = string.Empty;

    var segments = GetSegments(path);
    if (segments is not { Length: > 0 })
      return false;

    for (var i = 0; i < segments.Length - 1; ++i) {
      if (filesystem.Stat(parentDirectory).Kind != FilesystemNodeKind.Directory)
        return false;

      var child = filesystem.Lookup(parentDirectory, segments[i]);
      if (child is null)
        return false;

      parentDirectory = child.Value;
    }

    if (filesystem.Stat(parentDirectory).Kind != FilesystemNodeKind.Directory)
      return false;

    name = segments[^1];
    return true;
  }

  private static string[]? GetSegments(string path) {
    var rawSegments = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
    if (rawSegments.Length == 0)
      return [];

    var segments = new List<string>(rawSegments.Length);
    foreach (var segment in rawSegments) {
      if (segment == ".")
        continue;
      if (segment == "..")
        return null;
      segments.Add(segment);
    }

    return [.. segments];
  }
}
