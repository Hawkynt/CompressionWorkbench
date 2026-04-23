#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Channel coupling matrix application. Vorbis mappings store M/S-style square
/// polar pairs as (magnitude, angle) channel indices; this class undoes the
/// polar transform back into independent left/right (or N-channel) signals.
/// </summary>
internal static class VorbisMapping {

  public static void DecouplePolar(VorbisSetup.Mapping mapping, float[][] residueVectors, int n) {
    if (mapping.CouplingMagnitude.Length == 0) return;
    // Walk the coupling steps in reverse so each pair undoes its own
    // contribution before earlier steps see it.
    for (var step = mapping.CouplingMagnitude.Length - 1; step >= 0; --step) {
      var mag = residueVectors[mapping.CouplingMagnitude[step]];
      var ang = residueVectors[mapping.CouplingAngle[step]];
      for (var i = 0; i < n; ++i) {
        var m = mag[i];
        var a = ang[i];
        float newM, newA;
        if (m > 0) {
          if (a > 0) { newM = m; newA = m - a; } else { newA = m; newM = m + a; }
        } else {
          if (a > 0) { newM = m; newA = m + a; } else { newA = m; newM = m - a; }
        }
        mag[i] = newM;
        ang[i] = newA;
      }
    }
  }
}
