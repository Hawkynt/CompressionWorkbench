#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzham;

namespace FileFormat.Lzham;

/// <summary>
/// LZHAM stream format: 4-byte magic "LZHM" followed by the raw LZHAM building block output
/// (4-byte LE original size + compressed data).
/// </summary>
public static class LzhamStream {

  private static readonly byte[] Magic = [0x4C, 0x5A, 0x48, 0x4D]; // "LZHM"

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    output.Write(Magic);

    var bb = new LzhamBuildingBlock();
    var compressed = bb.Compress(data);
    output.Write(compressed);
  }

  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
      throw new InvalidDataException("Not an LZHAM stream (bad magic).");

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var payload = ms.ToArray();

    var bb = new LzhamBuildingBlock();
    var decompressed = bb.Decompress(payload);
    output.Write(decompressed);
  }
}
