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
  /// is preserved. Free space is consolidated at the end. Equivalent to calling
  /// <see cref="Defragment(System.IO.Stream, DefragOptions)"/> with
  /// <c>new DefragOptions { Mode = DefragMode.ConsolidateAtStart }</c>.
  /// </summary>
  void Defragment(Stream archive);

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
