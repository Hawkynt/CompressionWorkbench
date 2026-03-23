using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Arc;

/// <summary>
/// ARC Crunch methods 5-7: LZW variants with 9-12 bit codes.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Method 5 (old crunched): LZW 9-12 bit with RLE pre-pass, uses clear code.</item>
///   <item>Method 6 (new crunched): LZW 9-12 bit, no clear code, no RLE.</item>
///   <item>Method 7 (crunched7): LZW 9-12 bit with clear code, no RLE.</item>
/// </list>
/// </remarks>
internal static class ArcCrunch {
  /// <summary>
  /// Decodes method 5 (old crunched): RLE + LZW 9-12 bit.
  /// </summary>
  public static byte[] DecodeCrunched5(byte[] compressed, int originalSize) {
    // First decompress LZW, then undo RLE
    var lzwDecoded = DecodeLzw(compressed, useClearCode: true);
    return ArcRle.Decode(lzwDecoded);
  }

  /// <summary>
  /// Decodes method 6 (new crunched): LZW 9-12 bit, no clear code.
  /// </summary>
  public static byte[] DecodeCrunched6(byte[] compressed, int originalSize) =>
    DecodeLzw(compressed, useClearCode: false);

  /// <summary>
  /// Decodes method 7 (crunched7): LZW 9-12 bit with clear code.
  /// </summary>
  public static byte[] DecodeCrunched7(byte[] compressed, int originalSize) =>
    DecodeLzw(compressed, useClearCode: true);

  private static byte[] DecodeLzw(byte[] compressed, bool useClearCode) {
    using var ms = new MemoryStream(compressed);
    var decoder = new LzwDecoder(
      ms,
      minBits: ArcConstants.LzwMinBits,
      maxBits: ArcConstants.LzwMaxBits,
      useClearCode: useClearCode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    return decoder.Decode();
  }
}
