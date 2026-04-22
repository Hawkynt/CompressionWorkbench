namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can extract a single named entry straight to a
/// <see cref="Stream"/> without materialising it to disk. Used by the recursive-descent
/// driver to avoid per-layer temp-dir roundtrips.
/// </summary>
public interface IArchiveInMemoryExtract {
  /// <summary>
  /// Writes the bytes of <paramref name="entryName"/> (as named by <see cref="IArchiveFormatOperations.List"/>)
  /// into <paramref name="output"/>.
  /// </summary>
  void ExtractEntry(Stream input, string entryName, Stream output, string? password);
}
