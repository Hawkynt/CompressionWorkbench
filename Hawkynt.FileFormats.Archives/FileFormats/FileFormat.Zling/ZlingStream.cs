using Compression.Core.Dictionary.Zling;

namespace FileFormat.Zling;

/// <summary>
/// <c>.zling</c> files as Zhang Li's libzling demo (<c>zling e0</c>..<c>e4</c>, <c>zling d</c>)
/// writes and reads them; the codec is <see cref="ZlingCodec"/>.
/// </summary>
public static class ZlingStream {

  /// <summary>Compresses <paramref name="input"/> at level 4, the deepest search.</summary>
  /// <param name="input">The bytes to compress.</param>
  /// <param name="output">Receives the zling stream.</param>
  public static void Compress(Stream input, Stream output) => Compress(input, output, ZlingCodec.MaxLevel);

  /// <summary>
  /// Compresses <paramref name="input"/> at a libzling level from 0 (fastest) to 4 (deepest
  /// search, more lazy matching).
  /// </summary>
  /// <param name="input">The bytes to compress.</param>
  /// <param name="output">Receives the zling stream.</param>
  /// <param name="level">The level, 0 to 4.</param>
  public static void Compress(Stream input, Stream output, int level) {
    ArgumentOutOfRangeException.ThrowIfNegative(level);
    if (level > ZlingCodec.MaxLevel)
      throw new ArgumentOutOfRangeException(nameof(level), level, "Zling compression level must be in the range 0..4.");
    output.Write(ZlingCodec.Compress(ReadAll(input), level));
  }

  /// <summary>Decompresses a zling stream.</summary>
  /// <param name="input">The zling stream.</param>
  /// <param name="output">Receives the decompressed bytes.</param>
  /// <exception cref="InvalidDataException">The input is not a valid zling stream.</exception>
  public static void Decompress(Stream input, Stream output) => output.Write(ZlingCodec.Decompress(ReadAll(input)));

  private static byte[] ReadAll(Stream input) {
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    return buffer.ToArray();
  }
}
