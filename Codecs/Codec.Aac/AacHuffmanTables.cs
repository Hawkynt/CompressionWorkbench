#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC Huffman codebook metadata and decode helpers per ISO/IEC 14496-3 §4.6.3.
/// The bulk numeric codeword/length data lives in the generated partial half of
/// this class (AacHuffmanCodebooks.cs); this file holds the per-codebook properties
/// (dimension, sign convention, largest-absolute-value) and the bit-serial
/// Huffman lookup used by the spectral decoder.
/// </summary>
internal static partial class AacHuffmanTables {

  /// <summary>Number of spectral codebooks (1..11). Index 0 is "ZERO_HCB" (no data).</summary>
  public const int SpectralCodebookCount = 11;

  /// <summary>The codebook index that signals all-zero spectrum (no bits read).</summary>
  public const int ZeroHcb = 0;

  /// <summary>NOISE_HCB (perceptual noise substitution).</summary>
  public const int NoiseHcb = 13;

  /// <summary>IS_INTENSITY (intensity stereo) and its sign-inverted partner.</summary>
  public const int IntensityHcb2 = 14;
  public const int IntensityHcb = 15;

  /// <summary>The escape codebook (values |x| ≥ 16 carry an escape sequence).</summary>
  public const int EscapeHcb = 11;

  /// <summary>
  /// Dimension (number of quantised coefficients per codeword): 4 for the quad
  /// codebooks 1, 2, 5, 6; 2 for the pair codebooks 3, 4, 7, 8, 9, 10, 11.
  /// Value at index <c>i</c> corresponds to codebook <c>i</c>.
  /// </summary>
  public static readonly int[] Dimensions = [
    0, // cb 0 unused
    4, // cb 1
    4, // cb 2
    2, // cb 3
    2, // cb 4
    4, // cb 5
    4, // cb 6
    2, // cb 7
    2, // cb 8
    2, // cb 9
    2, // cb 10
    2, // cb 11 (escape codebook)
  ];

  /// <summary>
  /// Codebooks 3, 4, 7, 8, 11 are unsigned: codeword values are magnitudes and a
  /// sign bit follows the codeword for each non-zero coefficient
  /// (ISO/IEC 14496-3, <c>IS_CODEBOOK_UNSIGNED(x) = ((x-1) &amp; 10)</c>).
  /// </summary>
  public static readonly bool[] Unsigned = [
    false, // 0
    false, // 1
    false, // 2
    true,  // 3
    true,  // 4
    false, // 5
    false, // 6
    true,  // 7
    true,  // 8
    false, // 9
    false, // 10
    true,  // 11
  ];

  /// <summary>LAV (largest absolute value) per codebook per ISO/IEC 14496-3 Table 4.98.</summary>
  public static readonly int[] Lav = [
    0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16,
  ];

  /// <summary>
  /// Reads one Huffman codeword from <paramref name="reader"/> using spectral
  /// codebook <paramref name="codebook"/> (1..11) and returns its index into the
  /// codebook's value enumeration. Bit-serial longest-prefix match: AAC codes are
  /// a prefix code so the first length at which (accumulated bits == stored code)
  /// is unique.
  /// </summary>
  public static int DecodeSpectralIndex(AacBitReader reader, int codebook) {
    var codes = SpectralCodes[codebook - 1];
    var bits = SpectralBits[codebook - 1];
    uint acc = 0;
    var len = 0;
    while (len < 20) {
      acc = (acc << 1) | reader.ReadBits(1);
      ++len;
      for (var i = 0; i < codes.Length; ++i)
        if (bits[i] == len && codes[i] == acc)
          return i;
    }
    throw new InvalidDataException($"AAC: no Huffman match in codebook {codebook} after {len} bits.");
  }

  /// <summary>
  /// Reads one differential scale-factor codeword (HCB_SF) and returns the signed
  /// delta in [-60, 60] (the codebook index minus the 60 mid-point bias).
  /// </summary>
  public static int DecodeScaleFactorDelta(AacBitReader reader) {
    uint acc = 0;
    var len = 0;
    while (len < 20) {
      acc = (acc << 1) | reader.ReadBits(1);
      ++len;
      for (var i = 0; i < ScaleFactorCodes.Length; ++i)
        if (ScaleFactorBits[i] == len && ScaleFactorCodes[i] == acc)
          return i - 60;
    }
    throw new InvalidDataException($"AAC: no HCB_SF Huffman match after {len} bits.");
  }
}
