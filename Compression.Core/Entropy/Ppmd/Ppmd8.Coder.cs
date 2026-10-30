namespace Compression.Core.Entropy.Ppmd;

public sealed partial class Ppmd8 {
  /// <summary>The symbol <see cref="DecodeSymbol"/> returns for the end-of-data marker.</summary>
  public const int EndMarker = -1;

  /// <summary>The symbol <see cref="DecodeSymbol"/> returns for data no encoder can have written.</summary>
  public const int DataError = -2;

  private const uint Top = 1 << 24;
  private const uint Bottom = 1 << 15;

  private uint _range, _code, _low;
  private byte[] _input = [];
  private int _inputPos;
  private Stream? _output;

  /// <summary>Bytes the decoder read past the end of its input (each read as zero).</summary>
  public int InputOverrun { get; private set; }

  /// <summary>True once the decoder has consumed the encoder's final flush exactly.</summary>
  public bool DecoderFinishedOk => this._code == 0;

  /// <summary>Position of the next unread input byte.</summary>
  public int InputPosition => this._inputPos;

  // ---------- decoding ----------

  /// <summary>Starts decoding <paramref name="input"/> from <paramref name="offset"/>.</summary>
  /// <returns>False when the first four bytes cannot start a valid stream.</returns>
  public bool StartDecoding(byte[] input, int offset) {
    this._input = input;
    this._inputPos = offset;
    this._low = 0;
    this._range = 0xFFFFFFFF;
    this._code = 0;
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this.NextByte();

    return this._code < 0xFFFFFFFF;
  }

  private uint NextByte() {
    if (this._inputPos < this._input.Length)
      return this._input[this._inputPos++];

    ++this.InputOverrun;
    return 0;
  }

  private uint GetThreshold(uint total) => this._code / (this._range /= total);

  private void Decode(uint start, uint size) {
    start *= this._range;
    this._low += start;
    this._code -= start;
    this._range *= size;
    for (;;) {
      if ((this._low ^ (this._low + this._range)) >= Ppmd8.Top) {
        if (this._range >= Ppmd8.Bottom)
          break;

        this._range = (0 - this._low) & (Ppmd8.Bottom - 1);
      }

      this._code = (this._code << 8) | this.NextByte();
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  /// <summary>Decodes one symbol: a byte, <see cref="EndMarker"/> or <see cref="DataError"/>.</summary>
  public int DecodeSymbol() {
    Span<sbyte> charMask = stackalloc sbyte[256];
    var minContext = this._minContext;
    if (this.NumStats(minContext) != 0) {
      var s = this.Stats(minContext);
      uint hiCnt;
      var count = this.GetThreshold(this.SummFreq(minContext));
      if (count < (hiCnt = this.Freq(s))) {
        this.Decode(0, this.Freq(s));
        this._foundState = s;
        var symbol = (int)this.Symbol(s);
        this.Update1First();
        return symbol;
      }

      this._prevSuccess = 0;
      var i = this.NumStats(minContext);
      do {
        s += Ppmd8.StateSize;
        if ((hiCnt += this.Freq(s)) > count) {
          this.Decode(hiCnt - this.Freq(s), this.Freq(s));
          this._foundState = s;
          var symbol = (int)this.Symbol(s);
          this.Update1();
          return symbol;
        }
      } while (--i != 0);

      if (count >= this.SummFreq(minContext))
        return Ppmd8.DataError;

      this.Decode(hiCnt, this.SummFreq(minContext) - hiCnt);
      charMask.Fill(-1);
      charMask[(int)this.Symbol(s)] = 0;
      i = this.NumStats(minContext);
      do {
        s -= Ppmd8.StateSize;
        charMask[(int)this.Symbol(s)] = 0;
      } while (--i != 0);
    } else {
      var prob = this.BinSummIndex();
      if (this._code / (this._range >>= 14) < this._binSumm[prob]) {
        this.Decode(0, this._binSumm[prob]);
        this._binSumm[prob] = (ushort)(this._binSumm[prob] + (1 << Ppmd8.IntBits) - GetMean(this._binSumm[prob]));
        this._foundState = OneState(minContext);
        var symbol = (int)this.Symbol(this._foundState);
        this.UpdateBin();
        return symbol;
      }

      this.Decode(this._binSumm[prob], (1u << 14) - this._binSumm[prob]);
      this._binSumm[prob] = (ushort)(this._binSumm[prob] - GetMean(this._binSumm[prob]));
      this._initEsc = Ppmd8.ExpEscape[this._binSumm[prob] >> 10];
      charMask.Fill(-1);
      charMask[(int)this.Symbol(OneState(minContext))] = 0;
      this._prevSuccess = 0;
    }

    Span<uint> ps = stackalloc uint[256];
    for (;;) {
      var numMasked = this.NumStats(this._minContext);
      do {
        ++this._orderFall;
        if (this.Suffix(this._minContext) == 0)
          return Ppmd8.EndMarker;

        this._minContext = this.Suffix(this._minContext);
      } while (this.NumStats(this._minContext) == numMasked);

      var hiCnt = 0u;
      var s = this.Stats(this._minContext);
      var i = 0u;
      var num = this.NumStats(this._minContext) - numMasked;
      do {
        var k = charMask[(int)this.Symbol(s)];
        hiCnt += this.Freq(s) & (uint)k;
        ps[(int)i] = s;
        s += Ppmd8.StateSize;
        i -= (uint)k;
      } while (i != num);

      var see = this.MakeEscFreq(numMasked, out var freqSum);
      freqSum += hiCnt;
      var count = this.GetThreshold(freqSum);

      if (count < hiCnt) {
        var at = 0;
        for (hiCnt = 0; (hiCnt += this.Freq(ps[at])) <= count; ++at) { }

        s = ps[at];
        this.Decode(hiCnt - this.Freq(s), this.Freq(s));
        this.SeeUpdate(see);
        this._foundState = s;
        var symbol = (int)this.Symbol(s);
        this.Update2();
        return symbol;
      }

      if (count >= freqSum)
        return Ppmd8.DataError;

      this.Decode(hiCnt, freqSum - hiCnt);
      this._seeSumm[see] = (ushort)(this._seeSumm[see] + freqSum);
      do {
        charMask[(int)this.Symbol(ps[(int)--i])] = 0;
      } while (i != 0);
    }
  }

  // ---------- encoding ----------

  /// <summary>Starts encoding to <paramref name="output"/>.</summary>
  public void StartEncoding(Stream output) {
    this._output = output;
    this._low = 0;
    this._range = 0xFFFFFFFF;
  }

  /// <summary>Writes the encoder's final four bytes.</summary>
  public void FlushEncoder() {
    for (var i = 0; i < 4; ++i, this._low <<= 8)
      this._output!.WriteByte((byte)(this._low >> 24));
  }

  private void EncoderNormalize() {
    for (;;) {
      if ((this._low ^ (this._low + this._range)) >= Ppmd8.Top) {
        if (this._range >= Ppmd8.Bottom)
          break;

        this._range = (0 - this._low) & (Ppmd8.Bottom - 1);
      }

      this._output!.WriteByte((byte)(this._low >> 24));
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  private void Encode(uint start, uint size, uint total) {
    this._low += start * (this._range /= total);
    this._range *= size;
    this.EncoderNormalize();
  }

  private void EncodeBit0(uint size0) {
    this._range >>= 14;
    this._range *= size0;
    this.EncoderNormalize();
  }

  private void EncodeBit1(uint size0) {
    this._low += size0 * (this._range >>= 14);
    this._range *= (1u << 14) - size0;
    this.EncoderNormalize();
  }

  /// <summary>Encodes a byte, or <see cref="EndMarker"/>.</summary>
  public void EncodeSymbol(int symbol) {
    Span<sbyte> charMask = stackalloc sbyte[256];
    var minContext = this._minContext;
    if (this.NumStats(minContext) != 0) {
      var s = this.Stats(minContext);
      if (this.Symbol(s) == symbol) {
        this.Encode(0, this.Freq(s), this.SummFreq(minContext));
        this._foundState = s;
        this.Update1First();
        return;
      }

      this._prevSuccess = 0;
      var sum = this.Freq(s);
      var i = this.NumStats(minContext);
      do {
        s += Ppmd8.StateSize;
        if (this.Symbol(s) == symbol) {
          this.Encode(sum, this.Freq(s), this.SummFreq(minContext));
          this._foundState = s;
          this.Update1();
          return;
        }

        sum += this.Freq(s);
      } while (--i != 0);

      charMask.Fill(-1);
      charMask[(int)this.Symbol(s)] = 0;
      i = this.NumStats(minContext);
      do {
        s -= Ppmd8.StateSize;
        charMask[(int)this.Symbol(s)] = 0;
      } while (--i != 0);

      this.Encode(sum, this.SummFreq(minContext) - sum, this.SummFreq(minContext));
    } else {
      var prob = this.BinSummIndex();
      var s = OneState(minContext);
      if (this.Symbol(s) == symbol) {
        this.EncodeBit0(this._binSumm[prob]);
        this._binSumm[prob] = (ushort)(this._binSumm[prob] + (1 << Ppmd8.IntBits) - GetMean(this._binSumm[prob]));
        this._foundState = s;
        this.UpdateBin();
        return;
      }

      this.EncodeBit1(this._binSumm[prob]);
      this._binSumm[prob] = (ushort)(this._binSumm[prob] - GetMean(this._binSumm[prob]));
      this._initEsc = Ppmd8.ExpEscape[this._binSumm[prob] >> 10];
      charMask.Fill(-1);
      charMask[(int)this.Symbol(s)] = 0;
      this._prevSuccess = 0;
    }

    for (;;) {
      var numMasked = this.NumStats(this._minContext);
      do {
        ++this._orderFall;
        if (this.Suffix(this._minContext) == 0)
          return; // only the end marker escapes past the order-0 context

        this._minContext = this.Suffix(this._minContext);
      } while (this.NumStats(this._minContext) == numMasked);

      var see = this.MakeEscFreq(numMasked, out var escFreq);
      var s = this.Stats(this._minContext);
      var sum = 0u;
      var i = this.NumStats(this._minContext) + 1;
      do {
        var current = (int)this.Symbol(s);
        if (current == symbol) {
          var low = sum;
          var found = s;
          do {
            sum += this.Freq(s) & (uint)charMask[(int)this.Symbol(s)];
            s += Ppmd8.StateSize;
          } while (--i != 0);

          this.Encode(low, this.Freq(found), sum + escFreq);
          this.SeeUpdate(see);
          this._foundState = found;
          this.Update2();
          return;
        }

        sum += this.Freq(s) & (uint)charMask[current];
        charMask[current] = 0;
        s += Ppmd8.StateSize;
      } while (--i != 0);

      this.Encode(sum, escFreq, sum + escFreq);
      this._seeSumm[see] = (ushort)(this._seeSumm[see] + sum + escFreq);
    }
  }

  private static int GetMean(int prob) => (prob + (1 << (Ppmd8.PeriodBits - 2))) >> Ppmd8.PeriodBits;
}
