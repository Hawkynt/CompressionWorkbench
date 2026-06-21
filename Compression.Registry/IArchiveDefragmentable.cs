namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can rewrite an archive in place so that every file
/// occupies a contiguous cluster run, optionally with a chosen layout strategy
/// (consolidate at start / end, lazy hole-fill, carve a free region). Complements the
/// allocator's automatic fast-defrag (which fires only when a pending allocation can't
/// find a contiguous hole); this is the user-initiated full pass.
/// </summary>
public interface IArchiveDefragmentable {
  /// <summary>
  /// Rebuilds the archive content in place so every file is contiguous. Outer byte size
  /// is preserved. Free space is consolidated at the end.
  ///
  /// <para><b>Default implementation</b>: any descriptor that also implements
  /// <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveCreatable"/> gets
  /// defragmentation for free — a verified in-place extract → re-create rebuild via
  /// <see cref="RebuildVerb.RebuildInPlace"/> (the rebuild-via-WORM pattern inherently
  /// lays every file out contiguously) that refuses to commit a lossy result. Formats
  /// with a true in-place block mover override this for efficiency and full mode support.</para>
  /// </summary>
  void Defragment(Stream archive) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new System.NotSupportedException(
        "The default Defragment requires the descriptor to also implement IArchiveFormatOperations + IArchiveCreatable.");
    RebuildVerb.RebuildInPlace(archive, ops, creator);
  }

  /// <summary>
  /// Rewrites the archive content according to <paramref name="options"/>. Default
  /// implementation forwards to <see cref="Defragment(System.IO.Stream)"/> for
  /// <see cref="DefragMode.ConsolidateAtStart"/> and throws for every other mode —
  /// implementers should override to support all modes their on-disk format permits.
  /// </summary>
  /// <exception cref="System.NotSupportedException">If the implementer doesn't support
  /// the requested mode (default for any mode other than
  /// <see cref="DefragMode.ConsolidateAtStart"/>).</exception>
  void Defragment(Stream archive, DefragOptions options) {
    System.ArgumentNullException.ThrowIfNull(options);
    if (options.Mode == DefragMode.ConsolidateAtStart) {
      this.Defragment(archive);
      return;
    }
    throw new System.NotSupportedException(
      $"This descriptor only supports DefragMode.ConsolidateAtStart; got {options.Mode}.");
  }
}
