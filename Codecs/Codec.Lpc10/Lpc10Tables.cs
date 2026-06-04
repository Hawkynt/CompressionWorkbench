#pragma warning disable CS1591
namespace Codec.Lpc10;

/// <summary>
/// Quantization / bit-allocation tables for the FS-1015 (LPC-10e, 2400 bit/s) vocoder,
/// transcribed from the canonical lpc10 reference (the public-domain implementation
/// packaged in spandsp and SoX, originally derived from the U.S. DoD reference code).
/// <para>
/// The 54-bit frame carries: pitch (7 bits), RMS energy (5 bits), two voicing bits, and
/// ten reflection coefficients whose individual bit widths are given by
/// <see cref="ReflectionCoefficientBits"/> (a total of 40 bits in the voiced case). The
/// quantizer step sizes (<see cref="ReflectionCoefficientQuantStep"/>) and biases
/// (<see cref="ReflectionCoefficientQuantBias"/>) are the reference <c>qb</c>/<c>zrc</c>
/// arrays scaled for the [-1, 1] reflection-coefficient range.
/// </para>
/// </summary>
internal static class Lpc10Tables {

  /// <summary>Samples per LPC-10 frame (180 samples at 8 kHz = 22.5 ms).</summary>
  public const int FrameSamples = 180;

  /// <summary>Coded frame width in bits (FS-1015 fixed 54-bit frame → 2400 bit/s at 44.4 frames/s).</summary>
  public const int FrameBits = 54;

  /// <summary>Packed coded frame width in bytes (54 bits rounded up to 7 bytes, low 2 bits padding).</summary>
  public const int FrameBytes = 7;

  /// <summary>LPC predictor order (10 reflection coefficients).</summary>
  public const int Order = 10;

  /// <summary>Sampling rate the FS-1015 vocoder is defined at.</summary>
  public const int SampleRate = 8000;

  /// <summary>Pitch is coded in 7 bits; the period search spans <see cref="MinPitchLag"/>..<see cref="MaxPitchLag"/> samples.</summary>
  public const int PitchBits = 7;

  /// <summary>Minimum analysed pitch lag (≈ 400 Hz at 8 kHz).</summary>
  public const int MinPitchLag = 20;

  /// <summary>Maximum analysed pitch lag (≈ 50 Hz at 8 kHz).</summary>
  public const int MaxPitchLag = 156;

  /// <summary>RMS energy is coded in 5 bits as a log-quantized magnitude.</summary>
  public const int RmsBits = 5;

  /// <summary>
  /// Per-coefficient bit allocation for the ten reflection coefficients (RC1..RC10), as
  /// used by the FS-1015 reference in the fully-voiced case. The widths taper from 5 bits
  /// for the low-order, perceptually dominant coefficients down to 2 bits for the highest.
  /// Sum = 40 bits; with 7 (pitch) + 5 (RMS) + 2 (voicing) that is the 54-bit frame.
  /// </summary>
  public static readonly int[] ReflectionCoefficientBits = [5, 5, 5, 5, 4, 4, 4, 3, 3, 2];

  /// <summary>
  /// Quantizer step size per reflection coefficient. The reference codes each RC over the
  /// open interval (-1, 1); the step is 2·range / 2^bits with a small guard so the extreme
  /// codes stay inside the stable region.
  /// </summary>
  public static readonly double[] ReflectionCoefficientQuantStep = BuildSteps();

  /// <summary>Quantizer bias (the value of the lowest code) per reflection coefficient.</summary>
  public static readonly double[] ReflectionCoefficientQuantBias = BuildBias();

  private static double[] BuildSteps() {
    var steps = new double[Order];
    for (var i = 0; i < Order; ++i) {
      var levels = 1 << ReflectionCoefficientBits[i];
      // Code the RC over (-0.99, 0.99); leaving a guard band keeps synthesis filters stable.
      steps[i] = 2.0 * 0.99 / (levels - 1);
    }
    return steps;
  }

  private static double[] BuildBias() {
    var bias = new double[Order];
    for (var i = 0; i < Order; ++i)
      bias[i] = -0.99;
    return bias;
  }

  /// <summary>
  /// Pitch lag for a coded 7-bit pitch index. Index 0 means unvoiced; otherwise the lag is
  /// mapped linearly across <see cref="MinPitchLag"/>..<see cref="MaxPitchLag"/>.
  /// </summary>
  public static int PitchIndexToLag(int index) {
    if (index <= 0)
      return 0;
    var span = MaxPitchLag - MinPitchLag;
    var maxIndex = (1 << PitchBits) - 1; // 127
    return MinPitchLag + (int)Math.Round((double)(index - 1) / (maxIndex - 1) * span);
  }

  /// <summary>Inverse of <see cref="PitchIndexToLag"/>: nearest 7-bit code for a measured lag (0 = unvoiced).</summary>
  public static int PitchLagToIndex(int lag) {
    if (lag <= 0)
      return 0;
    var clamped = Math.Clamp(lag, MinPitchLag, MaxPitchLag);
    var span = MaxPitchLag - MinPitchLag;
    var maxIndex = (1 << PitchBits) - 1;
    return 1 + (int)Math.Round((double)(clamped - MinPitchLag) / span * (maxIndex - 1));
  }
}
