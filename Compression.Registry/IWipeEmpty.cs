#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can zero-fill all unused bytes in an image
/// or archive — free clusters/sectors, cluster-tip slack, deleted directory
/// entries, padding regions, and dead archive bytes. This is a forensic-cleanliness
/// tool ensuring no deleted file remnants survive.
///
/// <para>The default implementation is deliberately conservative. It is available
/// only when the same descriptor exposes a filesystem extent map or an archive
/// layout map, and it zeroes only what that map states outright: regions marked
/// <see cref="DefragBlockKind.Free"/>, plus the cluster tips of Used extents whose
/// logical size is known. A region the map never mentions is left alone, because a
/// map is free to enumerate the entries it understands and stay silent about the
/// header, entry table or index that make the container readable at all — reading
/// that silence as free space zeroes the structure. A descriptor whose map does
/// account for the whole image, or that knows its own dead regions, overrides this
/// and calls <see cref="UnusedSpaceWiper.Wipe"/> directly.</para>
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
  /// implementation can only wipe declared-free regions and tips; format-specific
  /// implementations may additionally scrub deleted metadata records.</param>
  /// <returns>The number of bytes that were overwritten with zeros.</returns>
  long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Wipe requires a readable, writable, seekable stream.", nameof(image));

    // The layout map comes first where a descriptor has both, because the two
    // answer different questions. EnumerateLayout describes THIS stream's
    // bytes; EnumerateExtents describes a filesystem, and for a disk-image
    // container (VDI, VMDK, QCOW2, VHDX) that filesystem is the guest's, whose
    // offsets are addresses inside the virtual disk rather than inside the file
    // on disk. Zeroing guest offsets in the container file lands on whatever
    // the container happens to keep there and destroys live data.
    var fromLayoutMap = this is IArchiveLayoutMap;
    List<DefragBlockInfo> extents = this switch {
      IArchiveLayoutMap archive => archive.EnumerateLayout(image).ToList(),
      IFilesystemExtentMap fs => fs.EnumerateExtents(image).ToList(),
      _ => throw new NotSupportedException(
        "The generic wipe requires IFilesystemExtentMap or IArchiveLayoutMap."),
    };

    // A cluster tip is a filesystem idea: an allocation unit is wider than the
    // file in it, and the difference is slack nobody owns. An archive layout
    // map has no allocation units — its Used extent is the member exactly as
    // stored, framing, checksum and compression included — so "extent longer
    // than the logical size" there means the bytes are encoded, not spare, and
    // trimming to the logical size cuts into the member itself.
    if (fromLayoutMap) wipeClusterTips = false;

    // Fail closed. A parser that cannot prove even one region exists must never
    // turn "unknown image" into "everything is free".
    if (extents.Count == 0)
      return 0;

    // The map may also have failed to place anything free; that is an answer,
    // not a licence to guess, and the loop below simply writes nothing.

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
        // it must not disable the regions the map declared free.
        sizeLookup = null;
        wipeClusterTips = false;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.WipeDeclaredFree(image, extents, image.Length, wipeClusterTips, sizeLookup);
  }
}
