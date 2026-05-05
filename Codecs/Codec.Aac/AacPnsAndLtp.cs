#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Perceptual Noise Substitution (PNS) — ISO/IEC 14496-3 §4.6.13. PNS replaces
/// a scalefactor band's spectrum with white noise scaled by a transmitted energy
/// value. Codebook 13 (NOISE_HCB) signals PNS application; correlation between
/// L/R noise is transmitted via the m/s mask when both channels use PNS for the
/// same band.
/// <para>
/// LTP (Long-Term Prediction, AAC-Main / AAC-LTP profiles) is intentionally
/// unimplemented — AAC-LC does not use it.
/// </para>
/// </summary>
internal static class AacPns {

  /// <summary>The Huffman-codebook index that signals noise substitution.</summary>
  public const int NoiseHcb = 13;

  /// <summary>The codebook indices used for intensity stereo.</summary>
  public const int IntensityHcb = 14;
  public const int IntensityHcb2 = 15;

  /// <summary>
  /// Generates a band of pseudo-random noise scaled to the energy implied by
  /// <paramref name="noiseEnergyDecibels"/>. The seed is stateful per channel
  /// per ISO/IEC 14496-3 §4.6.13.3 so L/R bands stay decorrelated unless the
  /// m/s mask says otherwise.
  /// </summary>
  public static void GenerateNoiseBand(float[] spectrum, int start, int end, ref uint seed, float noiseEnergyDecibels) {
    var scale = MathF.Pow(2f, noiseEnergyDecibels / 4f) / MathF.Sqrt(end - start);
    float energy = 0;
    var temp = new float[end - start];
    for (var k = 0; k < temp.Length; ++k) {
      seed = seed * 1664525u + 1013904223u; // numerical-recipes LCG
      var raw = (int)(seed >> 1) - (int)(1u << 30);
      temp[k] = raw;
      energy += raw * (float)raw;
    }
    var norm = energy > 0 ? scale / MathF.Sqrt(energy) : 0f;
    for (var k = 0; k < temp.Length; ++k)
      spectrum[start + k] = temp[k] * norm;
  }
}
