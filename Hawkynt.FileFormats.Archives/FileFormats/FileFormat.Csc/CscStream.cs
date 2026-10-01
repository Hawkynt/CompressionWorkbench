using Compression.Core.Dictionary.Csc;

namespace FileFormat.Csc;

/// <summary>
/// CSC: the stream format of Fu Siyuan's libcsc (https://github.com/fusiyuan2010/CSC),
/// as written by its <c>csc c</c> tool — a 10-byte big-endian property header (window size, compressed
/// block size, raw block size) followed by tagged range-coder and bit-coder blocks. See
/// <see cref="CscCodec"/> for the codec.
/// </summary>
public static class CscStream {
  /// <summary>Compresses with the reference tool's defaults (level 2, 64,000,000-byte window shrunk to the input).</summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">Receives the CSC stream.</param>
  public static void Compress(Stream input, Stream output) => CscCodec.Compress(input, output);

  /// <summary>Compresses with explicit encoder options.</summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">Receives the CSC stream.</param>
  /// <param name="options">Level, window and filter switches, as the reference tool's <c>-m</c>, <c>-d</c> and <c>-f*0</c>.</param>
  public static void Compress(Stream input, Stream output, CscEncoderOptions options) => CscCodec.Compress(input, output, options);

  /// <summary>Decompresses a CSC stream.</summary>
  /// <param name="input">The CSC stream.</param>
  /// <param name="output">Receives the original data.</param>
  /// <exception cref="InvalidDataException">The stream is truncated or corrupt.</exception>
  public static void Decompress(Stream input, Stream output) => CscCodec.Decompress(input, output);
}
