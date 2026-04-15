#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzs;

namespace FileFormat.Lzs;

/// <summary>
/// LZS stream format: 4-byte magic header followed by LZS building block output
/// (4-byte LE uncompressed size + compressed bitstream).
/// </summary>
public static class LzsStream {

  private static readonly byte[] Magic = [0x1F, 0x9D, 0x8C, 0x53];

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    output.Write(Magic);

    var bb = new LzsBuildingBlock();
    var compressed = bb.Compress(data);
    output.Write(compressed);
  }

  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
      throw new InvalidDataException("Not an LZS stream (bad magic).");

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var compressed = ms.ToArray();

    var bb = new LzsBuildingBlock();
    var decompressed = bb.Decompress(compressed);
    output.Write(decompressed);
  }
}
