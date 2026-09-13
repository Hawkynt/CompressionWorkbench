#pragma warning disable CS1591

namespace Codec.Mp3;

/// <summary>
/// MP3 subband synthesis filterbank: DCT-II of the 32-sample subbands followed by
/// the 512-tap polyphase prototype filter (window). Produces 32 PCM samples per
/// 32-band-input call, processing 18 such steps per granule. Ported verbatim
/// (scalar path only — no SIMD) from minimp3's <c>mp3d_DCT_II</c> + <c>mp3d_synth</c>
/// + <c>mp3d_synth_pair</c> + <c>mp3d_synth_granule</c>.
/// </summary>
internal static class Mp3Synthesis {

  private static readonly float[] _Sec = {
    10.19000816f, 0.50060302f, 0.50241929f, 3.40760851f, 0.50547093f, 0.52249861f,
    2.05778098f, 0.51544732f, 0.56694406f, 1.48416460f, 0.53104258f, 0.64682180f,
    1.16943991f, 0.55310392f, 0.78815460f, 0.97256821f, 0.58293498f, 1.06067765f,
    0.83934963f, 0.62250412f, 1.72244716f, 0.74453628f, 0.67480832f, 5.10114861f
  };

  /// <summary>32-band DCT-II producing 18 contiguous granule samples per band column.</summary>
  public static void DctII(float[] grbuf, int off, int n) {
    for (var k = 0; k < n; k++) {
      var t = new float[4 * 8];
      var yOff = off + k;

      // first stage
      for (var i = 0; i < 8; i++) {
        var x0 = grbuf[yOff + i * 18];
        var x1 = grbuf[yOff + (15 - i) * 18];
        var x2 = grbuf[yOff + (16 + i) * 18];
        var x3 = grbuf[yOff + (31 - i) * 18];
        var t0 = x0 + x3;
        var t1 = x1 + x2;
        var t2 = (x1 - x2) * _Sec[3 * i + 0];
        var t3 = (x0 - x3) * _Sec[3 * i + 1];
        t[i + 0] = t0 + t1;
        t[i + 8] = (t0 - t1) * _Sec[3 * i + 2];
        t[i + 16] = t3 + t2;
        t[i + 24] = (t3 - t2) * _Sec[3 * i + 2];
      }

      // second stage — 4 groups of 8
      for (var grp = 0; grp < 4; grp++) {
        var b = grp * 8;
        float x0 = t[b + 0], x1 = t[b + 1], x2 = t[b + 2], x3 = t[b + 3];
        float x4 = t[b + 4], x5 = t[b + 5], x6 = t[b + 6], x7 = t[b + 7];
        float xt;
        xt = x0 - x7; x0 += x7;
        x7 = x1 - x6; x1 += x6;
        x6 = x2 - x5; x2 += x5;
        x5 = x3 - x4; x3 += x4;
        x4 = x0 - x3; x0 += x3;
        x3 = x1 - x2; x1 += x2;
        t[b + 0] = x0 + x1;
        t[b + 4] = (x0 - x1) * 0.70710677f;
        x5 += x6;
        x6 = (x6 + x7) * 0.70710677f;
        x7 += xt;
        x3 = (x3 + x4) * 0.70710677f;
        x5 -= x7 * 0.198912367f;
        x7 += x5 * 0.382683432f;
        x5 -= x7 * 0.198912367f;
        var x0b = xt - x6;
        xt += x6;
        t[b + 1] = (xt + x7) * 0.50979561f;
        t[b + 2] = (x4 + x3) * 0.54119611f;
        t[b + 3] = (x0b - x5) * 0.60134488f;
        t[b + 5] = (x0b + x5) * 0.89997619f;
        t[b + 6] = (x4 - x3) * 1.30656302f;
        t[b + 7] = (xt - x7) * 2.56291556f;
      }

      // butterfly write-back: 7 strips of 4 then a final tail
      var y = yOff;
      for (var i = 0; i < 7; i++, y += 4 * 18) {
        grbuf[y + 0 * 18] = t[i + 0];
        grbuf[y + 1 * 18] = t[i + 16] + t[i + 24] + t[i + 24 + 1];
        grbuf[y + 2 * 18] = t[i + 8] + t[i + 8 + 1];
        grbuf[y + 3 * 18] = t[i + 16 + 1] + t[i + 24] + t[i + 24 + 1];
      }
      grbuf[y + 0 * 18] = t[7];
      grbuf[y + 1 * 18] = t[16 + 7] + t[24 + 7];
      grbuf[y + 2 * 18] = t[8 + 7];
      grbuf[y + 3 * 18] = t[24 + 7];
    }
  }

  // Polyphase synthesis window (15 rows × 16 cols) — minimp3's g_win.
  private static readonly float[] _Win = {
    -1, 26, -31, 208, 218, 401, -519, 2063, 2000, 4788, -5517, 7134, 5959, 35640, -39336, 74992,
    -1, 24, -35, 202, 222, 347, -581, 2080, 1952, 4425, -5879, 7640, 5288, 33791, -41176, 74856,
    -1, 21, -38, 196, 225, 294, -645, 2087, 1893, 4063, -6237, 8092, 4561, 31947, -43006, 74630,
    -1, 19, -41, 190, 227, 244, -711, 2085, 1822, 3705, -6589, 8492, 3776, 30112, -44821, 74313,
    -1, 17, -45, 183, 228, 197, -779, 2075, 1739, 3351, -6935, 8840, 2935, 28289, -46617, 73908,
    -1, 16, -49, 176, 228, 153, -848, 2057, 1644, 3004, -7271, 9139, 2037, 26482, -48390, 73415,
    -2, 14, -53, 169, 227, 111, -919, 2032, 1535, 2663, -7597, 9389, 1082, 24694, -50137, 72835,
    -2, 13, -58, 161, 224, 72, -991, 2001, 1414, 2330, -7910, 9592, 70, 22929, -51853, 72169,
    -2, 11, -63, 154, 221, 36, -1064, 1962, 1280, 2006, -8209, 9750, -998, 21189, -53534, 71420,
    -2, 10, -68, 147, 215, 2, -1137, 1919, 1131, 1692, -8491, 9863, -2122, 19478, -55178, 70590,
    -3, 9, -73, 139, 208, -29, -1210, 1870, 970, 1388, -8755, 9935, -3300, 17799, -56778, 69679,
    -3, 8, -79, 132, 200, -57, -1283, 1817, 794, 1095, -8998, 9966, -4533, 16155, -58333, 68692,
    -4, 7, -85, 125, 189, -83, -1356, 1759, 605, 814, -9219, 9959, -5818, 14548, -59838, 67629,
    -4, 7, -91, 117, 177, -106, -1428, 1698, 402, 545, -9416, 9916, -7154, 12980, -61289, 66494,
    -5, 6, -97, 111, 163, -127, -1498, 1634, 185, 288, -9585, 9838, -8540, 11455, -62684, 65290
  };

  private static short ScalePcm(float sample) {
    if (sample >= 32766.5f) return 32767;
    if (sample <= -32767.5f) return -32768;
    var s = (short)(sample + 0.5f);
    if (s < 0) s -= 1;
    return s;
  }

  private static void SynthPair(short[] pcm, int pcmOff, int nch, float[] z, int zOff) {
    float a;
    a = (z[zOff + 14 * 64] - z[zOff + 0]) * 29;
    a += (z[zOff + 1 * 64] + z[zOff + 13 * 64]) * 213;
    a += (z[zOff + 12 * 64] - z[zOff + 2 * 64]) * 459;
    a += (z[zOff + 3 * 64] + z[zOff + 11 * 64]) * 2037;
    a += (z[zOff + 10 * 64] - z[zOff + 4 * 64]) * 5153;
    a += (z[zOff + 5 * 64] + z[zOff + 9 * 64]) * 6574;
    a += (z[zOff + 8 * 64] - z[zOff + 6 * 64]) * 37489;
    a += z[zOff + 7 * 64] * 75038;
    pcm[pcmOff + 0] = ScalePcm(a);

    zOff += 2;
    a = z[zOff + 14 * 64] * 104;
    a += z[zOff + 12 * 64] * 1567;
    a += z[zOff + 10 * 64] * 9727;
    a += z[zOff + 8 * 64] * 64019;
    a += z[zOff + 6 * 64] * -9975;
    a += z[zOff + 4 * 64] * -45;
    a += z[zOff + 2 * 64] * 146;
    a += z[zOff + 0 * 64] * -5;
    pcm[pcmOff + 16 * nch] = ScalePcm(a);
  }

  private static void Synth(float[] xl, int xlOff, short[] dstl, int dstlOff, int nch, float[] lins, int linsOff) {
    var xrOff = xlOff + 576 * (nch - 1);
    var dstrOff = dstlOff + (nch - 1);
    var zlinOff = linsOff + 15 * 64;
    var w = 0;

    lins[zlinOff + 4 * 15 + 0] = xl[xlOff + 18 * 16];
    lins[zlinOff + 4 * 15 + 1] = xl[xrOff + 18 * 16];
    lins[zlinOff + 4 * 15 + 2] = xl[xlOff + 0];
    lins[zlinOff + 4 * 15 + 3] = xl[xrOff + 0];

    lins[zlinOff + 4 * 31 + 0] = xl[xlOff + 1 + 18 * 16];
    lins[zlinOff + 4 * 31 + 1] = xl[xrOff + 1 + 18 * 16];
    lins[zlinOff + 4 * 31 + 2] = xl[xlOff + 1];
    lins[zlinOff + 4 * 31 + 3] = xl[xrOff + 1];

    SynthPair(dstl, dstrOff, nch, lins, linsOff + 4 * 15 + 1);
    SynthPair(dstl, dstrOff + 32 * nch, nch, lins, linsOff + 4 * 15 + 64 + 1);
    SynthPair(dstl, dstlOff, nch, lins, linsOff + 4 * 15);
    SynthPair(dstl, dstlOff + 32 * nch, nch, lins, linsOff + 4 * 15 + 64);

    for (var i = 14; i >= 0; i--) {
      var a = new float[4];
      var b = new float[4];

      lins[zlinOff + 4 * i + 0] = xl[xlOff + 18 * (31 - i)];
      lins[zlinOff + 4 * i + 1] = xl[xrOff + 18 * (31 - i)];
      lins[zlinOff + 4 * i + 2] = xl[xlOff + 1 + 18 * (31 - i)];
      lins[zlinOff + 4 * i + 3] = xl[xrOff + 1 + 18 * (31 - i)];
      lins[zlinOff + 4 * (i + 16) + 0] = xl[xlOff + 1 + 18 * (1 + i)];
      lins[zlinOff + 4 * (i + 16) + 1] = xl[xrOff + 1 + 18 * (1 + i)];
      lins[zlinOff + 4 * (i - 16) + 2] = xl[xlOff + 18 * (1 + i)];
      lins[zlinOff + 4 * (i - 16) + 3] = xl[xrOff + 18 * (1 + i)];

      // S0(0)
      var w0 = _Win[w++]; var w1 = _Win[w++];
      var vzOff = zlinOff + 4 * i - 0 * 64;
      var vyOff = zlinOff + 4 * i - 15 * 64;
      for (var j = 0; j < 4; j++) {
        b[j] = lins[vzOff + j] * w1 + lins[vyOff + j] * w0;
        a[j] = lins[vzOff + j] * w0 - lins[vyOff + j] * w1;
      }
      // S2(1) S1(2) S2(3) S1(4) S2(5) S1(6) S2(7) — pattern matches minimp3 macros
      // S1: a += vz*w0 - vy*w1; b += vz*w1 + vy*w0
      // S2: a += vy*w1 - vz*w0; b += vz*w1 + vy*w0
      Action<int, bool> step = (k, isS2) => {
        var lw0 = _Win[w++]; var lw1 = _Win[w++];
        var lvz = zlinOff + 4 * i - k * 64;
        var lvy = zlinOff + 4 * i - (15 - k) * 64;
        for (var j = 0; j < 4; j++) {
          b[j] += lins[lvz + j] * lw1 + lins[lvy + j] * lw0;
          if (isS2) a[j] += lins[lvy + j] * lw1 - lins[lvz + j] * lw0;
          else a[j] += lins[lvz + j] * lw0 - lins[lvy + j] * lw1;
        }
      };
      step(1, true);
      step(2, false);
      step(3, true);
      step(4, false);
      step(5, true);
      step(6, false);
      step(7, true);

      dstl[dstrOff + (15 - i) * nch] = ScalePcm(a[1]);
      dstl[dstrOff + (17 + i) * nch] = ScalePcm(b[1]);
      dstl[dstlOff + (15 - i) * nch] = ScalePcm(a[0]);
      dstl[dstlOff + (17 + i) * nch] = ScalePcm(b[0]);
      dstl[dstrOff + (47 - i) * nch] = ScalePcm(a[3]);
      dstl[dstrOff + (49 + i) * nch] = ScalePcm(b[3]);
      dstl[dstlOff + (47 - i) * nch] = ScalePcm(a[2]);
      dstl[dstlOff + (49 + i) * nch] = ScalePcm(b[2]);
    }
  }

  /// <summary>Synthesizes one granule's worth of subbands into PCM samples (32 per band × nbands × nch).</summary>
  public static void SynthGranule(float[] qmfState, float[] grbuf, int nbands, int nch, short[] pcm, int pcmOff, float[] lins) {
    for (var i = 0; i < nch; i++) DctII(grbuf, 576 * i, nbands);
    Array.Copy(qmfState, 0, lins, 0, 15 * 64);
    for (var i = 0; i < nbands; i += 2)
      Synth(grbuf, i, pcm, pcmOff + 32 * nch * i, nch, lins, i * 64);
    if (nch == 1) {
      for (var i = 0; i < 15 * 64; i += 2) qmfState[i] = lins[nbands * 64 + i];
    } else {
      Array.Copy(lins, nbands * 64, qmfState, 0, 15 * 64);
    }
  }
}
