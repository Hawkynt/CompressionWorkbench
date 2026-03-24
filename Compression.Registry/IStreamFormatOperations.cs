namespace Compression.Registry;

/// <summary>
/// Operations for single-stream compression formats.
/// </summary>
public interface IStreamFormatOperations {
  /// <summary>Decompress the input stream to the output stream.</summary>
  void Decompress(Stream input, Stream output);

  /// <summary>Compress the input stream to the output stream.</summary>
  void Compress(Stream input, Stream output);

  /// <summary>Compress with maximum/optimal settings. Defaults to <see cref="Compress"/>.</summary>
  void CompressOptimal(Stream input, Stream output) => Compress(input, output);

  /// <summary>
  /// Returns a decompression wrapper stream, or null if the format doesn't support wrapping.
  /// Used for compound tar formats where the tar reader needs to read through the decompressor.
  /// </summary>
  Stream? WrapDecompress(Stream input) => null;

  /// <summary>
  /// Returns a compression wrapper stream, or null if the format doesn't support wrapping.
  /// </summary>
  Stream? WrapCompress(Stream output) => null;
}
