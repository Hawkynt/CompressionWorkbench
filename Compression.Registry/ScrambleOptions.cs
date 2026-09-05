namespace Compression.Registry;

/// <summary>
/// Inputs to <see cref="IFilesystemScrambleable.Scramble" /> — the deliberate
/// opposite of a defragmentation.
/// </summary>
/// <remarks>
/// A scramble is not a layout strategy a defragmentation may be asked for. It
/// has its own options record for the same reason it has its own interface: the
/// only way to reach it is to name it.
/// </remarks>
public sealed record class ScrambleOptions {

  /// <summary>
  /// Seeds the shuffle. The same seed over the same volume deals the same
  /// layout, every run and every machine — which is what makes a scrambled
  /// volume usable as a test fixture and as a screenshot.
  /// </summary>
  public int Seed { get; init; } = 1;

  /// <summary>
  /// Bytes the pass may hold in memory while a block whose destination is still
  /// occupied waits for it to clear.
  /// </summary>
  /// <remarks>
  /// A shuffle is a permutation, and a permutation has cycles: somewhere in the
  /// volume a block's destination holds a block whose own destination holds the
  /// first. One of them has to leave the volume for the cycle to unwind. On a
  /// volume with space to spare the pass hops through a spare block instead and
  /// this is never reached.
  /// </remarks>
  public long StagingMemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;

  /// <summary>
  /// Optional progress callback, emitting the same snapshots and read/write-head
  /// updates a defragmentation does, so the maintenance block map animates the
  /// scattering as it happens.
  /// </summary>
  public Action<DefragProgressEvent>? OnProgress { get; init; }

  /// <summary>Cooperative cancellation, honoured at the next safe block boundary.</summary>
  public CancellationToken CancellationToken { get; init; }
}
