namespace Compression.Registry;

/// <summary>
/// Opt-in capability for physical or logical re-layout. Native mutable filesystems
/// may move extents in place; WORM/archive containers can satisfy the same verb by
/// building a verified staged target and committing it after completion.
/// </summary>
public interface IArchiveDefragmentable {
  /// <summary>
  /// Defragments using the format's default consolidate-at-start strategy.
  /// Generic list/extract/create descriptors use the verified staged rebuild.
  /// </summary>
  void Defragment(Stream archive) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new NotSupportedException(
        "The default Defragment requires IArchiveFormatOperations + IArchiveCreatable.");
    RebuildVerb.RebuildInPlace(archive, ops, creator);
  }

  /// <summary>
  /// Rewrites according to <paramref name="options"/>. Descriptors with their own
  /// native parameterless mover retain it. Descriptors relying on the interface
  /// default are routed through the progress-reporting, cancellable staged rebuild,
  /// so archive repacks and WORM re-layouts drive the same block-map UI as physical
  /// filesystem extent moves.
  /// </summary>
  void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException(
        $"This descriptor only supports DefragMode.ConsolidateAtStart; got {options.Mode}.");

    // Preserve a concrete native parameterless implementation when one exists.
    // Generic promoted descriptors have no such method and therefore get the
    // staged rebuild below, including block-map progress and safe cancellation.
    var native = this.GetType().GetMethod(nameof(Defragment), [typeof(Stream)]);
    if (native != null && native.DeclaringType != typeof(IArchiveDefragmentable)) {
      options.CancellationToken.ThrowIfCancellationRequested();
      this.Defragment(archive);
      return;
    }

    if (this is IArchiveFormatOperations ops && this is IArchiveCreatable creator) {
      RebuildVerb.RebuildInPlace(archive, ops, creator,
        onProgress: options.OnProgress,
        cancellationToken: options.CancellationToken);
      return;
    }

    this.Defragment(archive);
  }
}
