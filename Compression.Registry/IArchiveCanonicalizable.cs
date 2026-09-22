namespace Compression.Registry;

/// <summary>
/// Opt-in capability for rewriting a container into its canonical representation
/// without treating the rewrite as compression optimization or physical defragmentation.
/// </summary>
public interface IArchiveCanonicalizable {
  /// <summary>
  /// Writes a canonical representation of <paramref name="input"/> to
  /// <paramref name="output"/>, preserving all logical live content.
  /// </summary>
  void Canonicalize(Stream input, Stream output);
}
