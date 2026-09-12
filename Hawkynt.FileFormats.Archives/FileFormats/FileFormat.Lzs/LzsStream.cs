#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzs;

namespace FileFormat.Lzs;

/// <summary>
/// LZS stream format: 4-byte magic header followed by LZS building block output
/// (4-byte LE uncompressed size + RFC 2395 compressed bitstream).
/// </summary>
public static class LzsStream {

  private static readonly byte[] Magic = [0x1F, 0x9D, 0x8C, 0x53];

  /// <summary>Encodes the supplied input with the balanced encoder.</summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, LzsCompressionLevel.Balanced);

  /// <summary>Encodes the supplied input with the requested encoder effort.</summary>
  public static void Compress(Stream input, Stream output, LzsCompressionLevel level) {
    var data = ReadAll(input);
    output.Write(Magic);
    output.Write(new LzsBuildingBlock().Compress(data, level));
  }

  /// <summary>
  /// Tries every managed LZS parsing effort and writes the smallest complete stream.
  /// This hard comparison makes optimization non-regressing even when a deeper greedy
  /// search changes token boundaries unfavourably for a particular input.
  /// </summary>
  public static void CompressOptimal(Stream input, Stream output) {
    var data = ReadAll(input);
    var codec = new LzsBuildingBlock();
    byte[]? best = null;

    foreach (var level in Enum.GetValues<LzsCompressionLevel>()) {
      var candidate = codec.Compress(data, level);
      if (best is null || candidate.Length < best.Length)
        best = candidate;
    }

    output.Write(Magic);
    output.Write(best!);
  }

  /// <summary>Decodes the supplied input.</summary>
  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (!magicBuf.SequenceEqual(Magic))
      throw new InvalidDataException("Not an LZS stream (bad magic).");

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    output.Write(new LzsBuildingBlock().Decompress(ms.ToArray()));
  }

  private static byte[] ReadAll(Stream input) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ms.ToArray();
  }
}
