namespace Compression.Registry;

/// <summary>
/// Inputs to <see cref="IFilesystemPlaceable.PlaceFileAt" /> — which owner goes
/// where.
/// </summary>
/// <remarks>
/// A placement needs a target, and a target is not something a defragmentation
/// mode can carry: <see cref="DefragOptions" /> has no field that means "this
/// file, at this offset", and giving it one would make every other mode's
/// options record answer a question it has no business answering. So the verb
/// has its own options record, the same way it has its own interface.
/// </remarks>
public sealed record class PlacementOptions {

  /// <summary>
  /// The owner to place, as the extent map names it. Matched
  /// case-insensitively; an owner the volume does not hold is refused before
  /// anything moves.
  /// </summary>
  public required string FileName { get; init; }

  /// <summary>
  /// Byte offset its first block has to end up at. Has to name a real cluster
  /// boundary inside the data area; a target between boundaries, outside the
  /// volume, or inside a reserved region is refused rather than rounded, since
  /// rounding would report a placement that did not happen.
  /// </summary>
  public required long TargetOffset { get; init; }

  /// <summary>
  /// Bytes the pass may hold in memory while a block whose destination is still
  /// occupied waits for it to clear.
  /// </summary>
  /// <remarks>
  /// Evicting what is in the way frequently sends it to the space the owner is
  /// vacating, and on a volume with nothing spare those two wait on each other.
  /// One of them has to leave the volume for that to unwind.
  /// </remarks>
  public long StagingMemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;

  /// <summary>
  /// Optional progress callback, emitting the same snapshots and read/write-head
  /// updates a defragmentation does, so the maintenance block map animates the
  /// placement as it happens.
  /// </summary>
  public Action<DefragProgressEvent>? OnProgress { get; init; }

  /// <summary>Cooperative cancellation, honoured at the next safe move boundary.</summary>
  public CancellationToken CancellationToken { get; init; }
}
