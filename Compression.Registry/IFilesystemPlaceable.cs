namespace Compression.Registry;

/// <summary>
/// Opt-in capability for putting one named owner at one chosen offset, moving
/// whatever is in the way out of the way first.
/// </summary>
/// <remarks>
/// <para>This is deliberately not a <see cref="DefragMode" />. A placement takes
/// two things no defragmentation takes — which owner, and where — and a mode
/// that reads them out of fields the other modes ignore is an operation
/// reachable by a mis-set enum value on a method whose name means something
/// else. Reaching this needs the caller to name the verb and the descriptor to
/// say it can do it.</para>
///
/// <para>It is also not <see cref="DefragMode.CarveHole" /> with an extra
/// argument, although it shares that mode's eviction: carving leaves a region
/// empty and placement fills it, and a caller that asked for one and got the
/// other would have no way to tell from the return type.</para>
///
/// <para>There is no default implementation and no rebuild fallback. A rebuild
/// lays the volume out in whatever order it walks the directory, which will not
/// be the order that was asked for; a descriptor that cannot place in place
/// refuses instead, naming what stopped it.</para>
/// </remarks>
public interface IFilesystemPlaceable {

  /// <summary>
  /// Puts <see cref="PlacementOptions.FileName" /> at
  /// <see cref="PlacementOptions.TargetOffset" />, relocating every live extent
  /// that is in the way. Content is preserved exactly; only where it lives
  /// changes.
  /// </summary>
  /// <remarks>
  /// The owner comes out contiguous where the volume allows it. Where a
  /// reserved table or a bad block sits inside the span it is split around
  /// that, and the promise that survives is the weaker one: every block of the
  /// owner sits above the block before it, so a sequential read never seeks
  /// backwards.
  /// </remarks>
  /// <exception cref="InvalidOperationException">The request cannot be honoured
  /// — no such owner, a target outside the volume or off the cluster grid, not
  /// enough room from the target upward, nowhere to put what is in the way, or
  /// a mover that cannot relink a split owner. Nothing is changed.</exception>
  void PlaceFileAt(Stream image, PlacementOptions options);
}
