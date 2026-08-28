#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can zero-fill all unused bytes in an image
/// or archive — free clusters/sectors, cluster-tip slack, deleted directory
/// entries, padding regions, and dead archive bytes. This is a forensic-cleanliness
/// tool ensuring no deleted file remnants survive.
///
/// <para>The default implementation is deliberately conservative. It is available
/// only when the same descriptor exposes an exact filesystem extent map or archive
/// layout map; unknown/undecoded regions must therefore be emitted as
/// <see cref="DefragBlockKind.MetadataReserved"/> by those maps rather than omitted.
/// An empty map is treated as "cannot prove anything is free" and wipes nothing.</para>
/// </summary>
public interface IWipeEmpty {
  /// <summary>
  /// Zeros all bytes in <paramref name="image"/> that are not part of any live
  /// file or required metadata. Returns the total number of bytes wiped.
  /// </summary>
  /// <param name="image">The image or archive stream. Must be readable, writable,
  /// and seekable.</param>
  /// <param name="wipeClusterTips">When true, also zero the tail of cluster-aligned
  /// extents where the actual file size is smaller than the allocated extent.</param>
  /// <param name="wipeDeletedEntries">When true, zero deleted directory entries
  /// and any other recoverable remnants beyond simple free-space gaps. The generic
  /// implementation can only wipe gaps/tips; format-specific implementations may
  /// additionally scrub deleted metadata records.</param>
  /// <returns>The number of bytes that were overwritten with zeros.</returns>
  long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Wipe requires a readable, writable, seekable stream.", nameof(image));

    List<DefragBlockInfo> extents = this switch {
      IFilesystemExtentMap fs => fs.EnumerateExtents(image).ToList(),
      IArchiveLayoutMap archive => archive.EnumerateLayout(image).ToList(),
      _ => throw new NotSupportedException(
        "The generic wipe requires IFilesystemExtentMap or IArchiveLayoutMap."),
    };

    // Fail closed. A parser that cannot prove even one region exists must never
    // turn "unknown image" into "everything is free".
    if (extents.Count == 0)
      return 0;

    Func<string, long>? sizeLookup = null;
    if (wipeClusterTips && this is IArchiveFormatOperations ops) {
      try {
        image.Position = 0;
        var sizes = ops.List(image, null)
          .Where(e => !e.IsDirectory)
          .GroupBy(e => e.Name, StringComparer.Ordinal)
          .ToDictionary(g => g.Key, g => Math.Max(0L, g.First().OriginalSize), StringComparer.Ordinal);
        sizeLookup = name => sizes.TryGetValue(name, out var size) ? size : -1;
      } catch {
        // Lack of a trustworthy logical-size table merely disables tip wiping;
        // it must not disable proven whole free gaps.
        sizeLookup = null;
        wipeClusterTips = false;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, sizeLookup);
  }
}
