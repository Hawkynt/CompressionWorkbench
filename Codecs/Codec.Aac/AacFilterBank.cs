#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// AAC filter bank: inverse MDCT (2048-point for long windows, 8×256-point for
/// short windows) plus sine/KBD (Kaiser-Bessel-Derived) window application and
/// overlap-add with the previous frame's tail (ISO/IEC 14496-3 §4.6.11).
/// <para>
/// The IMDCT is a direct definition-based transform (O(N²)). For a 2048-point
/// long window this is ~1M multiply-adds per channel per frame — slow but exact
/// and trivially verifiable, which is the right trade-off for a reference decoder
/// and the small test clips here. The transform cosine arguments are tabulated at
/// init so the inner loop is a table lookup.
/// </para>
/// <para>
/// Window shapes: the sine window is computed directly; the KBD window is derived
/// numerically from a Kaiser window (α=4 for long, α=6 for short) using the
/// standard cumulative-sum-of-squares derivation, matching ISO/IEC 14496-3 §4.6.11.2.4.
/// </para>
/// </summary>
internal static class AacFilterBank {

  public const int LongFrameSize = 1024;
  public const int ShortFrameSize = 128;
  public const int LongMdctSize = 2048;
  public const int ShortMdctSize = 256;

  // window_sequence values
  public const int OnlyLong = 0;
  public const int LongStart = 1;
  public const int EightShort = 2;
  public const int LongStop = 3;

  private static readonly float[] SineLong = MakeSineWindow(LongFrameSize);
  private static readonly float[] SineShort = MakeSineWindow(ShortFrameSize);
  private static readonly float[] KbdLong = MakeKbdWindow(LongFrameSize, 4.0);
  private static readonly float[] KbdShort = MakeKbdWindow(ShortFrameSize, 6.0);

  // IMDCT cosine tables: imdct[n*N + k] for the N=1024 and N=128 transforms.
  private static readonly float[] ImdctLong = MakeImdctTable(LongFrameSize);
  private static readonly float[] ImdctShort = MakeImdctTable(ShortFrameSize);

  /// <summary>
  /// Performs IMDCT + windowing + overlap-add for one channel and one frame,
  /// writing <see cref="LongFrameSize"/> PCM-domain samples to
  /// <paramref name="pcmOut"/>. <paramref name="overlap"/> carries the previous
  /// frame's second-half tail in and is updated with this frame's tail out.
  /// </summary>
  public static void Synthesize(
    float[] spectralInput,
    int windowSequence,
    int windowShape,
    int prevWindowShape,
    float[] overlap,
    float[] pcmOut) {

    if (windowSequence == EightShort)
      SynthesizeShort(spectralInput, windowShape, prevWindowShape, overlap, pcmOut);
    else
      SynthesizeLong(spectralInput, windowSequence, windowShape, prevWindowShape, overlap, pcmOut);
  }

  private static void SynthesizeLong(
    float[] spec, int windowSequence, int windowShape, int prevWindowShape,
    float[] overlap, float[] pcmOut) {
    const int n = LongFrameSize;
    var time = new float[2 * n];
    Imdct(spec, time, n, ImdctLong);

    // Left/right window halves: the left half shape depends on the previous frame
    // (LONG_STOP uses a short left half), the right half on this frame
    // (LONG_START uses a short right half).
    var leftPrev = LeftWindow(windowSequence, prevWindowShape);
    var rightThis = RightWindow(windowSequence, windowShape);

    for (var i = 0; i < n; ++i) {
      var windowed = time[i] * leftPrev[i];
      pcmOut[i] = windowed + overlap[i];
    }
    for (var i = 0; i < n; ++i)
      overlap[i] = time[n + i] * rightThis[i];
  }

  // Long left window (samples 0..1023). LONG_STOP transitions from a short window:
  // its first 448 samples are zero, then a 128-sample short rising edge, then ones.
  private static float[] LeftWindow(int windowSequence, int prevShape) {
    var w = prevShape == 1 ? KbdLong : SineLong;
    if (windowSequence != LongStop)
      return w; // ONLY_LONG / LONG_START share a full long rising left half
    const int n = LongFrameSize;
    var shortW = prevShape == 1 ? KbdShort : SineShort;
    var result = new float[n];
    for (var i = 0; i < n; ++i) {
      if (i < (n - ShortFrameSize) / 2) result[i] = 0f;
      else if (i < (n + ShortFrameSize) / 2) result[i] = shortW[i - (n - ShortFrameSize) / 2];
      else result[i] = 1f;
    }
    return result;
  }

  // Long right window (samples 1024..2047, i.e. the falling edge stored in overlap).
  private static float[] RightWindow(int windowSequence, int thisShape) {
    const int n = LongFrameSize;
    var w = thisShape == 1 ? KbdLong : SineLong;
    if (windowSequence != LongStart) {
      // ONLY_LONG / LONG_STOP: full long falling half = mirrored rising window.
      var result = new float[n];
      for (var i = 0; i < n; ++i) result[i] = w[n - 1 - i];
      return result;
    }
    // LONG_START: ones, then a 128-sample short falling edge, then zeros.
    var shortW = thisShape == 1 ? KbdShort : SineShort;
    var r = new float[n];
    for (var i = 0; i < n; ++i) {
      if (i < (n - ShortFrameSize) / 2) r[i] = 1f;
      else if (i < (n + ShortFrameSize) / 2) r[i] = shortW[ShortFrameSize - 1 - (i - (n - ShortFrameSize) / 2)];
      else r[i] = 0f;
    }
    return r;
  }

  // EIGHT_SHORT: eight overlapping 256-point IMDCTs spaced 128 samples apart,
  // centred inside the 1024-sample frame, overlap-added together and with the
  // previous frame's tail.
  private static void SynthesizeShort(
    float[] spec, int windowShape, int prevWindowShape, float[] overlap, float[] pcmOut) {
    const int n = ShortFrameSize;
    const int offset = (LongFrameSize - ShortFrameSize) / 2; // 448
    var acc = new float[LongFrameSize + offset + ShortFrameSize]; // working area incl. tail
    var leftW = prevWindowShape == 1 ? KbdShort : SineShort;
    var thisW = windowShape == 1 ? KbdShort : SineShort;

    for (var win = 0; win < 8; ++win) {
      var sub = new float[n];
      Array.Copy(spec, win * n, sub, 0, n);
      var time = new float[2 * n];
      Imdct(sub, time, n, ImdctShort);
      var start = offset + win * n;
      for (var i = 0; i < n; ++i)
        acc[start + i] += time[i] * leftW[i];
      for (var i = 0; i < n; ++i)
        acc[start + n + i] += time[n + i] * thisW[n - 1 - i];
    }

    for (var i = 0; i < LongFrameSize; ++i)
      pcmOut[i] = acc[i] + overlap[i];
    for (var i = 0; i < LongFrameSize; ++i)
      overlap[i] = acc[LongFrameSize + i];
  }

  // Direct IMDCT: time[n] = sum_k spec[k]·cos( (pi/N)·(n + N/2 + 1/2)·(2k+1) ),
  // for n = 0..2N-1 (ISO/IEC 14496-3 §4.6.11.2.2). Input length N, output 2N.
  private static void Imdct(float[] spec, float[] time, int nHalf, float[] cosTable) {
    var twoN = 2 * nHalf;
    for (var nIdx = 0; nIdx < twoN; ++nIdx) {
      float sum = 0;
      var rowBase = nIdx * nHalf;
      for (var k = 0; k < nHalf; ++k)
        sum += spec[k] * cosTable[rowBase + k];
      time[nIdx] = sum * (2f / nHalf);
    }
  }

  private static float[] MakeImdctTable(int nHalf) {
    var twoN = 2 * nHalf;
    var table = new float[twoN * nHalf];
    var n0 = (nHalf / 2.0) + 0.5; // N/2 + 1/2 with N = nHalf
    for (var nIdx = 0; nIdx < twoN; ++nIdx)
      for (var k = 0; k < nHalf; ++k)
        table[nIdx * nHalf + k] =
          (float)Math.Cos(Math.PI / nHalf * (nIdx + n0) * (2 * k + 1));
    return table;
  }

  private static float[] MakeSineWindow(int n) {
    var w = new float[n];
    for (var i = 0; i < n; ++i)
      w[i] = (float)Math.Sin(Math.PI / (2.0 * n) * (i + 0.5));
    return w;
  }

  // Kaiser-Bessel-Derived window of length n from a Kaiser window with parameter
  // alpha. The KBD window of length N is the square root of the running
  // normalised cumulative sum of the length-(N+1) Kaiser window's squared values.
  private static float[] MakeKbdWindow(int n, double alpha) {
    var kaiser = new double[n + 1];
    var denom = BesselI0(Math.PI * alpha);
    for (var i = 0; i <= n; ++i) {
      var ratio = (2.0 * i / n) - 1.0;
      kaiser[i] = BesselI0(Math.PI * alpha * Math.Sqrt(1.0 - ratio * ratio)) / denom;
    }
    var total = 0.0;
    for (var i = 0; i <= n; ++i) total += kaiser[i];
    var w = new float[n];
    var running = 0.0;
    for (var i = 0; i < n; ++i) {
      running += kaiser[i];
      w[i] = (float)Math.Sqrt(running / total);
    }
    return w;
  }

  private static double BesselI0(double x) {
    var sum = 1.0;
    var term = 1.0;
    var halfX = x / 2.0;
    for (var k = 1; k < 64; ++k) {
      term *= (halfX / k) * (halfX / k);
      sum += term;
      if (term < sum * 1e-16) break;
    }
    return sum;
  }
}
