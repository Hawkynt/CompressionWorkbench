#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Scaled first-order filter (reference <c>CScaledFirstOrderFilter&lt;31,5&gt;</c>):
/// a one-tap leaky integrator with multiply 31, shift 5. <see cref="Compress"/> and
/// <see cref="Decompress"/> are exact inverses.
/// </summary>
internal sealed class ScaledFirstOrderFilter {
  private const int Multiply = 31;
  private const int Shift = 5;
  private int _last;

  public void Flush() => this._last = 0;

  public int Compress(int input) {
    var ret = input - ((this._last * Multiply) >> Shift);
    this._last = input;
    return ret;
  }

  public int Decompress(int input) {
    this._last = input + ((this._last * Multiply) >> Shift);
    return this._last;
  }
}

/// <summary>
/// Sign-LMS neural-network pre-filter (reference <c>CNNFilter</c>) applied for
/// compression levels 2000+ before the order-4 dual predictor. A short-coefficient
/// adaptive filter of the given order and fixed-point shift, with the v3.98+ three
/// step adaption. <see cref="Compress"/>/<see cref="Decompress"/> are exact inverses.
/// </summary>
internal sealed class NnFilter {
  private const int WindowElements = 512;

  private readonly int _order;
  private readonly int _shift;
  private readonly short[] _input;
  private readonly short[] _deltaM;
  private readonly short[] _m;
  private int _inputPos;  // index of element [0] within _input
  private int _deltaPos;  // index of element [0] within _deltaM
  private int _runningAverage;

  public NnFilter(int order, int shift) {
    this._order = order;
    this._shift = shift;
    this._input = new short[WindowElements + order];
    this._deltaM = new short[WindowElements + order];
    this._m = new short[order];
    this.Flush();
  }

  public void Flush() {
    Array.Clear(this._m);
    Array.Clear(this._input);
    Array.Clear(this._deltaM);
    this._inputPos = this._order;
    this._deltaPos = this._order;
    this._runningAverage = 0;
  }

  private static short Sat(int v) => v switch {
    > short.MaxValue => short.MaxValue,
    < short.MinValue => short.MinValue,
    _ => (short)v,
  };

  private int DotProduct() {
    var acc = 0;
    var p = this._inputPos - this._order;
    for (var i = 0; i < this._order; ++i)
      acc += this._input[p + i] * this._m[i];
    return acc;
  }

  private void Adapt(int direction) {
    var p = this._deltaPos - this._order;
    if (direction < 0)
      for (var i = 0; i < this._order; ++i)
        this._m[i] += this._deltaM[p + i];
    else if (direction > 0)
      for (var i = 0; i < this._order; ++i)
        this._m[i] -= this._deltaM[p + i];
  }

  private void IncrementSafe() {
    ++this._inputPos;
    ++this._deltaPos;
    if (this._inputPos == WindowElements + this._order) {
      Array.Copy(this._input, this._inputPos - this._order, this._input, 0, this._order);
      Array.Copy(this._deltaM, this._deltaPos - this._order, this._deltaM, 0, this._order);
      this._inputPos = this._order;
      this._deltaPos = this._order;
    }
  }

  public int Compress(int input) {
    this._input[this._inputPos] = Sat(input);
    var dot = this.DotProduct();
    var output = input - ((dot + (1 << (this._shift - 1))) >> this._shift);
    this.Adapt(output);
    UpdateDelta(this._deltaM, this._deltaPos, ref this._runningAverage, input);
    this.IncrementSafe();
    return output;
  }

  public int Decompress(int input) {
    var dot = this.DotProduct();
    this.Adapt(input);
    var output = input + ((dot + (1 << (this._shift - 1))) >> this._shift);
    this._input[this._inputPos] = Sat(output);
    UpdateDelta(this._deltaM, this._deltaPos, ref this._runningAverage, output);
    this.IncrementSafe();
    return output;
  }

  // v3.98+ adaption (CNNFilter, m_nVersion >= 3980 branch).
  private static void UpdateDelta(short[] delta, int pos, ref int runningAverage, int value) {
    var abs = Math.Abs(value);
    if (abs > runningAverage * 3)
      delta[pos] = (short)(((value >> 25) & 64) - 32);
    else if (abs > runningAverage * 4 / 3)
      delta[pos] = (short)(((value >> 26) & 32) - 16);
    else if (abs > 0)
      delta[pos] = (short)(((value >> 27) & 16) - 8);
    else
      delta[pos] = 0;

    runningAverage += (abs - runningAverage) / 16;

    delta[pos - 1] >>= 1;
    delta[pos - 2] >>= 1;
    delta[pos - 8] >>= 1;
  }
}

/// <summary>
/// Monkey's Audio v3.95+ ("3950 to current") inverse predictor — a byte-exact port
/// of the reference SDK's <c>CPredictorDecompress3950toCurrent</c>. Two order-4 /
/// order-5 adaptive cross-channel filters (the X / Y "A" and "B" stages) feeding a
/// scaled first-order filter, preceded by the level-dependent <see cref="NnFilter"/>
/// cascade. One instance per channel; the second channel's reconstructed value is
/// passed in as the cross-prediction input.
/// </summary>
internal sealed class ApePredictorDecompress {
  private const int WindowBlocks = 512;
  private const int MCount = 8;

  private readonly NnFilter? _nn;
  private readonly NnFilter? _nn1;
  private readonly NnFilter? _nn2;

  private readonly int[] _aryMA = new int[MCount];
  private readonly int[] _aryMB = new int[MCount];

  // Rolling history buffers (CRollBufferFast<int, 512, 8>): flat array + current pos.
  private readonly int[] _predA = new int[WindowBlocks + 8];
  private readonly int[] _predB = new int[WindowBlocks + 8];
  private readonly int[] _adaptA = new int[WindowBlocks + 8];
  private readonly int[] _adaptB = new int[WindowBlocks + 8];
  private int _pa, _pb, _aa, _ab;

  private readonly ScaledFirstOrderFilter _stage1A = new();
  private readonly ScaledFirstOrderFilter _stage1B = new();

  private int _currentIndex;
  private int _lastValueA;

  public ApePredictorDecompress(int compressionLevel) {
    (this._nn, this._nn1, this._nn2) = MakeFilters(compressionLevel);
    this.Flush();
  }

  internal static (NnFilter?, NnFilter?, NnFilter?) MakeFilters(int level) => level switch {
    MonkeysAudioCodec.CompressionFast => (null, null, null),
    MonkeysAudioCodec.CompressionNormal => (new NnFilter(16, 11), null, null),
    MonkeysAudioCodec.CompressionHigh => (new NnFilter(64, 11), null, null),
    MonkeysAudioCodec.CompressionExtraHigh => (new NnFilter(256, 13), new NnFilter(32, 10), null),
    MonkeysAudioCodec.CompressionInsane => (new NnFilter(1024 + 256, 15), new NnFilter(256, 13), new NnFilter(16, 11)),
    _ => throw new NotSupportedException($"Unsupported Monkey's Audio compression level: {level}."),
  };

  public void Flush() {
    this._nn?.Flush();
    this._nn1?.Flush();
    this._nn2?.Flush();
    Array.Clear(this._aryMA);
    Array.Clear(this._aryMB);
    Array.Clear(this._predA);
    Array.Clear(this._predB);
    Array.Clear(this._adaptA);
    Array.Clear(this._adaptB);
    this._pa = this._pb = this._aa = this._ab = 8;
    this._aryMA[0] = 360;
    this._aryMA[1] = 317;
    this._aryMA[2] = -109;
    this._aryMA[3] = 98;
    this._stage1A.Flush();
    this._stage1B.Flush();
    this._lastValueA = 0;
    this._currentIndex = 0;
  }

  private void Roll() {
    Array.Copy(this._predA, this._pa - 8, this._predA, 0, 8);
    Array.Copy(this._predB, this._pb - 8, this._predB, 0, 8);
    Array.Copy(this._adaptA, this._aa - 8, this._adaptA, 0, 8);
    Array.Copy(this._adaptB, this._ab - 8, this._adaptB, 0, 8);
    this._pa = this._pb = this._aa = this._ab = 8;
  }

  public int DecompressValue(int nA, int nB) {
    if (this._currentIndex == WindowBlocks) {
      this.Roll();
      this._currentIndex = 0;
    }

    if (this._nn2 != null) nA = this._nn2.Decompress(nA);
    if (this._nn1 != null) nA = this._nn1.Decompress(nA);
    if (this._nn != null) nA = this._nn.Decompress(nA);

    this._predA[this._pa] = this._lastValueA;
    this._predA[this._pa - 1] = this._predA[this._pa] - this._predA[this._pa - 1];

    this._predB[this._pb] = this._stage1B.Compress(nB);
    this._predB[this._pb - 1] = this._predB[this._pb] - this._predB[this._pb - 1];

    var predictionA = this._predA[this._pa] * this._aryMA[0]
                    + this._predA[this._pa - 1] * this._aryMA[1]
                    + this._predA[this._pa - 2] * this._aryMA[2]
                    + this._predA[this._pa - 3] * this._aryMA[3];
    var predictionB = this._predB[this._pb] * this._aryMB[0]
                    + this._predB[this._pb - 1] * this._aryMB[1]
                    + this._predB[this._pb - 2] * this._aryMB[2]
                    + this._predB[this._pb - 3] * this._aryMB[3]
                    + this._predB[this._pb - 4] * this._aryMB[4];

    var currentA = nA + ((predictionA + (predictionB >> 1)) >> 10);

    this._adaptA[this._aa] = this._predA[this._pa] != 0 ? ((this._predA[this._pa] >> 30) & 2) - 1 : 0;
    this._adaptA[this._aa - 1] = this._predA[this._pa - 1] != 0 ? ((this._predA[this._pa - 1] >> 30) & 2) - 1 : 0;
    this._adaptB[this._ab] = this._predB[this._pb] != 0 ? ((this._predB[this._pb] >> 30) & 2) - 1 : 0;
    this._adaptB[this._ab - 1] = this._predB[this._pb - 1] != 0 ? ((this._predB[this._pb - 1] >> 30) & 2) - 1 : 0;

    if (nA > 0) {
      this._aryMA[0] -= this._adaptA[this._aa];
      this._aryMA[1] -= this._adaptA[this._aa - 1];
      this._aryMA[2] -= this._adaptA[this._aa - 2];
      this._aryMA[3] -= this._adaptA[this._aa - 3];
      this._aryMB[0] -= this._adaptB[this._ab];
      this._aryMB[1] -= this._adaptB[this._ab - 1];
      this._aryMB[2] -= this._adaptB[this._ab - 2];
      this._aryMB[3] -= this._adaptB[this._ab - 3];
      this._aryMB[4] -= this._adaptB[this._ab - 4];
    } else if (nA < 0) {
      this._aryMA[0] += this._adaptA[this._aa];
      this._aryMA[1] += this._adaptA[this._aa - 1];
      this._aryMA[2] += this._adaptA[this._aa - 2];
      this._aryMA[3] += this._adaptA[this._aa - 3];
      this._aryMB[0] += this._adaptB[this._ab];
      this._aryMB[1] += this._adaptB[this._ab - 1];
      this._aryMB[2] += this._adaptB[this._ab - 2];
      this._aryMB[3] += this._adaptB[this._ab - 3];
      this._aryMB[4] += this._adaptB[this._ab - 4];
    }

    var retVal = this._stage1A.Decompress(currentA);
    this._lastValueA = currentA;

    ++this._pa;
    ++this._pb;
    ++this._aa;
    ++this._ab;
    ++this._currentIndex;

    return retVal;
  }
}

/// <summary>
/// Monkey's Audio forward predictor — a byte-exact port of the reference SDK's
/// <c>CPredictorCompressNormal::CompressValue</c>. It produces exactly the residual
/// stream the reference encoder would, so the bytes the entropy/range stage emits
/// are reference-faithful and round-trip through <see cref="ApePredictorDecompress"/>
/// (and through ffmpeg). One instance per channel.
/// </summary>
internal sealed class ApePredictorCompress {
  private const int WindowBlocks = 512;

  private readonly NnFilter? _nn;
  private readonly NnFilter? _nn1;
  private readonly NnFilter? _nn2;

  // m_rbPrediction history 10, m_rbAdapt history 9 (CRollBufferFast).
  private readonly int[] _pred = new int[WindowBlocks + 10];
  private readonly int[] _adapt = new int[WindowBlocks + 9];
  private int _pp, _ap;

  private readonly ScaledFirstOrderFilter _stage1A = new();
  private readonly ScaledFirstOrderFilter _stage1B = new();

  // m_aryM[9]; reference indexes via paryM = &m_aryM[8].
  private readonly int[] _aryM = new int[9];

  private int _currentIndex;

  public ApePredictorCompress(int compressionLevel) {
    (this._nn, this._nn1, this._nn2) = ApePredictorDecompress.MakeFilters(compressionLevel);
    this.Flush();
  }

  public void Flush() {
    this._nn?.Flush();
    this._nn1?.Flush();
    this._nn2?.Flush();
    Array.Clear(this._pred);
    Array.Clear(this._adapt);
    this._pp = 10;
    this._ap = 9;
    this._stage1A.Flush();
    this._stage1B.Flush();
    Array.Clear(this._aryM);
    this._aryM[8] = 360;
    this._aryM[7] = 317;
    this._aryM[6] = -109;
    this._aryM[5] = 98;
    this._currentIndex = 0;
  }

  private void Roll() {
    Array.Copy(this._pred, this._pp - 10, this._pred, 0, 10);
    Array.Copy(this._adapt, this._ap - 9, this._adapt, 0, 9);
    this._pp = 10;
    this._ap = 9;
  }

  public int CompressValue(int nA, int nB) {
    if (this._currentIndex == WindowBlocks) {
      this.Roll();
      this._currentIndex = 0;
    }

    nA = this._stage1A.Compress(nA);
    nB = this._stage1B.Compress(nB);

    this._pred[this._pp] = nA;
    this._pred[this._pp - 2] = this._pred[this._pp - 1] - this._pred[this._pp - 2];
    this._pred[this._pp - 5] = nB;
    this._pred[this._pp - 6] = this._pred[this._pp - 5] - this._pred[this._pp - 6];

    // paryM = &m_aryM[8]; paryM[0..-3] are A taps, paryM[-4..-8] are B taps.
    var predictionA = this._pred[this._pp - 1] * this._aryM[8]
                    + this._pred[this._pp - 2] * this._aryM[7]
                    + this._pred[this._pp - 3] * this._aryM[6]
                    + this._pred[this._pp - 4] * this._aryM[5];
    var predictionB = this._pred[this._pp - 5] * this._aryM[4]
                    + this._pred[this._pp - 6] * this._aryM[3]
                    + this._pred[this._pp - 7] * this._aryM[2]
                    + this._pred[this._pp - 8] * this._aryM[1]
                    + this._pred[this._pp - 9] * this._aryM[0];

    var output = nA - ((predictionA + (predictionB >> 1)) >> 10);

    this._adapt[this._ap] = this._pred[this._pp - 1] != 0 ? ((this._pred[this._pp - 1] >> 30) & 2) - 1 : 0;
    this._adapt[this._ap - 1] = this._pred[this._pp - 2] != 0 ? ((this._pred[this._pp - 2] >> 30) & 2) - 1 : 0;
    this._adapt[this._ap - 4] = this._pred[this._pp - 5] != 0 ? ((this._pred[this._pp - 5] >> 30) & 2) - 1 : 0;
    this._adapt[this._ap - 5] = this._pred[this._pp - 6] != 0 ? ((this._pred[this._pp - 6] >> 30) & 2) - 1 : 0;

    if (output > 0)
      for (var i = 0; i < 9; ++i)
        this._aryM[i] -= this._adapt[this._ap - 8 + i];
    else if (output < 0)
      for (var i = 0; i < 9; ++i)
        this._aryM[i] += this._adapt[this._ap - 8 + i];

    if (this._nn != null) {
      output = this._nn.Compress(output);
      if (this._nn1 != null) {
        output = this._nn1.Compress(output);
        if (this._nn2 != null)
          output = this._nn2.Compress(output);
      }
    }

    ++this._pp;
    ++this._ap;
    ++this._currentIndex;

    return output;
  }
}
