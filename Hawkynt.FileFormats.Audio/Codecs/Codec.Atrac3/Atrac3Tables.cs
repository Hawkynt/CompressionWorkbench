#pragma warning disable CS1591
namespace Codec.Atrac3;

/// <summary>
/// Constant tables ported verbatim from FFmpeg's <c>libavcodec/atrac3data.h</c> and the
/// runtime table generators in <c>libavcodec/atrac.c</c> (<c>ff_atrac_generate_tables</c> /
/// <c>ff_atrac_init_gain_compensation</c>). These pin the decoder's spectral reconstruction,
/// inverse QMF synthesis and gain compensation exactly to the reference.
/// </summary>
internal static class Atrac3Tables {

  /// <summary>clc_length_tab[8] — constant-length-coding word widths per VLC selector.</summary>
  public static readonly int[] ClcLengthTab = [0, 4, 3, 3, 4, 4, 5, 6];

  /// <summary>mantissa_clc_tab[4] — 2-bit code → signed mantissa (selector 1, CLC).</summary>
  public static readonly int[] MantissaClcTab = [0, 1, -2, -1];

  /// <summary>mantissa_vlc_tab[18] — Huffman symbol → signed mantissa pair (selector 1, VLC).</summary>
  public static readonly int[] MantissaVlcTab = [
    0, 0, 0, 1, 0, -1, 1, 0, -1, 0, 1, 1, 1, -1, -1, 1, -1, -1,
  ];

  /// <summary>inv_max_quant[8] — inverse quantization step per VLC selector.</summary>
  public static readonly double[] InvMaxQuant = [
    0.0, 1.0 / 1.5, 1.0 / 2.5, 1.0 / 3.5, 1.0 / 4.5, 1.0 / 7.5, 1.0 / 15.5, 1.0 / 31.5,
  ];

  /// <summary>subband_tab[33] — coefficient boundaries of the 32 coding subbands.</summary>
  public static readonly int[] SubbandTab = [
    0, 8, 16, 24, 32, 40, 48, 56,
    64, 80, 96, 112, 128, 144, 160, 176,
    192, 224, 256, 288, 320, 352, 384, 416,
    448, 480, 512, 576, 640, 704, 768, 896,
    1024,
  ];

  /// <summary>matrix_coeffs[8] — joint-stereo matrixing weights (left/right pairs).</summary>
  public static readonly double[] MatrixCoeffs = [0.0, 2.0, 2.0, 2.0, 0.0, 0.0, 1.0, 1.0];

  /// <summary>huff_tab_sizes[7] — entry count of each spectral-coefficient Huffman table.</summary>
  public static readonly int[] HuffTabSizes = [9, 5, 7, 9, 15, 31, 63];

  /// <summary>
  /// atrac3_hufftabs[][2] — the seven spectral-coefficient Huffman tables as
  /// (symbol, bit-length) pairs, concatenated. FFmpeg builds the VLCs with
  /// <c>ff_vlc_init_from_lengths</c> assigning canonical codes in listed order and a
  /// symbol offset of <c>-31</c>, so the decoded value is <c>symbol - 31</c>.
  /// </summary>
  public static readonly (int Symbol, int Bits)[] HuffTabs = [
    // Spectral coefficient 1 — 9 entries
    (31, 1), (32, 3), (33, 3), (34, 4), (35, 4),
    (36, 5), (37, 5), (38, 5), (39, 5),
    // Spectral coefficient 2 — 5 entries
    (31, 1), (32, 3), (30, 3), (33, 3), (29, 3),
    // Spectral coefficient 3 — 7 entries
    (31, 1), (32, 3), (30, 3), (33, 4),
    (29, 4), (34, 4), (28, 4),
    // Spectral coefficient 4 — 9 entries
    (31, 1), (32, 3), (30, 3), (33, 4), (29, 4),
    (34, 5), (28, 5), (35, 5), (27, 5),
    // Spectral coefficient 5 — 15 entries
    (31, 2), (32, 3), (30, 3), (33, 4), (29, 4),
    (34, 4), (28, 4), (38, 4), (24, 4), (35, 5),
    (27, 5), (36, 6), (26, 6), (37, 6), (25, 6),
    // Spectral coefficient 6 — 31 entries
    (31, 3), (32, 4), (30, 4), (33, 4), (29, 4), (34, 4),
    (28, 4), (46, 4), (16, 4), (35, 5), (27, 5), (36, 5),
    (26, 5), (37, 5), (25, 5), (38, 6), (24, 6), (39, 6),
    (23, 6), (40, 6), (22, 6), (41, 6), (21, 6), (42, 7),
    (20, 7), (43, 7), (19, 7), (44, 7), (18, 7), (45, 7),
    (17, 7),
    // Spectral coefficient 7 — 63 entries
    (31, 3), (62, 4), (0, 4), (32, 5), (30, 5), (33, 5),
    (29, 5), (34, 5), (28, 5), (35, 5), (27, 5), (36, 5),
    (26, 5), (37, 6), (25, 6), (38, 6), (24, 6), (39, 6),
    (23, 6), (40, 6), (22, 6), (41, 6), (21, 6), (42, 6),
    (20, 6), (43, 6), (19, 6), (44, 6), (18, 6), (45, 7),
    (17, 7), (46, 7), (16, 7), (47, 7), (15, 7), (48, 7),
    (14, 7), (49, 7), (13, 7), (50, 7), (12, 7), (51, 7),
    (11, 7), (52, 8), (10, 8), (53, 8), (9, 8), (54, 8),
    (8, 8), (55, 8), (7, 8), (56, 8), (6, 8), (57, 8),
    (5, 8), (58, 8), (4, 8), (59, 8), (3, 8), (60, 8),
    (2, 8), (61, 8), (1, 8),
  ];

  /// <summary>The symbol offset FFmpeg applies when building the spectral VLCs.</summary>
  public const int HuffSymbolOffset = -31;

  /// <summary>qmf_48tap_half[24] — half of the symmetric 48-tap QMF prototype.</summary>
  private static readonly double[] Qmf48TapHalf = [
    -0.00001461907, -0.00009205479, -0.000056157569, 0.00030117269,
    0.0002422519, -0.00085293897, -0.0005205574, 0.0020340169,
    0.00078333891, -0.0042153862, -0.00075614988, 0.0078402944,
    -0.000061169922, -0.01344162, 0.0024626821, 0.021736089,
    -0.007801671, -0.034090221, 0.01880949, 0.054326009,
    -0.043596379, -0.099384367, 0.13207909, 0.46424159,
  ];

  /// <summary>ff_atrac_sf_table[64]: <c>pow(2, (i - 15) / 3)</c>.</summary>
  public static readonly float[] SfTable = BuildSfTable();

  /// <summary>qmf_window[48]: the 48-tap window built from the doubled half-prototype.</summary>
  public static readonly float[] QmfWindow = BuildQmfWindow();

  /// <summary>mdct_window[512]: the ATRAC3 IMDCT analysis/synthesis window.</summary>
  public static readonly float[] MdctWindow = BuildMdctWindow();

  private static float[] BuildSfTable() {
    var t = new float[64];
    for (var i = 0; i < 64; ++i)
      t[i] = (float)Math.Pow(2.0, (i - 15) / 3.0);
    return t;
  }

  private static float[] BuildQmfWindow() {
    var w = new float[48];
    for (var i = 0; i < 24; ++i) {
      var s = (float)(Qmf48TapHalf[i] * 2.0);
      w[i] = w[47 - i] = s;
    }
    return w;
  }

  private static float[] BuildMdctWindow() {
    var w = new float[512];
    for (int i = 0, j = 255; i < 128; ++i, --j) {
      var wi = (float)(Math.Sin(((i + 0.5) / 256.0 - 0.5) * Math.PI) + 1.0);
      var wj = (float)(Math.Sin(((j + 0.5) / 256.0 - 0.5) * Math.PI) + 1.0);
      var d = (float)(0.5 * (wi * wi + wj * wj));
      w[i] = w[511 - i] = wi / d;
      w[j] = w[511 - j] = wj / d;
    }
    return w;
  }
}
