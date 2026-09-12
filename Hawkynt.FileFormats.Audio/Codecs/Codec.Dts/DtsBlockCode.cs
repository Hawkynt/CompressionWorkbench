#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// DCA "block code" sample unpacking for low bit-allocation indexes (abits 1..7 where no Huffman
/// table applies). A faithful port of FFmpeg's <c>decode_blockcode</c> / <c>decode_blockcodes</c>:
/// each transmitted code packs four samples as a mixed-radix number in <c>levels</c>, recovered by
/// repeated division with a mid-tread offset of <c>(levels-1)/2</c>. Two codes together yield the
/// eight samples of one subband/sub-subframe. A non-zero return signals a corrupt (out-of-range)
/// code, exactly as the reference's residual check does.
/// </summary>
public static class DtsBlockCode {

  /// <summary>abits 1..7 → bits per transmitted block code (FFmpeg <c>abits_sizes</c>).</summary>
  public static readonly int[] Sizes = [7, 10, 12, 13, 15, 17, 19];

  /// <summary>abits 1..7 → number of quantization levels (FFmpeg <c>abits_levels</c>).</summary>
  public static readonly int[] Levels = [3, 5, 7, 9, 13, 17, 25];

  /// <summary>Unpacks one code into four samples at <paramref name="dst"/>[<paramref name="dstStart"/>..+4); returns the residual.</summary>
  public static int DecodeBlockCode(int code, int levels, int[] dst, int dstStart) {
    var offset = (levels - 1) >> 1;
    for (var i = 0; i < 4; ++i) {
      var div = code / levels;
      dst[dstStart + i] = code - offset - div * levels;
      code = div;
    }
    return code;
  }

  /// <summary>Unpacks two codes into eight samples at <paramref name="dst"/>[<paramref name="dstStart"/>..+8); returns the combined residual (0 = valid).</summary>
  public static int DecodeBlockCodes(int code1, int code2, int levels, int[] dst, int dstStart)
    => DecodeBlockCode(code1, levels, dst, dstStart)
       | DecodeBlockCode(code2, levels, dst, dstStart + 4);
}
