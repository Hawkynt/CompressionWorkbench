namespace Compression.Registry;

/// <summary>
/// Opt-in maintenance capability: rebuild a container from the very same entries —
/// dropping dead space a removal left behind and laying the live entries out again —
/// while copying every entry's stored bytes and metadata through unchanged.
/// </summary>
/// <remarks>
/// <para>This is the <c>repack</c> verb of the maintenance split
/// (<see cref="MaintenanceCapability.Repack"/>). It implies no recompression — that is
/// <see cref="ICompressionOptimizable"/> — and no change of geometry — that is
/// <see cref="ILayoutOptimizable"/>.</para>
///
/// <para>There is deliberately no default implementation. The generic extract →
/// re-create rebuild keeps names and bytes and nothing else, which is exactly the loss
/// the lossless-or-refuse rule forbids, so a format opts in only with a rewrite that
/// carries its entries over verbatim.</para>
/// </remarks>
public interface IArchiveRepackable {
  /// <summary>
  /// Writes the repacked container to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The source, readable and seekable.</param>
  /// <param name="output">The target, writable and seekable.</param>
  /// <param name="password">Password for an encrypted source, when the format needs one to rewrite.</param>
  void Repack(Stream input, Stream output, string? password = null);
}
