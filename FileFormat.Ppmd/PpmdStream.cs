#pragma warning disable CS1591
using Compression.Core.Dictionary.Ppm;

namespace FileFormat.Ppmd;

/// <summary>
/// PPMd stream container format.
/// Layout: 4-byte magic (0x8F 0xAF 0xAC 0x84), then the raw output of
/// <see cref="PpmBuildingBlock"/> (which includes its own 1-byte order + 4-byte LE size header).
/// </summary>
public static class PpmdStream {

  private static readonly byte[] Magic = [0x8F, 0xAF, 0xAC, 0x84];

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    output.Write(Magic);

    var bb = new PpmBuildingBlock();
    var compressed = bb.Compress(data);
    output.Write(compressed);
  }

  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
      throw new InvalidDataException("Not a PPMd stream (bad magic).");

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var remaining = ms.ToArray();

    var bb = new PpmBuildingBlock();
    var decompressed = bb.Decompress(remaining);
    output.Write(decompressed);
  }
}
