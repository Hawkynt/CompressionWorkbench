#pragma warning disable CS1591
namespace Codec.Atrac1;

/// <summary>
/// ATRAC1 static tables, ported verbatim from FFmpeg's <c>libavcodec/atrac1data.h</c> together
/// with the shared ATRAC generators in <c>libavcodec/atrac.c</c> (scale-factor table, 48-tap QMF
/// window) and the length-32 sine analysis window from <c>libavcodec/sinewin.c</c>. The scale
/// factor / QMF tables are bit-identical to the ones the ATRAC3 decoder (<c>Codec.Atrac3</c>) generates; they
/// are re-derived here so this decoder has no cross-codec build dependency.
/// </summary>
internal static class Atrac1Tables {

  /// <summary>BFU-count selector (info byte): how many block floating units are coded.</summary>
  public static readonly int[] BfuAmountTab1 = [20, 28, 32, 36, 40, 44, 48, 52];

  /// <summary>Consumed-bits accounting helpers (do not affect the spectrum, only overflow checks).</summary>
  public static readonly int[] BfuAmountTab2 = [0, 112, 176, 208];
  public static readonly int[] BfuAmountTab3 = [0, 24, 36, 48, 72, 108, 132, 156];

  /// <summary>First-BFU index of each of the three QMF bands (low / mid / high) plus the end.</summary>
  public static readonly int[] BfuBands = [0, 20, 36, 52];

  /// <summary>Number of spectral lines in each of the 52 BFUs.</summary>
  public static readonly int[] SpecsPerBfu = [
    8, 8, 8, 8, 4, 4, 4, 4, 8, 8, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6,        // low band
    6, 6, 6, 6, 7, 7, 7, 7, 9, 9, 9, 9, 10, 10, 10, 10,               // middle band
    12, 12, 12, 12, 12, 12, 12, 12, 20, 20, 20, 20, 20, 20, 20, 20,   // high band
  ];

  /// <summary>Start position of each BFU in the MDCT spectrum — long mode.</summary>
  public static readonly int[] BfuStartLong = [
    0, 8, 16, 24, 32, 36, 40, 44, 48, 56, 64, 72, 80, 86, 92, 98, 104, 110, 116, 122,
    128, 134, 140, 146, 152, 159, 166, 173, 180, 189, 198, 207, 216, 226, 236, 246,
    256, 268, 280, 292, 304, 316, 328, 340, 352, 372, 392, 412, 432, 452, 472, 492,
  ];

  /// <summary>Start position of each BFU in the MDCT spectrum — short mode.</summary>
  public static readonly int[] BfuStartShort = [
    0, 32, 64, 96, 8, 40, 72, 104, 12, 44, 76, 108, 20, 52, 84, 116, 26, 58, 90, 122,
    128, 160, 192, 224, 134, 166, 198, 230, 141, 173, 205, 237, 150, 182, 214, 246,
    256, 288, 320, 352, 384, 416, 448, 480, 268, 300, 332, 364, 396, 428, 460, 492,
  ];

  /// <summary>Transform size in samples in long mode for each QMF band {low, mid, high}.</summary>
  public static readonly int[] SamplesPerBand = [128, 128, 256];

  /// <summary>log2 transform size in long mode for each QMF band.</summary>
  public static readonly int[] MdctLongNbits = [7, 7, 8];

  // ── generated tables ──────────────────────────────────────────────────────────

  private static readonly float[] Qmf48TapHalf = [
    -0.00001461907f, -0.00009205479f, -0.000056157569f, 0.00030117269f,
     0.0002422519f,  -0.00085293897f, -0.0005205574f,   0.0020340169f,
     0.00078333891f, -0.0042153862f,  -0.00075614988f,  0.0078402944f,
    -0.000061169922f,-0.01344162f,     0.0024626821f,   0.021736089f,
    -0.007801671f,   -0.034090221f,    0.01880949f,     0.054326009f,
    -0.043596379f,   -0.099384367f,    0.13207909f,     0.46424159f,
  ];

  /// <summary>Scale factor table: <c>sf[i] = 2^((i-15)/3)</c> (shared with the ATRAC family).</summary>
  public static readonly float[] SfTable = BuildSfTable();

  /// <summary>Symmetric 48-tap inverse-QMF synthesis window (shared with the ATRAC family).</summary>
  public static readonly float[] QmfWindow = BuildQmfWindow();

  /// <summary>Length-32 sine analysis window: <c>w[i] = sin((i+0.5)·π/64)</c>.</summary>
  public static readonly float[] Sine32 = BuildSine32();

  private static float[] BuildSfTable() {
    var t = new float[64];
    for (var i = 0; i < 64; ++i)
      t[i] = (float)Math.Pow(2.0, (i - 15) / 3.0);
    return t;
  }

  private static float[] BuildQmfWindow() {
    var w = new float[48];
    for (var i = 0; i < 24; ++i) {
      var s = Qmf48TapHalf[i] * 2.0f;
      w[i] = w[47 - i] = s;
    }
    return w;
  }

  private static float[] BuildSine32() {
    var w = new float[32];
    for (var i = 0; i < 32; ++i)
      w[i] = (float)Math.Sin((i + 0.5) * (Math.PI / (2.0 * 32)));
    return w;
  }
}
