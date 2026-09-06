#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Constant tables for AC-3 (ATSC A/52) decoding. All tables are ported faithfully from the
/// public A/52 specification and the FFmpeg reference decoder (<c>libavcodec/ac3tab.c</c>,
/// <c>ac3dec_data.c</c>, <c>ac3.c</c>); the names mirror the spec / FFmpeg identifiers so the
/// port can be cross-checked. They cover the bit-allocation parametric model
/// (<c>bndtab</c>/<c>masktab</c>/<c>latab</c>/decay/gain/db-per-bit/floor), the quantization
/// mantissa levels (<c>baptab</c> bits-per-mantissa) and the IMDCT window.
/// </summary>
internal static class Ac3Tables {

  /// <summary>Number of mantissas per bap index (FFmpeg <c>ff_ac3_bap_tab</c>, indexed by mask value).</summary>
  // bits per mantissa for each of the 16 bit-allocation pointer (bap) values.
  public static readonly byte[] BapBits = [0, 0, 0, 3, 0, 4, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0];

  // ── Quantization quantities per bap (A/52 Table 7.21) ────────────────────────────────
  // qntztab[bap] = number of quantization levels; 0 means special / not a simple linear bap.
  // bap:           0  1  2  3   4   5    6    7    8     9     10    11    12     13     14     15
  // levels:        0  3  5  7  11  15   32   64  128   256   512  1024  2048   4096   16384  65536
  // group sizes: bap1→3 levels grouped by 3 (5-bit word), bap2→5 levels grouped by 3 (7-bit),
  //              bap3→7 levels direct (3 bits), bap4→11 grouped by 2 (7-bit), bap5→15 direct (4 bits),
  //              bap6..15→ linear, qntztab[bap] bits per mantissa.
  public static readonly byte[] QuantizationBits = [0, 0, 0, 3, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16];

  // ── Bit-allocation parametric tables (A/52 §7.2.2, FFmpeg ac3tab.c) ───────────────────

  /// <summary>FFmpeg <c>ff_ac3_slow_decay_tab</c> indexed by sdcycod.</summary>
  public static readonly byte[] SlowDecay = [0x0f, 0x11, 0x13, 0x15];

  /// <summary>FFmpeg <c>ff_ac3_fast_decay_tab</c> indexed by fdcycod.</summary>
  public static readonly byte[] FastDecay = [0x3f, 0x53, 0x67, 0x7b];

  /// <summary>FFmpeg <c>ff_ac3_slow_gain_tab</c> indexed by sgaincod.</summary>
  public static readonly ushort[] SlowGain = [0x540, 0x4d8, 0x478, 0x410];

  /// <summary>FFmpeg <c>ff_ac3_db_per_bit_tab</c> indexed by dbpbcod.</summary>
  public static readonly ushort[] DbPerBit = [0x000, 0x700, 0x900, 0xb00];

  /// <summary>
  /// A/52 Table 7.10 floortab[], indexed by floorcod. Entry 7 is the negative sentinel
  /// 0xf800, which §7.2.2.7 relies on: the masking value has the floor subtracted, is
  /// clamped at zero, is truncated to 0x1fe0 and then has the floor added back.
  /// </summary>
  public static readonly short[] Floor = [0x2f0, 0x2b0, 0x270, 0x230, 0x1f0, 0x170, 0x0f0, unchecked((short)0xf800)];

  /// <summary>FFmpeg <c>ff_ac3_fast_gain_tab</c> indexed by fgaincod (per channel).</summary>
  public static readonly ushort[] FastGain = [0x080, 0x100, 0x180, 0x200, 0x280, 0x300, 0x380, 0x400];

  /// <summary>
  /// Logarithmic-to-linear add table (A/52 Table 7.14, latab[]). Indexed by half the absolute
  /// difference of the two power-spectral-density terms being combined, clamped to 255. All 256
  /// entries matter: the tail is zero, so a truncated copy adds energy where the spec adds none.
  /// </summary>
  public static readonly byte[] LogAdd = [
    0x0040, 0x003f, 0x003e, 0x003d, 0x003c, 0x003b, 0x003a, 0x0039, 0x0038, 0x0037,
    0x0036, 0x0035, 0x0034, 0x0034, 0x0033, 0x0032, 0x0031, 0x0030, 0x002f, 0x002f,
    0x002e, 0x002d, 0x002c, 0x002c, 0x002b, 0x002a, 0x0029, 0x0029, 0x0028, 0x0027,
    0x0026, 0x0026, 0x0025, 0x0024, 0x0024, 0x0023, 0x0023, 0x0022, 0x0021, 0x0021,
    0x0020, 0x0020, 0x001f, 0x001e, 0x001e, 0x001d, 0x001d, 0x001c, 0x001c, 0x001b,
    0x001b, 0x001a, 0x001a, 0x0019, 0x0019, 0x0018, 0x0018, 0x0017, 0x0017, 0x0016,
    0x0016, 0x0015, 0x0015, 0x0015, 0x0014, 0x0014, 0x0013, 0x0013, 0x0013, 0x0012,
    0x0012, 0x0012, 0x0011, 0x0011, 0x0011, 0x0010, 0x0010, 0x0010, 0x000f, 0x000f,
    0x000f, 0x000e, 0x000e, 0x000e, 0x000d, 0x000d, 0x000d, 0x000d, 0x000c, 0x000c,
    0x000c, 0x000c, 0x000b, 0x000b, 0x000b, 0x000b, 0x000a, 0x000a, 0x000a, 0x000a,
    0x000a, 0x0009, 0x0009, 0x0009, 0x0009, 0x0009, 0x0008, 0x0008, 0x0008, 0x0008,
    0x0008, 0x0008, 0x0007, 0x0007, 0x0007, 0x0007, 0x0007, 0x0007, 0x0006, 0x0006,
    0x0006, 0x0006, 0x0006, 0x0006, 0x0006, 0x0006, 0x0005, 0x0005, 0x0005, 0x0005,
    0x0005, 0x0005, 0x0005, 0x0005, 0x0004, 0x0004, 0x0004, 0x0004, 0x0004, 0x0004,
    0x0004, 0x0004, 0x0004, 0x0004, 0x0004, 0x0003, 0x0003, 0x0003, 0x0003, 0x0003,
    0x0003, 0x0003, 0x0003, 0x0003, 0x0003, 0x0003, 0x0003, 0x0003, 0x0003, 0x0002,
    0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002,
    0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0002, 0x0001, 0x0001,
    0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001,
    0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001,
    0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001, 0x0001,
    0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
    0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
    0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
    0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
    0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
  ];

  /// <summary>
  /// Hearing-threshold table (A/52 Table 7.15, hth[fscod][band]), transposed to
  /// [50 band groups][fscod 0..2]. Each row is the threshold for a band group at one of the
  /// three legacy sample rates.
  /// </summary>
  public static readonly ushort[,] HearingThreshold = {
    { 0x04d0, 0x04f0, 0x0580 }, { 0x04d0, 0x04f0, 0x0580 }, { 0x0440, 0x0460, 0x04b0 },
    { 0x0400, 0x0410, 0x0450 }, { 0x03e0, 0x03e0, 0x0420 }, { 0x03c0, 0x03d0, 0x03f0 },
    { 0x03b0, 0x03c0, 0x03e0 }, { 0x03b0, 0x03b0, 0x03d0 }, { 0x03a0, 0x03b0, 0x03c0 },
    { 0x03a0, 0x03a0, 0x03b0 }, { 0x03a0, 0x03a0, 0x03b0 }, { 0x03a0, 0x03a0, 0x03b0 },
    { 0x03a0, 0x03a0, 0x03a0 }, { 0x0390, 0x03a0, 0x03a0 }, { 0x0390, 0x0390, 0x03a0 },
    { 0x0390, 0x0390, 0x03a0 }, { 0x0380, 0x0390, 0x03a0 }, { 0x0380, 0x0380, 0x03a0 },
    { 0x0370, 0x0380, 0x03a0 }, { 0x0370, 0x0380, 0x03a0 }, { 0x0360, 0x0370, 0x0390 },
    { 0x0360, 0x0370, 0x0390 }, { 0x0350, 0x0360, 0x0390 }, { 0x0350, 0x0360, 0x0390 },
    { 0x0340, 0x0350, 0x0380 }, { 0x0340, 0x0350, 0x0380 }, { 0x0330, 0x0340, 0x0380 },
    { 0x0320, 0x0340, 0x0370 }, { 0x0310, 0x0320, 0x0360 }, { 0x0300, 0x0310, 0x0350 },
    { 0x02f0, 0x0300, 0x0340 }, { 0x02f0, 0x02f0, 0x0330 }, { 0x02f0, 0x02f0, 0x0320 },
    { 0x02f0, 0x02f0, 0x0310 }, { 0x0300, 0x02f0, 0x0300 }, { 0x0310, 0x0300, 0x02f0 },
    { 0x0340, 0x0320, 0x02f0 }, { 0x0390, 0x0350, 0x02f0 }, { 0x03e0, 0x0390, 0x0300 },
    { 0x0420, 0x03e0, 0x0310 }, { 0x0460, 0x0420, 0x0330 }, { 0x0490, 0x0450, 0x0350 },
    { 0x04a0, 0x04a0, 0x03c0 }, { 0x0460, 0x0490, 0x0410 }, { 0x0440, 0x0460, 0x0470 },
    { 0x0440, 0x0440, 0x04a0 }, { 0x0520, 0x0480, 0x0460 }, { 0x0800, 0x0630, 0x0440 },
    { 0x0840, 0x0840, 0x0450 }, { 0x0840, 0x0840, 0x04e0 },
  };

  /// <summary>
  /// Masking-curve sub-band group bin start (FFmpeg <c>ff_ac3_band_start_tab</c>, A/52 bndtab).
  /// Index 0..50 gives the first transform-coefficient bin of each of the 50 band groups; entry 50
  /// is the sentinel end (= 253). bndtab[band] = first bin of band.
  /// </summary>
  public static readonly byte[] BandStart = [
    0,  1,  2,   3,   4,   5,   6,   7,   8,   9,
    10, 11, 12,  13,  14,  15,  16,  17,  18,  19,
    20, 21, 22,  23,  24,  25,  26,  27,  28,  31,
    34, 37, 40,  43,  46,  49,  55,  61,  67,  73,
    79, 85, 97, 109, 121, 133, 157, 181, 205, 229, 253,
  ];

  /// <summary>
  /// Bin → band-group mapping (FFmpeg <c>ff_ac3_bin_to_band_tab</c>, A/52 masktab). masktab[bin]
  /// = band group index for transform bin <c>bin</c> (0..252); built from <see cref="BandStart"/>.
  /// </summary>
  public static readonly byte[] BinToBand = BuildBinToBand();

  /// <summary>Number of bins in each band group (BandStart[i+1]-BandStart[i]).</summary>
  public static readonly byte[] BandSize = BuildBandSize();

  private static byte[] BuildBinToBand() {
    var result = new byte[256];
    var band = 0;
    for (var bin = 0; bin < 253; ++bin) {
      while (band < 50 && bin >= BandStart[band + 1])
        ++band;
      result[bin] = (byte)band;
    }
    return result;
  }

  private static byte[] BuildBandSize() {
    var result = new byte[50];
    for (var i = 0; i < 50; ++i)
      result[i] = (byte)(BandStart[i + 1] - BandStart[i]);
    return result;
  }

  /// <summary>
  /// Transform window sequence w[] (A/52 Table 7.33). Used by both the 512-point long transform
  /// and the dual 256-point short transforms. Only the first 256 of the 512 window points are
  /// stored; the second half is the mirror image, w[511 - n] = w[n].
  /// </summary>
  public static readonly float[] Window = [
    0.00014f, 0.00024f, 0.00037f, 0.00051f, 0.00067f, 0.00086f, 0.00107f, 0.00130f,
    0.00157f, 0.00187f, 0.00220f, 0.00256f, 0.00297f, 0.00341f, 0.00390f, 0.00443f,
    0.00501f, 0.00564f, 0.00632f, 0.00706f, 0.00785f, 0.00871f, 0.00962f, 0.01061f,
    0.01166f, 0.01279f, 0.01399f, 0.01526f, 0.01662f, 0.01806f, 0.01959f, 0.02121f,
    0.02292f, 0.02472f, 0.02662f, 0.02863f, 0.03073f, 0.03294f, 0.03527f, 0.03770f,
    0.04025f, 0.04292f, 0.04571f, 0.04862f, 0.05165f, 0.05481f, 0.05810f, 0.06153f,
    0.06508f, 0.06878f, 0.07261f, 0.07658f, 0.08069f, 0.08495f, 0.08935f, 0.09389f,
    0.09859f, 0.10343f, 0.10842f, 0.11356f, 0.11885f, 0.12429f, 0.12988f, 0.13563f,
    0.14152f, 0.14757f, 0.15376f, 0.16011f, 0.16661f, 0.17325f, 0.18005f, 0.18699f,
    0.19407f, 0.20130f, 0.20867f, 0.21618f, 0.22382f, 0.23161f, 0.23952f, 0.24757f,
    0.25574f, 0.26404f, 0.27246f, 0.28100f, 0.28965f, 0.29841f, 0.30729f, 0.31626f,
    0.32533f, 0.33450f, 0.34376f, 0.35311f, 0.36253f, 0.37204f, 0.38161f, 0.39126f,
    0.40096f, 0.41072f, 0.42054f, 0.43040f, 0.44030f, 0.45023f, 0.46020f, 0.47019f,
    0.48020f, 0.49022f, 0.50025f, 0.51028f, 0.52031f, 0.53033f, 0.54033f, 0.55031f,
    0.56026f, 0.57019f, 0.58007f, 0.58991f, 0.59970f, 0.60944f, 0.61912f, 0.62873f,
    0.63827f, 0.64774f, 0.65713f, 0.66643f, 0.67564f, 0.68476f, 0.69377f, 0.70269f,
    0.71150f, 0.72019f, 0.72877f, 0.73723f, 0.74557f, 0.75378f, 0.76186f, 0.76981f,
    0.77762f, 0.78530f, 0.79283f, 0.80022f, 0.80747f, 0.81457f, 0.82151f, 0.82831f,
    0.83496f, 0.84145f, 0.84779f, 0.85398f, 0.86001f, 0.86588f, 0.87160f, 0.87716f,
    0.88257f, 0.88782f, 0.89291f, 0.89785f, 0.90264f, 0.90728f, 0.91176f, 0.91610f,
    0.92028f, 0.92432f, 0.92822f, 0.93197f, 0.93558f, 0.93906f, 0.94240f, 0.94560f,
    0.94867f, 0.95162f, 0.95444f, 0.95713f, 0.95971f, 0.96217f, 0.96451f, 0.96674f,
    0.96887f, 0.97089f, 0.97281f, 0.97463f, 0.97635f, 0.97799f, 0.97953f, 0.98099f,
    0.98236f, 0.98366f, 0.98488f, 0.98602f, 0.98710f, 0.98811f, 0.98905f, 0.98994f,
    0.99076f, 0.99153f, 0.99225f, 0.99291f, 0.99353f, 0.99411f, 0.99464f, 0.99513f,
    0.99558f, 0.99600f, 0.99639f, 0.99674f, 0.99706f, 0.99736f, 0.99763f, 0.99788f,
    0.99811f, 0.99831f, 0.99850f, 0.99867f, 0.99882f, 0.99895f, 0.99908f, 0.99919f,
    0.99929f, 0.99938f, 0.99946f, 0.99953f, 0.99959f, 0.99965f, 0.99969f, 0.99974f,
    0.99978f, 0.99981f, 0.99984f, 0.99986f, 0.99988f, 0.99990f, 0.99992f, 0.99993f,
    0.99994f, 0.99995f, 0.99996f, 0.99997f, 0.99998f, 0.99998f, 0.99998f, 0.99999f,
    0.99999f, 0.99999f, 0.99999f, 1.00000f, 1.00000f, 1.00000f, 1.00000f, 1.00000f,
    1.00000f, 1.00000f, 1.00000f, 1.00000f, 1.00000f, 1.00000f, 1.00000f, 1.00000f,
  ];

  // ── Dequantization lookup tables for grouped/small baps (A/52 §7.3.2) ────────────────
  // For bap 1 (3 levels), bap 2 (5 levels), bap 3 (7 levels), bap 4 (11 levels), bap 5 (15 levels)
  // mantissas are read as symmetric quantizer outputs. The reconstructed (normalized to ±1) value
  // for level index k of an N-level quantizer is (2k - (N-1)) / N.

  /// <summary>3-level symmetric dequant (bap 1). Index 0..2.</summary>
  public static readonly float[] Quant3 = SymmetricLevels(3);

  /// <summary>5-level symmetric dequant (bap 2). Index 0..4.</summary>
  public static readonly float[] Quant5 = SymmetricLevels(5);

  /// <summary>7-level symmetric dequant (bap 3). Index 0..6.</summary>
  public static readonly float[] Quant7 = SymmetricLevels(7);

  /// <summary>11-level symmetric dequant (bap 4). Index 0..10.</summary>
  public static readonly float[] Quant11 = SymmetricLevels(11);

  /// <summary>15-level symmetric dequant (bap 5). Index 0..14.</summary>
  public static readonly float[] Quant15 = SymmetricLevels(15);

  private static float[] SymmetricLevels(int levels) {
    var t = new float[levels];
    for (var k = 0; k < levels; ++k)
      t[k] = (2.0f * k - (levels - 1)) / levels;
    return t;
  }
}
