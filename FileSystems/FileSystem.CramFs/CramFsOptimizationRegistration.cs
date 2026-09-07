using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileSystem.CramFs;

/// <summary>
/// Writer-specific optimization hooks for CramFS. Kept beside the writer because
/// symbolic-link inode semantics belong to the filesystem, not the generic registry.
/// </summary>
internal static class CramFsOptimizationRegistration {
  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterSymbolicLinkDeduplicator<CramFsFormatDescriptor>(RebuildWithSymlinks);

  private static void RebuildWithSymlinks(
    ILayoutOptimizable _,
    Stream source,
    Stream target,
    LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    if (options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("CramFS symbolic-link deduplication cannot be combined with sparse/hard-link transforms.");

    source.Position = 0;
    using var reader = new CramFsReader(source);
    var sourceRegular = reader.Entries
      .Where(e => e.IsRegularFile)
      .ToDictionary(e => e.FullPath, e => reader.Extract(e), StringComparer.Ordinal);

    using (var writer = new CramFsWriter(target, leaveOpen: true)) {
      foreach (var directory in reader.Entries
                 .Where(e => e.IsDirectory && e.FullPath != "/")
                 .OrderBy(e => Depth(e.FullPath)))
        writer.AddDirectory(directory.FullPath);

      var firstByContent = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
        if (entry.IsSymlink) {
          writer.AddSymlink(entry.FullPath, Encoding.UTF8.GetString(reader.Extract(entry)));
          continue;
        }
        if (!entry.IsRegularFile) continue;

        var data = sourceRegular[entry.FullPath];
        var key = ContentKey(data);
        if (firstByContent.TryGetValue(key, out var canonical))
          writer.AddSymlink(entry.FullPath, RelativeTarget(entry.FullPath, canonical));
        else {
          firstByContent[key] = entry.FullPath;
          writer.AddFile(entry.FullPath, data);
        }
      }
    }

    // The optimization is allowed to change inode type, not observable file bytes.
    // Resolve the emitted links ourselves because the generic CramFS descriptor's
    // extraction API intentionally exposes raw symlink payloads.
    target.Position = 0;
    using var rebuilt = new CramFsReader(target);
    var rebuiltEntries = rebuilt.Entries.ToDictionary(e => e.FullPath, StringComparer.Ordinal);
    foreach (var (path, expected) in sourceRegular) {
      var actual = ReadLogicalFile(rebuilt, rebuiltEntries, path, new HashSet<string>(StringComparer.Ordinal));
      if (!actual.AsSpan().SequenceEqual(expected))
        throw new InvalidOperationException($"CramFS symbolic-link deduplication changed '{path}'.");
    }
  }

  private static byte[] ReadLogicalFile(
    CramFsReader reader,
    IReadOnlyDictionary<string, CramFsEntry> entries,
    string path,
    HashSet<string> visiting) {
    path = NormalizeAbsolute(path);
    if (!visiting.Add(path))
      throw new InvalidDataException($"CramFS symbolic-link cycle at '{path}'.");
    if (!entries.TryGetValue(path, out var entry))
      throw new InvalidDataException($"CramFS symbolic link resolves to missing '{path}'.");

    try {
      if (entry.IsRegularFile) return reader.Extract(entry);
      if (!entry.IsSymlink)
        throw new InvalidDataException($"CramFS path '{path}' does not resolve to a regular file.");

      var rawTarget = Encoding.UTF8.GetString(reader.Extract(entry));
      var resolved = rawTarget.StartsWith('/')
        ? NormalizeAbsolute(rawTarget)
        : NormalizeAbsolute(Parent(path) + "/" + rawTarget);
      return ReadLogicalFile(reader, entries, resolved, visiting);
    } finally {
      visiting.Remove(path);
    }
  }

  private static string ContentKey(byte[] data)
    => $"{data.Length}:{Convert.ToHexString(SHA256.HashData(data))}";

  private static string RelativeTarget(string linkPath, string targetPath) {
    var from = Segments(Parent(NormalizeAbsolute(linkPath)));
    var to = Segments(NormalizeAbsolute(targetPath));
    var common = 0;
    while (common < from.Length && common < to.Length
           && string.Equals(from[common], to[common], StringComparison.Ordinal))
      ++common;

    var parts = new List<string>(from.Length - common + to.Length - common);
    for (var i = common; i < from.Length; ++i) parts.Add("..");
    for (var i = common; i < to.Length; ++i) parts.Add(to[i]);
    return parts.Count == 0 ? "." : string.Join('/', parts);
  }

  private static int Depth(string path) => Segments(path).Length;

  private static string Parent(string path) {
    var slash = path.LastIndexOf('/');
    return slash <= 0 ? "/" : path[..slash];
  }

  private static string[] Segments(string path)
    => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

  private static string NormalizeAbsolute(string path) {
    var result = new List<string>();
    foreach (var segment in Segments(path.Replace('\\', '/'))) {
      switch (segment) {
        case ".":
          break;
        case "..":
          if (result.Count == 0)
            throw new InvalidDataException($"CramFS link escapes the filesystem root: '{path}'.");
          result.RemoveAt(result.Count - 1);
          break;
        default:
          result.Add(segment);
          break;
      }
    }
    return "/" + string.Join('/', result);
  }
}
