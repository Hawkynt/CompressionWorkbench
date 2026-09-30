#pragma warning disable CS1591

namespace FileSystem.Refs;

/// <summary>
/// A writable path must identify exactly one namespace row. ReFS directories
/// can contain names that differ only by case, while the offline API resolves
/// paths case-insensitively; choosing either row would silently edit the wrong
/// file when the caller did not supply case-sensitive lookup semantics.
/// </summary>
internal static class RefsNamespaceLookup {
  public static RefsFileRecord? FindUnique(IEnumerable<RefsFileRecord> files, string path) {
    RefsFileRecord? match = null;
    foreach (var file in files) {
      if (!string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
      if (match != null)
        throw new NotSupportedException($"ReFS path '{path}' is ambiguous under case-insensitive lookup.");
      match = file;
    }
    return match;
  }
}
