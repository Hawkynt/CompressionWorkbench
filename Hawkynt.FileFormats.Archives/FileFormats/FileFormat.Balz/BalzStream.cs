using Compression.Core.Dictionary.Balz;

namespace FileFormat.Balz;

/// <summary>
/// <c>.balz</c> files as Ilya Muravyov's BALZ 1.20 writes and reads them; the codec is
/// <see cref="BalzCodec"/>.
/// </summary>
public static class BalzStream {

  /// <summary>Compresses <paramref name="input"/> as <c>balz c</c> does.</summary>
  /// <param name="input">The bytes to compress.</param>
  /// <param name="output">Receives the <c>.balz</c> file.</param>
  public static void Compress(Stream input, Stream output) => output.Write(BalzCodec.Compress(ReadAll(input)));

  /// <summary>
  /// Compresses <paramref name="input"/> in the reference's maximum mode (<c>balz cx</c>) and
  /// keeps that file only when it is smaller than the normal one; both are BALZ files.
  /// </summary>
  /// <param name="input">The bytes to compress.</param>
  /// <param name="output">Receives the <c>.balz</c> file.</param>
  public static void CompressOptimal(Stream input, Stream output) {
    var data = ReadAll(input);
    var normal = BalzCodec.Compress(data);
    var maximum = BalzCodec.Compress(data, maximum: true);
    output.Write(maximum.Length < normal.Length ? maximum : normal);
  }

  /// <summary>Decompresses a <c>.balz</c> file.</summary>
  /// <param name="input">The <c>.balz</c> file.</param>
  /// <param name="output">Receives the decompressed bytes.</param>
  /// <exception cref="InvalidDataException">The input is not a valid BALZ file.</exception>
  public static void Decompress(Stream input, Stream output) => output.Write(BalzCodec.Decompress(ReadAll(input)));

  private static byte[] ReadAll(Stream input) {
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    return buffer.ToArray();
  }
}
