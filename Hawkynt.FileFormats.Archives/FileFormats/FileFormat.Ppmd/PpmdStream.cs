#pragma warning disable CS1591
using Compression.Core.Dictionary.Ppm;
using Compression.Core.Entropy.Ppmd;

namespace FileFormat.Ppmd;

/// <summary>
/// PPMd stream container format.
/// Current layout: 4-byte magic (0x8F 0xAF 0xAC 0x84), 1-byte format version,
/// then the raw output of <see cref="PpmdBuildingBlock"/> (1-byte order,
/// 4-byte LE original size, range-coded data).
/// </summary>
/// <remarks>
/// Version 0 streams written by earlier CompressionWorkbench builds omitted the
/// version byte and carried the simpler order-3 <see cref="PpmBuildingBlock"/>
/// payload. They remain readable so enabling the real PPMd-H encoder does not
/// strand files already produced by the application.
/// </remarks>
public static class PpmdStream {

  private const byte CurrentVersion = 1;
  private static readonly byte[] Magic = [0x8F, 0xAF, 0xAC, 0x84];

  /// <summary>
  /// Encodes the supplied input with the default PPMd-H model order.
  /// </summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, PpmdBuildingBlock.DefaultOrder);

  /// <summary>
  /// Encodes the supplied input with the requested PPMd-H model order.
  /// </summary>
  public static void Compress(Stream input, Stream output, int order) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);

    output.Write(Magic);
    output.WriteByte(PpmdStream.CurrentVersion);

    var bb = new PpmdBuildingBlock(order);
    output.Write(bb.Compress(ms.GetBuffer().AsSpan(0, checked((int)ms.Length))));
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (!magicBuf.SequenceEqual(PpmdStream.Magic))
      throw new InvalidDataException("Not a PPMd stream (bad magic).");

    var versionOrLegacyOrder = input.ReadByte();
    if (versionOrLegacyOrder < 0)
      throw new InvalidDataException("PPMd: truncated stream header.");

    using var ms = new MemoryStream();
    if (versionOrLegacyOrder == PpmdStream.CurrentVersion) {
      input.CopyTo(ms);
      var bb = new PpmdBuildingBlock();
      output.Write(bb.Decompress(ms.GetBuffer().AsSpan(0, checked((int)ms.Length))));
      return;
    }

    // Legacy files had no version byte. Their first payload byte is the fixed
    // PPM order (3), followed by the original length and arithmetic-coded data.
    if (versionOrLegacyOrder != PpmCompressor.MaxOrder)
      throw new InvalidDataException($"PPMd: unsupported stream version {versionOrLegacyOrder}.");

    ms.WriteByte((byte)versionOrLegacyOrder);
    input.CopyTo(ms);
    var legacy = new PpmBuildingBlock();
    output.Write(legacy.Decompress(ms.GetBuffer().AsSpan(0, checked((int)ms.Length))));
  }
}
