namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can rewrite an archive in place so that every file
/// occupies a contiguous cluster run, without changing the outer image size. Complements
/// the allocator's automatic fast-defrag (which fires only when a pending allocation can't
/// find a contiguous hole); this is the user-initiated full pass.
/// </summary>
public interface IArchiveDefragmentable {
  /// <summary>
  /// Rebuilds the archive content in place so every file is contiguous. Outer byte size is
  /// preserved. Free space is consolidated at the end.
  /// </summary>
  void Defragment(Stream archive);
}
