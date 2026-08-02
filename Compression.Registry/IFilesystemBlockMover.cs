#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability for filesystems that support true in-place defragmentation
/// via cluster-level moves. Implementing this interface allows the planner-driven
/// defrag path to move extents without rebuilding the entire image.
///
/// <para><see cref="MoveExtent"/> performs the raw byte copy from source to
/// destination within the image. <see cref="UpdateAllocationAfterMove(Stream, string, long, long, long)"/> patches
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
  /// Size of one allocation unit — a cluster, a block — in bytes, or zero when
  /// the mover does not say.
  /// </summary>
  /// <remarks>
  /// <see cref="UpdateAllocationScattered" /> is documented as taking one entry
  /// per allocation block, and a caller that does not know the size can only
  /// hand over one entry per contiguous run. A relink that reads those as
  /// blocks writes a chain as long as the run count — a seventy-cluster file
  /// arriving as one run became a one-cluster file. Stating the size lets the
  /// caller expand runs into the blocks the contract promises.
  /// </remarks>
  int AllocationBlockSize => 0;

  /// <summary>
  /// Whether this mover can relink an owner's <em>whole</em> allocation in one
  /// call. A fragmented file's runs have to become a single chain;
  /// <see cref="UpdateAllocationAfterMove(Stream, string, long, long, long)" />, called once per run, can only
  /// describe each run as a file of its own — which truncates the file to its
  /// last run. A mover that returns false is never asked to move a fragmented
  /// owner: the caller falls back to a rebuild instead.
  /// </summary>
  bool SupportsScatteredRelink => false;

  /// <summary>
  /// Whether <see cref="UpdateAllocationAfterMove(Stream, string, long, long, long)" /> repoints exactly the run
  /// it is told about and leaves the owner's other runs alone.
  /// </summary>
  /// <remarks>
  /// <para>A mover that finds an owner's record by name and rewrites the one
  /// field naming where the owner starts cannot do this: called once per run it
  /// would describe each run as the whole file, and the file would end up as
  /// its last run. That is what the fragmented-owner refusal protects.</para>
  ///
  /// <para>A mover that finds the record by the run's own address — the extent
  /// descriptor naming that block, the pointer holding it — is a different
  /// thing entirely: it rewrites that run and nothing else, so a fragmented
  /// owner is simply several such calls. Saying so is what lets a fragmented
  /// volume be laid out in place instead of written out again.</para>
  /// </remarks>
  bool RepointsRunsIndependently => false;

  /// <summary>
  /// Whether this mover copes with a run being held outside the volume while
  /// the rest of the layout moves.
  /// </summary>
  /// <remarks>
  /// <para>It is what makes a full volume rearrangeable: with no free region to
  /// park a run in, the only way round a cycle is to lift one out. Two things
  /// have to hold for that to be safe. The run's old space is given up the
  /// moment it is lifted, so putting it down again must not release that space
  /// a second time — see the overload taking
  /// <c>releaseOldSpace</c>. And while it is held, its record still names where
  /// it was, which something else may have moved into, so finding the record by
  /// that address alone can find the wrong one.</para>
  ///
  /// <para>A mover says so only once both are true of it and the layout has
  /// been driven through every mode to check. Everything else keeps to what the
  /// volume itself can offer, and falls back to a rebuild when that runs out.</para>
  /// </remarks>
  bool SupportsHeldRuns => false;

  /// <summary>
  /// Repoints a run the way <see cref="UpdateAllocationAfterMove(Stream, string, long, long, long)" /> does, but
  /// says whether the space it came from should be released.
  /// </summary>
  /// <remarks>
  /// A run that was held outside the volume while the rest moved left its old
  /// space behind at that moment, and something else has very likely taken it
  /// since. Releasing it when the run is finally put down would hand out space
  /// another owner is living in. Movers that keep an allocation bitmap override
  /// this; the rest do not care, and the default forwards.
  /// </remarks>
  void UpdateAllocationAfterMove(Stream image, string fileName,
      long oldOffset, long newOffset, long length, bool releaseOldSpace)
    => this.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);

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
