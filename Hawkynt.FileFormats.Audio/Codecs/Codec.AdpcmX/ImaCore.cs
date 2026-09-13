#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Shared IMA ADPCM step/index tables and the canonical <c>adpcm_ima_expand_nibble</c>
/// helper (ported from ffmpeg <c>libavcodec/adpcm.c</c>). Several of the AdpcmX variants
/// (Duck DK3/DK4, the EA EACS/SEAD nibble streams and 4XM) are thin layout wrappers around
/// this exact expand step; keeping one copy here avoids drift between them. The tables are
/// the standard 89-entry IMA step table and the 16-entry index-adjust table.
/// </summary>
internal static class ImaCore {

  internal static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  /// <summary>Index adjustment for the low three bits of a nibble (ffmpeg <c>ff_adpcm_index_table</c>).</summary>
  internal static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  /// <summary>
  /// The canonical IMA expand step (<c>adpcm_ima_expand_nibble</c>). <paramref name="shift"/>
  /// is the diff-accumulation shift (3 for DK3/DK4/EACS, 4 for 4XM, 6 for SEAD); ffmpeg passes
  /// it so different variants can tune the delta granularity. Predictor and step index are
  /// updated in place and the new clamped 16-bit sample is returned.
  /// </summary>
  internal static short Expand(int nibble, ref int predictor, ref int index, int shift) {
    var step = StepTable[index];
    var diff = step >> shift;
    if ((nibble & 4) != 0) diff += step;
    if ((nibble & 2) != 0) diff += step >> 1;
    if ((nibble & 1) != 0) diff += step >> 2;
    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    if (predictor > 32767) predictor = 32767;
    else if (predictor < -32768) predictor = -32768;
    index += IndexAdjust[nibble & 0x07];
    if (index < 0) index = 0;
    else if (index > 88) index = 88;
    return (short)predictor;
  }

  internal static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;

  internal static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;
}
