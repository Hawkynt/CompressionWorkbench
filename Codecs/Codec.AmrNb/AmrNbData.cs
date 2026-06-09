#pragma warning disable CS1591
namespace Codec.AmrNb;

/// <summary>
/// AMR narrowband constants and the float helper tables that live outside
/// <c>amrnbdata.h</c> (they sit in ffmpeg <c>libavcodec/acelp_vectors.c</c>). The bulk numeric
/// tables are the mechanically-faithful port in <see cref="AmrNbTables"/>.
/// </summary>
internal static class AmrNbData {

  /// <summary>Samples per 20 ms frame at 8 kHz.</summary>
  public const int SamplesPerFrame = 160;

  /// <summary>Subframes per frame.</summary>
  public const int SubframeCount = 4;

  /// <summary>Samples per 5 ms subframe.</summary>
  public const int SubframeSize = 40;

  /// <summary>LP filter order.</summary>
  public const int LpOrder = 10;

  /// <summary>Maximum pitch delay (PITCH_DELAY_MAX, ffmpeg acelp_pitch_delay.h).</summary>
  public const int PitchDelayMax = 143;

  /// <summary>Minimum pitch delay (PITCH_DELAY_MIN).</summary>
  public const int PitchDelayMin = 20;

  /// <summary>
  /// Storage-format (IF1 / .amr) frame size in BYTES, indexed by the 4-bit frame type. Provenance:
  /// 3GPP TS 26.101 / ffmpeg <c>amr_nb_frame_sizes</c>. These are the PAYLOAD bytes (header byte
  /// excluded): {12,13,15,17,19,20,26,31} for the speech modes, 5 for SID, 0 for NO_DATA.
  /// </summary>
  public static readonly int[] PayloadBytes = {
    12, 13, 15, 17, 19, 20, 26, 31, // MR475..MR122
    5,                              // 8: SID  (39 bits → 5 bytes)
    0, 0, 0, 0, 0,                  // 9..13 reserved
    0,                              // 14 speech lost
    0,                              // 15 NO_DATA
  };

  /// <summary>True if the 4-bit frame type is a defined active speech mode (0..7).</summary>
  public static bool IsSpeech(int frameType) => frameType is >= 0 and <= 7;

  /// <summary>True if the 4-bit frame type is the SID (comfort-noise) frame (8).</summary>
  public static bool IsSid(int frameType) => frameType == 8;

  // From ffmpeg libavcodec/acelp_vectors.c (provenance: ff_pow_0_7 / _0_75 / _0_55).
  public static readonly float[] Pow07 = {
    0.700000f, 0.490000f, 0.343000f, 0.240100f, 0.168070f,
    0.117649f, 0.082354f, 0.057648f, 0.040354f, 0.028248f,
  };
  public static readonly float[] Pow075 = {
    0.750000f, 0.562500f, 0.421875f, 0.316406f, 0.237305f,
    0.177979f, 0.133484f, 0.100113f, 0.075085f, 0.056314f,
  };
  public static readonly float[] Pow055 = {
    0.550000f, 0.302500f, 0.166375f, 0.091506f, 0.050328f,
    0.027681f, 0.015224f, 0.008373f, 0.004605f, 0.002533f,
  };

  /// <summary>b60 Hamming-windowed sinc used for 1/6-resolution pitch interpolation. Provenance:
  /// ffmpeg <c>ff_b60_sinc</c> (acelp_vectors.c).</summary>
  public static readonly float[] B60Sinc = {
    0.898529f, 0.865051f, 0.769257f, 0.624054f, 0.448639f, 0.265289f,
    0.0959167f, -0.0412598f, -0.134338f, -0.178986f, -0.178528f, -0.142609f,
    -0.0849304f, -0.0205078f, 0.0369568f, 0.0773926f, 0.0955200f, 0.0912781f,
    0.0689392f, 0.0357056f, 0.0f, -0.0305481f, -0.0504150f, -0.0570068f,
    -0.0508423f, -0.0350037f, -0.0141602f, 0.00665283f, 0.0230713f, 0.0323486f,
    0.0335388f, 0.0275879f, 0.0167847f, 0.00411987f, -0.00747681f, -0.0156860f,
    -0.0193481f, -0.0183716f, -0.0137634f, -0.00704956f, 0.0f, 0.00582886f,
    0.00939941f, 0.0103760f, 0.00903320f, 0.00604248f, 0.00238037f, -0.00109863f,
    -0.00366211f, -0.00497437f, -0.00503540f, -0.00402832f, -0.00241089f, -0.000579834f,
    0.00103760f, 0.00222778f, 0.00277710f, 0.00271606f, 0.00213623f, 0.00115967f,
    0.0f,
  };
}
