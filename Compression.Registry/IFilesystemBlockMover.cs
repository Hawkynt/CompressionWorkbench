#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability for filesystems that support true in-place defragmentation
/// via cluster-level moves. Implementing this interface allows the planner-driven
/// defrag path to move extents without rebuilding the entire image.
///
/// <para><see cref="MoveExtent"/> performs the raw byte copy from source to
/// destination within the image. <see cref="UpdateAllocationAfterMove"/> patches
/// filesystem metadata (FAT chain entries, directory entry start-cluster, bitmap
/// bits, etc.) so the file remains reachable at its new location.</para>
/// </summary>
public interface IFilesystemBlockMover {
  /// <summary>
  /// Copies <paramref name="length"/> bytes from <paramref name="srcOffset"/>
  /// to <paramref name="dstOffset"/> within <paramref name="image"/>.
  /// Optionally zeros the source region after the copy (controlled by
  /// <paramref name="zeroSource"/>). Caller is responsible for ensuring the
  /// destination region is free.
  /// </summary>
  void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false);

  /// <summary>
  /// Patches filesystem metadata after a raw extent move. Walks the allocation
  /// structures (FAT chain, directory entries, bitmaps, etc.) to update every
  /// reference from the old cluster range to the new one.
  /// </summary>
  /// <param name="image">The filesystem image stream.</param>
  /// <param name="fileName">The file whose extent was moved (used to locate
  /// the directory entry that needs its start-cluster patched).</param>
  /// <param name="oldOffset">Byte offset of the extent before the move.</param>
  /// <param name="newOffset">Byte offset of the extent after the move.</param>
  /// <param name="length">Length of the moved extent in bytes.</param>
  void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length);

  /// <summary>
  /// Whether this mover can relink an owner's <em>whole</em> allocation in one
  /// call. A fragmented file's runs have to become a single chain;
  /// <see cref="UpdateAllocationAfterMove" />, called once per run, can only
  /// describe each run as a file of its own — which truncates the file to its
  /// last run. A mover that returns false is never asked to move a fragmented
  /// owner: the caller falls back to a rebuild instead.
  /// </summary>
  bool SupportsScatteredRelink => false;

  /// <summary>
  /// Rewrites <paramref name="fileName" />'s allocation so that it occupies
  /// <paramref name="newBlockOffsets" /> in that order, having previously
  /// occupied <paramref name="oldBlockOffsets" />. Both lists are one entry per
  /// allocation block, in the file's own order.
  /// </summary>
  /// <param name="blocksLiveElsewhere">Blocks other owners have already been
  /// relinked onto. An owner's old blocks are frequently where another owner
  /// has just landed, and freeing those would cut the other owner's chain.</param>
  void UpdateAllocationScattered(Stream image, string fileName,
      IReadOnlyList<long> oldBlockOffsets, IReadOnlyList<long> newBlockOffsets,
      IReadOnlySet<long>? blocksLiveElsewhere)
    => throw new NotSupportedException(
      $"{this.GetType().Name} cannot relink a scattered allocation.");
}
