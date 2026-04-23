#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Temporal Noise Shaping (TNS) inverse filter per ISO/IEC 14496-3 §4.6.9.
/// TNS applies a per-band linear-prediction filter in the frequency domain to
/// shape quantisation noise across time, used for transient signals in AAC-LC.
/// </summary>
internal static class AacTns {

  /// <summary>Reads the TNS data block (n_filt, length, order, direction, compressed coefs).</summary>
  public static TnsData Decode(AacBitReader reader, int windowSequence) {
    _ = reader; _ = windowSequence;
    throw new NotSupportedException(
      "AAC TNS decoding not yet implemented. ISO/IEC 14496-3 §4.6.9 specifies " +
      "per-window n_filt + length + order + direction + 3- or 4-bit signed coefficients " +
      "expanded via inverse-quantisation table c[] = sin(2π·k/(2·denom)).");
  }

  /// <summary>Applies in-place TNS inverse filtering to spectral coefficients.</summary>
  public static void Apply(float[] spectrum, in TnsData data) {
    _ = spectrum; _ = data;
    throw new NotSupportedException("AAC TNS inverse filter not yet implemented.");
  }
}

internal readonly record struct TnsData(int NumFilters);
