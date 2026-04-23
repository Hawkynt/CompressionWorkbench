#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC Huffman codebook tables per ISO/IEC 14496-3 §4.6.3. The 11 spectral
/// codebooks (1..11) plus the scale-factor codebook (HCB_SF) are declared here.
/// <para>
/// NOTE: This is a placeholder table set. Shipping the full 1700-entry set of
/// numeric tables is required before <see cref="AacSpectral.DecodeSpectralData"/>
/// can produce audio. Until those tables are ported verbatim from the spec (not
/// from GPL sources), the decoder gates on AAC-LC profile detection and raises
/// <see cref="NotSupportedException"/> at the spectral-decode step.
/// </para>
/// </summary>
internal static class AacHuffmanTables {

  /// <summary>Number of spectral codebooks (1..11). Index 0 is "unused".</summary>
  public const int SpectralCodebookCount = 11;

  /// <summary>
  /// Dimension (2 for pair codebooks 3, 4, 9, 10, 11; 4 for quad codebooks 1, 2, 5, 6, 7, 8).
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

  /// <summary>Codebooks 3, 5, 7, 9, 11 are unsigned (sign bits follow the codeword).</summary>
  public static readonly bool[] Unsigned = [
    false, false, false, true, false, true, false, true, false, true, false, true,
  ];

  /// <summary>LAV (largest absolute value) per codebook per ISO/IEC 14496-3 Table 4.98.</summary>
  public static readonly int[] Lav = [
    0, 1, 1, 2, 2, 4, 4, 7, 7, 12, 12, 16,
  ];

  // --------- Scale factor codebook (HCB_SF) ---------
  // TODO: port the 241-entry scale factor codebook from ISO/IEC 14496-3 §4.A.1.
  // Left as an empty placeholder pending real spec tables.

  // --------- Spectral codebooks 1..11 ---------
  // TODO: port the 11 spectral Huffman codebooks verbatim from ISO/IEC 14496-3
  //       Tables 4.A.2.1 .. 4.A.2.11. The escape codebook (11) encodes values in
  //       range [-16, 16] with escape sequences for |x| >= 16.
}
