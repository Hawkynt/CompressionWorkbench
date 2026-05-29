#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor (or a partner type) can enumerate the
/// <em>actual</em> on-disk byte layout of a filesystem image — every used
/// cluster chain per file (one <see cref="DefragBlockInfo"/> per contiguous
/// run), every metadata-reserved region (boot sector, FAT, bitmap,
/// superblock, MFT, root directory, inode table, BAM, group descriptor table,
/// etc.), and optionally every free region.
///
/// <para>Coverage may be sparse — gaps in the returned set are interpreted by
/// the caller as <see cref="DefragBlockKind.Free"/>. The yielded extents
/// don't need to be sorted; the caller is responsible for sorting + gap
/// filling. Implementations must not throw for malformed or partially-walked
/// images — they should yield whatever they can identify and return.</para>
///
/// <para>Drives the Defragment-window block-map preview so the user sees the
/// real fragmented layout before pressing "Defragment" rather than the
/// post-defrag approximation.</para>
/// </summary>
public interface IFilesystemExtentMap {
  /// <summary>
  /// Enumerates the actual on-disk layout of <paramref name="image"/>.
  /// Coverage may be sparse; callers fill the gaps with
  /// <see cref="DefragBlockKind.Free"/>. The stream's position may be
  /// modified during enumeration but the caller owns the lifetime —
  /// implementations must not dispose <paramref name="image"/>.
  /// </summary>
  /// <param name="image">The filesystem image to walk. Must be readable and
  /// seekable.</param>
  /// <returns>Zero or more contiguous regions describing the on-disk
  /// layout. Order is unspecified; lengths are in bytes; offsets are
  /// relative to the start of <paramref name="image"/>.</returns>
  IEnumerable<DefragBlockInfo> EnumerateExtents(System.IO.Stream image);
}
