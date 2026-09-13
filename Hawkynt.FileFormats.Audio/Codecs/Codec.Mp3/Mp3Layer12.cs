#pragma warning disable CS1591

namespace Codec.Mp3;

/// <summary>
/// MPEG-1/2 Layer I and Layer II decoder internals: subband bit-allocation table
/// selection, scale-factor selection info (scfsi) + scale-factor reading, grouped /
/// linear sample dequantization and scale-factor application. The 32 dequantized
/// subband samples per time slot are fed into the shared polyphase synthesis
/// filterbank (<see cref="Mp3Synthesis.SynthGranule"/>) the Layer III path also uses.
/// Port of minimp3's L12_* functions; Layer I uses <c>group_size = 1</c> and Layer II
/// <c>group_size = 3</c> (3 samples per granule slot).
/// </summary>
internal sealed class Mp3Layer12 {

  /// <summary>
  /// Per-frame Layer I/II scale info (matches minimp3's <c>L12_scale_info</c>):
  /// total subbands, stereo (non-intensity) bound, per-(band,channel) bit allocation
  /// and scale-factor selection codes, plus the decoded per-band scale factors.
  /// </summary>
  public sealed class ScaleInfo {
    public readonly float[] Scf = new float[3 * 64];
    public byte TotalBands;
    public byte StereoBands;
    public readonly byte[] BitAlloc = new byte[64];
    public readonly byte[] Scfcod = new byte[64];
  }

  /// <summary>One row of the subband-allocation table (matches minimp3's <c>L12_subband_alloc_t</c>).</summary>
  private readonly record struct SubbandAlloc(byte TabOffset, byte CodeTabWidth, byte BandCount);

  private static readonly SubbandAlloc[] _AllocL1 = { new(76, 4, 32) };
  private static readonly SubbandAlloc[] _AllocL2M2 = { new(60, 4, 4), new(44, 3, 7), new(44, 2, 19) };
  private static readonly SubbandAlloc[] _AllocL2M1 = { new(0, 4, 3), new(16, 4, 8), new(32, 3, 12), new(40, 2, 7) };
  private static readonly SubbandAlloc[] _AllocL2M1LowRate = { new(44, 4, 2), new(44, 3, 10) };

  private static readonly byte[] _BitallocCodeTab = {
    0,17, 3, 4, 5,6,7, 8,9,10,11,12,13,14,15,16,
    0,17,18, 3,19,4,5, 6,7, 8, 9,10,11,12,13,16,
    0,17,18, 3,19,4,5,16,
    0,17,18,16,
    0,17,18,19, 4,5,6, 7,8, 9,10,11,12,13,14,15,
    0,17,18, 3,19,4,5, 6,7, 8, 9,10,11,12,13,14,
    0, 2, 3, 4, 5,6,7, 8,9,10,11,12,13,14,15,16
  };

  // g_deq_L12: 18 nb-classes × 3 (b%3) dequantizer normalization constants. DQ(x) expands
  // to {9.53674316e-07/x, 7.56931807e-07/x, 6.00777173e-07/x}; the leading constant brings
  // the (already scale-factor-scaled) samples to ~full-scale 16-bit, x is the level count.
  private static readonly float[] _DeqL12 = BuildDeqTable();

  private static float[] BuildDeqTable() {
    var t = new float[18 * 3];
    var levels = new[] { 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9 };
    var idx = 0;
    foreach (var x in levels) {
      t[idx++] = 9.53674316e-07f / x;
      t[idx++] = 7.56931807e-07f / x;
      t[idx++] = 6.00777173e-07f / x;
    }
    return t;
  }

  /// <summary>
  /// Selects the subband-allocation table for the current frame and fills
  /// <see cref="ScaleInfo.TotalBands"/> / <see cref="ScaleInfo.StereoBands"/>.
  /// </summary>
  private static SubbandAlloc[] SubbandAllocTable(in Mp3FrameHeader hdr, ScaleInfo sci) {
    var mode = hdr.ChannelMode;
    var stereoBands = mode == 3 ? 0 : (mode == 1 ? (hdr.ModeExtension << 2) + 4 : 32);

    SubbandAlloc[] alloc;
    int nbands;

    if (hdr.Layer == 1) {
      alloc = _AllocL1;
      nbands = 32;
    } else if (!hdr.IsMpeg1) {
      alloc = _AllocL2M2;
      nbands = 30;
    } else {
      var sampleRateIdx = hdr.SampleRateIndex;
      var kbps = hdr.BitrateKbps >> (mode != 3 ? 1 : 0);
      if (kbps == 0) kbps = 192; // free-format fallback (matches minimp3)

      alloc = _AllocL2M1;
      nbands = 27;
      if (kbps < 56) {
        alloc = _AllocL2M1LowRate;
        nbands = sampleRateIdx == 2 ? 12 : 8;
      } else if (kbps >= 96 && sampleRateIdx != 1) {
        nbands = 30;
      }
    }

    sci.TotalBands = (byte)nbands;
    sci.StereoBands = (byte)Math.Min(stereoBands, nbands);
    return alloc;
  }

  private static void ReadScalefactors(Mp3BitReader bs, byte[] pba, int pbaOff, byte[] scfcod, int bands, float[] scf) {
    var scfPos = 0;
    for (var i = 0; i < bands; i++) {
      float s = 0;
      int ba = pba[pbaOff + i];
      var mask = ba != 0 ? 4 + ((19 >> scfcod[i]) & 3) : 0;
      for (var m = 4; m != 0; m >>= 1) {
        if ((mask & m) != 0) {
          var b = (int)bs.GetBits(6);
          s = _DeqL12[ba * 3 - 6 + b % 3] * (1 << 21 >> b / 3);
        }
        scf[scfPos++] = s;
      }
    }
  }

  /// <summary>
  /// Reads bit allocation, scfsi and scale factors for all subbands of the frame.
  /// </summary>
  public static void ReadScaleInfo(in Mp3FrameHeader hdr, Mp3BitReader bs, ScaleInfo sci) {
    var subbandAlloc = SubbandAllocTable(hdr, sci);

    var allocIdx = 0;
    var k = 0;
    var baBits = 0;
    var baCodeTabOff = 0;

    for (var i = 0; i < sci.TotalBands; i++) {
      if (i == k) {
        k += subbandAlloc[allocIdx].BandCount;
        baBits = subbandAlloc[allocIdx].CodeTabWidth;
        baCodeTabOff = subbandAlloc[allocIdx].TabOffset;
        allocIdx++;
      }
      var ba = _BitallocCodeTab[baCodeTabOff + (int)bs.GetBits(baBits)];
      sci.BitAlloc[2 * i] = ba;
      if (i < sci.StereoBands)
        ba = _BitallocCodeTab[baCodeTabOff + (int)bs.GetBits(baBits)];
      sci.BitAlloc[2 * i + 1] = sci.StereoBands != 0 ? ba : (byte)0;
    }

    for (var i = 0; i < 2 * sci.TotalBands; i++)
      sci.Scfcod[i] = sci.BitAlloc[i] != 0 ? (hdr.Layer == 1 ? (byte)2 : (byte)bs.GetBits(2)) : (byte)6;

    ReadScalefactors(bs, sci.BitAlloc, 0, sci.Scfcod, sci.TotalBands * 2, sci.Scf);

    for (var i = sci.StereoBands; i < sci.TotalBands; i++)
      sci.BitAlloc[2 * i + 1] = 0;
  }

  /// <summary>
  /// Dequantizes one granule's worth of subband samples (group_size samples per band per
  /// channel) into <paramref name="grbuf"/> at the band-major layout the synthesis expects.
  /// Returns the number of time slots written (group_size*4).
  /// </summary>
  public static int DequantizeGranule(float[] grbuf, int grOff, Mp3BitReader bs, ScaleInfo sci, int groupSize) {
    var choff = 576;
    for (var j = 0; j < 4; j++) {
      var dst = grOff + groupSize * j;
      for (var i = 0; i < 2 * sci.TotalBands; i++) {
        int ba = sci.BitAlloc[i];
        if (ba != 0) {
          if (ba < 17) {
            var half = (1 << (ba - 1)) - 1;
            for (var kk = 0; kk < groupSize; kk++)
              grbuf[dst + kk] = (int)bs.GetBits(ba) - half;
          } else {
            var mod = (uint)((2 << (ba - 17)) + 1); // 3, 5, 9
            var code = bs.GetBits((int)(mod + 2 - (mod >> 3))); // 5, 7, 10
            for (var kk = 0; kk < groupSize; kk++, code /= mod)
              grbuf[dst + kk] = (int)(code % mod) - (int)(mod / 2);
          }
        }
        dst += choff;
        choff = 18 - choff;
      }
    }
    return groupSize * 4;
  }

  /// <summary>
  /// Applies the per-band scale factors to a fully-dequantized 12-slot granule and
  /// mirrors the intensity-bound bands into the second channel buffer.
  /// </summary>
  public static void ApplyScf384(ScaleInfo sci, float[] scf, int scfOff, float[] dst, int dstOff) {
    // Duplicate the shared (mono / intensity-bound) bands into the right channel buffer.
    Array.Copy(dst, dstOff + sci.StereoBands * 18,
               dst, dstOff + 576 + sci.StereoBands * 18,
               (sci.TotalBands - sci.StereoBands) * 18);

    var d = dstOff;
    var s = scfOff;
    for (var i = 0; i < sci.TotalBands; i++, d += 18, s += 6) {
      for (var kk = 0; kk < 12; kk++) {
        dst[d + kk] *= scf[s + 0];
        dst[d + kk + 576] *= scf[s + 3];
      }
    }
  }
}
