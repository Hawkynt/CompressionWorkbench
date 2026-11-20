using Compression.Core.Dictionary.Sqx;

namespace Compression.Core.Entropy.Ppmd;

public sealed partial class Ppmd8 {
  /// <summary>
  /// Decodes one symbol of an SQX 2.0 method 6 stream: the same model, driven by SQX's carryless
  /// range coder (absolute code, 2^16 bottom), whose escape total is capped at the coder's range.
  /// Converted from Squeez-c (MIT) src/ppm8.c, decode_symbol. Returns a byte, or a negative value
  /// for data no encoder wrote.
  /// </summary>
  internal int DecodeSymbolSqx(SqxPpmdRangeDecoder rc) {
    Span<bool> masked = stackalloc bool[256];
    var minContext = this._minContext;
    if (this.NumStats(minContext) != 0) {
      var s = this.Stats(minContext);
      var summ = this.SummFreq(minContext);
      var count = rc.Threshold(summ);
      var hiCnt = count;
      if ((int)(count -= this.Freq(s)) < 0) {
        rc.Decode(0, this.Freq(s));
        rc.Normalize();
        this._foundState = s;
        var symbol = (int)this.Symbol(s);
        this.Update1First();
        return symbol;
      }
      this._prevSuccess = 0;
      var i = this.NumStats(minContext);
      do {
        s += Ppmd8.StateSize;
        if ((int)(count -= this.Freq(s)) < 0) {
          rc.Decode(hiCnt - count - this.Freq(s), this.Freq(s));
          rc.Normalize();
          this._foundState = s;
          var symbol = (int)this.Symbol(s);
          this.Update1();
          return symbol;
        }
      } while (--i != 0);
      if (hiCnt >= summ)
        return Ppmd8.DataError;
      hiCnt -= count;
      rc.Decode(hiCnt, summ - hiCnt);
      for (var q = this.Stats(minContext); q <= s; q += Ppmd8.StateSize)
        masked[(int)this.Symbol(q)] = true;
    } else {
      var prob = this.BinSummIndex();
      var old = (uint)this._binSumm[prob];
      var q14 = rc.Range >> 14;
      var size0 = q14 * old;
      var updated = old - (uint)GetMean((int)old);
      if (rc.Offset < size0) {
        this._binSumm[prob] = (ushort)(updated + (1 << Ppmd8.IntBits));
        rc.Range = size0;
        rc.Normalize();
        this._foundState = OneState(minContext);
        var symbol = (int)this.Symbol(this._foundState);
        this.UpdateBin();
        return symbol;
      }
      this._binSumm[prob] = (ushort)updated;
      this._initEsc = Ppmd8.ExpEscape[updated >> 10];
      rc.Low += size0;
      rc.Range = ((uint)Ppmd8.BinScale - old) * q14;
      this._prevSuccess = 0;
      masked[(int)this.Symbol(OneState(minContext))] = true;
    }

    Span<uint> states = stackalloc uint[256];
    for (;;) {
      rc.Normalize();
      var numMasked = this.NumStats(this._minContext);
      do {
        ++this._orderFall;
        if (this.Suffix(this._minContext) == 0)
          return Ppmd8.EndMarker;
        this._minContext = this.Suffix(this._minContext);
      } while (this.NumStats(this._minContext) == numMasked);

      // the unmasked states of the lower context, as many as it has beyond the masked ones
      var numStats = this.NumStats(this._minContext);
      var want = numStats > numMasked ? numStats - numMasked : 0;
      var hiCnt = 0u;
      var found = 0;
      var st = this.Stats(this._minContext);
      for (var pos = 0u; pos <= numStats && want > 0; ++pos, st += Ppmd8.StateSize)
        if (!masked[(int)this.Symbol(st)]) {
          states[found++] = st;
          hiCnt += this.Freq(st);
          --want;
        }

      var see = this.MakeEscFreq(numMasked, out var freqSum);
      freqSum += hiCnt;
      var capped = Math.Min(freqSum, rc.Range);
      var count = rc.Threshold(capped);
      if (count < hiCnt) {
        hiCnt = count;
        var s = states[0];
        for (var k = 0; k < found; ++k) {
          s = states[k];
          count -= this.Freq(s);
          if ((int)count < 0)
            break;
        }
        rc.Decode(hiCnt - count - this.Freq(s), this.Freq(s));
        rc.Normalize();
        this.SeeUpdate(see);
        this._foundState = s;
        var symbol = (int)this.Symbol(s);
        this.Update2();
        return symbol;
      }
      if (count >= capped)
        return Ppmd8.DataError;
      rc.Decode(hiCnt, capped - hiCnt);
      this._seeSumm[see] = (ushort)(this._seeSumm[see] + freqSum);
      for (var k = 0; k < found; ++k)
        masked[(int)this.Symbol(states[k])] = true;
    }
  }
}
