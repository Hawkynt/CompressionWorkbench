#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Spectral data decoding: reads Huffman-coded quantised coefficients per
/// ISO/IEC 14496-3 §4.6.3, inverse-quantises them (<c>x^(4/3)</c>), and rescales
/// them by the per-scalefactor-band gain factors.
/// </summary>
internal static class AacSpectral {

  /// <summary>
  /// Decodes the spectral coefficients for a single individual_channel_stream.
  /// Throws <see cref="NotSupportedException"/> until the Huffman codebooks in
  /// <see cref="AacHuffmanTables"/> are populated.
  /// </summary>
  public static float[] DecodeSpectralData(AacBitReader reader, IcsInfo ics, int[] codebooksPerSfb) {
    _ = reader; _ = ics; _ = codebooksPerSfb;
    throw new NotSupportedException(
      "AAC spectral decoding requires the 11 Huffman codebooks + HCB_SF from " +
      "ISO/IEC 14496-3 §4.A. Profile parsing and ADTS framing are supported; " +
      "the spectral pipeline is not yet implemented. See AacHuffmanTables for TODO markers.");
  }
}

/// <summary>
/// Per-channel individual_channel_stream parameters (ICS info) per
/// ISO/IEC 14496-3 §4.5.2.3. Fixed-point values rather than bitstream positions.
/// </summary>
internal readonly record struct IcsInfo(
  int WindowSequence,          // 0=ONLY_LONG, 1=LONG_START, 2=EIGHT_SHORT, 3=LONG_STOP
  int WindowShape,             // 0=sine, 1=KBD
  int MaxSfb,
  int ScaleFactorGrouping,
  int[] SectionData,
  int[] ScaleFactors);
