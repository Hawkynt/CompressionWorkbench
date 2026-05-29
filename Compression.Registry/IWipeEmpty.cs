#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can zero-fill all unused bytes in an image
/// or archive — free clusters/sectors, cluster-tip slack, deleted directory
/// entries, padding regions, and dead archive bytes. This is a forensic-cleanliness
/// tool ensuring no deleted file remnants survive.
///
/// <para>Implementations that don't need format-specific logic can delegate to
/// <see cref="UnusedSpaceWiper.Wipe"/> which works generically with any
/// <see cref="IFilesystemExtentMap"/> or <see cref="IArchiveLayoutMap"/>.</para>
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
  /// and any other recoverable remnants beyond simple free-space gaps.</param>
  /// <returns>The number of bytes that were overwritten with zeros.</returns>
  long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true);
}
