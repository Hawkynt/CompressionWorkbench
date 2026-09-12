#pragma warning disable CS1591
namespace Codec.AmrWb;

/// <summary>
/// AMR wideband constants (ffmpeg <c>amrwbdata.h</c> #defines). The bulk numeric tables are the
/// mechanically-faithful port in <see cref="AmrWbTables"/>.
/// </summary>
internal static class AmrWbData {
  public const int LpOrder = 16;
  public const int LpOrder16k = 20;
  public const int HbFirSize = 30;
  public const int UpsFirSize = 12;
  public const int UpsMemSize = 2 * UpsFirSize; // 24
  public const int SfrSize = 64;                // 12.8 kHz subframe
  public const int SfrSize16k = 80;             // 16 kHz subframe
  public const int PDelayMax = 231;
  public const int PDelayMin = 34;

  public const double MinIsfSpacing = 128.0 / 32768.0;
  public const double PredFactor = 1.0 / 3.0;
  public const float MinEnergy = -14.0f;
  public const float EnergyMean = 30.0f;
  public const float PreemphFac = 0.68f;

  /// <summary>Output samples per 20 ms frame at 16 kHz (4 subframes × 80).</summary>
  public const int SamplesPerFrame = 4 * SfrSize16k; // 320

  public const float Hpf31Gain = AmrWbTables.Hpf31Gain;
  public const float Hpf400Gain = AmrWbTables.Hpf400Gain;

  /// <summary>True if the 4-bit frame type is an active speech mode (0..8).</summary>
  public static bool IsSpeech(int frameType) => frameType is >= 0 and <= 8;

  /// <summary>True if the 4-bit frame type is the SID comfort-noise frame (9).</summary>
  public static bool IsSid(int frameType) => frameType == 9;

  /// <summary>
  /// Storage-format frame byte size for a 4-bit frame type: <c>((cf_sizes_wb+7)&gt;&gt;3)+1</c>.
  /// Yields {17,23,32,36,40,46,50,58,60} for the speech modes and 5 for SID. Provenance: ffmpeg
  /// amrwb demuxer / <c>cf_sizes_wb</c>.
  /// </summary>
  public static int FrameBytes(int frameType) {
    if (frameType < 0 || frameType >= AmrWbTables.CfSizesWb.Length)
      return 0;
    var bits = AmrWbTables.CfSizesWb[frameType];
    return bits == 0 ? 0 : ((bits + 7) >> 3) + 1;
  }
}
