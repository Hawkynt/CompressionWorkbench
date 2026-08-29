#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Per-channel individual_channel_stream parameters (ICS info) per
/// ISO/IEC 14496-3 §4.5.2.3, fully resolved into scale-factor-band geometry.
/// </summary>
internal sealed class IcsInfo {

  /// <summary>0=ONLY_LONG, 1=LONG_START, 2=EIGHT_SHORT, 3=LONG_STOP.</summary>
  public int WindowSequence;

  /// <summary>0=sine, 1=KBD.</summary>
  public int WindowShape;

  /// <summary>Highest coded scale-factor band (+1 = count of coded sfbs).</summary>
  public int MaxSfb;

  /// <summary>EIGHT_SHORT grouping bitmask (7 bits, one per short-window boundary).</summary>
  public int ScaleFactorGrouping;

  /// <summary>Number of window groups (1 for long windows, 1..8 for short).</summary>
  public int WindowGroupCount;

  /// <summary>Windows per group, indexed by group.</summary>
  public int[] WindowGroupLength = [1];

  /// <summary>True for the EIGHT_SHORT (transient) sequence.</summary>
  public bool IsEightShort => this.WindowSequence == 2;

  /// <summary>
  /// SWB boundary offsets within a single window (long: 0..1024; short: 0..128).
  /// </summary>
  public int[] SwbOffset = [];

  /// <summary>Number of scale-factor bands per window for the active sequence.</summary>
  public int NumSwb;
}

/// <summary>
/// Spectral data decoding: reads Huffman-coded quantised coefficients per
/// ISO/IEC 14496-3 §4.6.3, then inverse-quantises (<c>sign·|x|^(4/3)</c>) and
/// applies the per-band scale-factor gain <c>2^((sf-100)/4)</c>.
/// </summary>
internal static class AacSpectral {

  /// <summary>
  /// Reconstructs the quantised integer coefficients (2 or 4, by codebook
  /// dimension) encoded by codeword <paramref name="index"/>. The standard enumerates the
  /// codewords as a fixed positional number system: base <c>2·LAV+1</c> centred on
  /// zero for signed codebooks, base <c>LAV+1</c> (magnitudes) for unsigned ones.
  /// </summary>
  public static void IndexToCoefficients(int index, int codebook, Span<int> values) {
    var dim = AacHuffmanTables.Dimensions[codebook];
    var lav = AacHuffmanTables.Lav[codebook];
    var unsigned = AacHuffmanTables.Unsigned[codebook];
    var baseN = unsigned ? lav + 1 : 2 * lav + 1;
    for (var d = 0; d < dim; ++d) {
      var div = 1;
      for (var p = 0; p < dim - 1 - d; ++p)
        div *= baseN;
      var digit = (index / div) % baseN;
      values[d] = unsigned ? digit : digit - lav;
    }
  }

  /// <summary>
  /// Decodes the spectral coefficients for one individual_channel_stream into a
  /// 1024-length quantised-integer buffer. Scale-factor application happens later
  /// (after PNS/IS resolution) in <see cref="Dequantize"/>.
  /// </summary>
  public static int[] DecodeQuantizedSpectrum(AacBitReader reader, IcsInfo ics, int[][] sfbCodebooks) {
    var coeffs = new int[AacFilterBank.LongFrameSize];
    var values = new int[4];

    var groupWindowStart = 0;
    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      var windowsInGroup = ics.WindowGroupLength[g];
      var groupBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
      // Per ISO §4.6.3 short-window spectral layout is interleaved by sfb across
      // the windows of a group, so we decode sfb-by-sfb and scatter into windows.
      for (var sfb = 0; sfb < ics.MaxSfb; ++sfb) {
        var cb = sfbCodebooks[g][sfb];
        if (cb is AacHuffmanTables.ZeroHcb or AacHuffmanTables.NoiseHcb
            or AacHuffmanTables.IntensityHcb or AacHuffmanTables.IntensityHcb2)
          continue; // no Huffman-coded coefficients for these codebooks

        var dim = AacHuffmanTables.Dimensions[cb];
        var unsigned = AacHuffmanTables.Unsigned[cb];
        var sfbStart = ics.SwbOffset[sfb];
        var sfbEnd = ics.SwbOffset[sfb + 1];
        var width = sfbEnd - sfbStart;

        for (var w = 0; w < windowsInGroup; ++w) {
          var baseBin = (groupWindowStart + w) * groupBins + sfbStart;
          for (var k = 0; k < width; k += dim) {
            var idx = AacHuffmanTables.DecodeSpectralIndex(reader, cb);
            IndexToCoefficients(idx, cb, values);

            // The sign bits for the whole tuple come first and the escape sequences after
            // them, not one sign/escape pair per value. Only the ESC codebook has escapes,
            // so this is the one codebook where the difference is observable - and reading
            // them interleaved consumed a sign bit where an escape prefix was written.
            if (unsigned)
              for (var j = 0; j < dim; ++j)
                if (values[j] != 0 && reader.ReadBits(1) == 1)
                  values[j] = -values[j];

            if (cb == AacHuffmanTables.EscapeHcb)
              for (var j = 0; j < dim; ++j)
                if (values[j] is 16 or -16)
                  values[j] = ReadEscape(reader, values[j] < 0);

            for (var j = 0; j < dim; ++j)
              coeffs[baseBin + k + j] = values[j];
          }
        }
      }
      groupWindowStart += windowsInGroup;
    }
    return coeffs;
  }

  /// <summary>
  /// Reads an escape sequence for the ESC codebook (cb 11): unary-coded escape
  /// prefix length <c>N</c> followed by an <c>N</c>-bit word giving the absolute
  /// value <c>(1&lt;&lt;N) + word</c> (ISO/IEC 14496-3 §4.6.3.3).
  /// </summary>
  private static int ReadEscape(AacBitReader reader, bool negative) {
    var n = 4;
    while (reader.ReadBits(1) == 1)
      ++n;
    var word = (int)reader.ReadBits(n);
    var magnitude = (1 << n) + word;
    return negative ? -magnitude : magnitude;
  }

  /// <summary>
  /// Inverse-quantises one window group's coefficients in place: each non-zero
  /// quantised value <c>q</c> becomes <c>sign(q)·|q|^(4/3)·2^((sf-100)/4)</c>,
  /// where <c>sf</c> is the band's scale factor.
  /// </summary>
  public static void Dequantize(int[] quant, float[] outSpectrum, IcsInfo ics, int[][] scaleFactors, int[][] sfbCodebooks) {
    var groupWindowStart = 0;
    var groupBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
    for (var g = 0; g < ics.WindowGroupCount; ++g) {
      var windowsInGroup = ics.WindowGroupLength[g];
      for (var sfb = 0; sfb < ics.MaxSfb; ++sfb) {
        var cb = sfbCodebooks[g][sfb];
        if (cb is AacHuffmanTables.ZeroHcb or AacHuffmanTables.NoiseHcb
            or AacHuffmanTables.IntensityHcb or AacHuffmanTables.IntensityHcb2)
          continue;
        var gain = ScaleFactorGain(scaleFactors[g][sfb]);
        var sfbStart = ics.SwbOffset[sfb];
        var sfbEnd = ics.SwbOffset[sfb + 1];
        for (var w = 0; w < windowsInGroup; ++w) {
          var baseBin = (groupWindowStart + w) * groupBins;
          for (var k = sfbStart; k < sfbEnd; ++k) {
            var q = quant[baseBin + k];
            if (q == 0) {
              outSpectrum[baseBin + k] = 0f;
              continue;
            }
            var mag = MathF.Pow(MathF.Abs(q), 4f / 3f) * gain;
            outSpectrum[baseBin + k] = q < 0 ? -mag : mag;
          }
        }
      }
      groupWindowStart += windowsInGroup;
    }
  }

  /// <summary>Scale-factor gain factor <c>2^((sf-100)/4)</c> (ISO/IEC 14496-3 §4.6.2).</summary>
  public static float ScaleFactorGain(int scaleFactor) => MathF.Pow(2f, (scaleFactor - 100) / 4f);
}
