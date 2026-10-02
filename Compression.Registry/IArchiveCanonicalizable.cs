namespace Compression.Registry;

/// <summary>
/// Opt-in maintenance capability: rewrite a file or container into its canonical
/// representation — chunk order, padding, header fields the format fixes — without
/// re-encoding any payload and without changing what the file represents.
/// </summary>
/// <remarks>
/// This is the <c>canonicalize</c> verb of the maintenance split
/// (<see cref="MaintenanceCapability.Canonicalize"/>): MP4 fast-start, Matroska cues in
/// front, MacBinary header normalisation. It is neither compression
/// (<see cref="ICompressionOptimizable"/>) nor repacking (<see cref="IArchiveRepackable"/>).
/// A rewrite that would lose anything refuses with <see cref="NotSupportedException"/>.
/// </remarks>
public interface IArchiveCanonicalizable {
  /// <summary>
  /// Writes the canonical representation of <paramref name="input"/> to
  /// <paramref name="output"/>. When the input is already canonical the output is
  /// byte-identical to it.
  /// </summary>
  /// <param name="input">The source, readable and seekable.</param>
  /// <param name="output">The target, readable, writable and seekable.</param>
  void Canonicalize(Stream input, Stream output);
}
