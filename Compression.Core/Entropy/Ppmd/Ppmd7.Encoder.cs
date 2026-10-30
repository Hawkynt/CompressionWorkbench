namespace Compression.Core.Entropy.Ppmd;

/// <summary>The range encoder a <see cref="Ppmd7"/> model writes its symbols to.</summary>
public interface IPpmd7RangeEncoder {
  /// <summary>Codes the interval [<paramref name="start"/>, <paramref name="start"/> + <paramref name="size"/>) of <paramref name="total"/>.</summary>
  void Encode(uint start, uint size, uint total);

  /// <summary>Codes a binary decision of value <paramref name="bit"/>, 0 having probability <paramref name="size0"/> / 2^14.</summary>
  void EncodeBit(uint size0, uint bit);
}

public sealed partial class Ppmd7 {
  /// <summary>Encodes one byte, or the end marker (-1), mirroring <see cref="DecodeSymbol"/>.</summary>
  /// <remarks>Converted from Ppmd7_EncodeSymbol in Igor Pavlov's public-domain Ppmd7Enc.c (7-Zip 9.20).</remarks>
  public void EncodeSymbol(IPpmd7RangeEncoder rc, int symbol) {
    var mask = this._charMask;
    if (this.NumStats(this._minContext) != 1) {
      var s = this.Stats(this._minContext);
      if (this.Symbol(s) == symbol) {
        rc.Encode(0, this.Freq(s), this.SummFreq(this._minContext));
        this._foundState = s;
        this.Update1First();
        return;
      }

      this._prevSuccess = 0;
      uint sum = this.Freq(s);
      var i = this.NumStats(this._minContext) - 1;
      do {
        s += 6;
        if (this.Symbol(s) == symbol) {
          rc.Encode(sum, this.Freq(s), this.SummFreq(this._minContext));
          this._foundState = s;
          this.Update1();
          return;
        }

        sum += this.Freq(s);
      } while (--i != 0);

      this._hiBitsFlag = this._hb2Flag[this.Symbol(this._foundState)];
      Array.Fill(mask, (sbyte)-1);
      mask[this.Symbol(s)] = 0;
      i = this.NumStats(this._minContext) - 1;
      do {
        s -= 6;
        mask[this.Symbol(s)] = 0;
      } while (--i != 0);

      rc.Encode(sum, this.SummFreq(this._minContext) - sum, this.SummFreq(this._minContext));
    } else {
      var index = this.BinSummIndex();
      var prob = this._binSumm[index];
      var one = OneState(this._minContext);
      if (this.Symbol(one) == symbol) {
        rc.EncodeBit(prob, 0);
        this._binSumm[index] = (ushort)(prob + (1 << IntBits) - ((prob + (1 << (PeriodBits - 2))) >> PeriodBits));
        this._foundState = one;
        this.UpdateBin();
        return;
      }

      rc.EncodeBit(prob, 1);
      prob = this._binSumm[index] = (ushort)(prob - ((prob + (1 << (PeriodBits - 2))) >> PeriodBits));
      this.InitEsc = ExpEscape[prob >> 10];
      Array.Fill(mask, (sbyte)-1);
      mask[this.Symbol(one)] = 0;
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
      uint sum = 0;
      var i = this.NumStats(this._minContext);
      do {
        int current = this.Symbol(s);
        if (current == symbol) {
          var low = sum;
          var found = s;
          do {
            sum += (uint)(this.Freq(s) & mask[this.Symbol(s)]);
            s += 6;
          } while (--i != 0);

          rc.Encode(low, this.Freq(found), sum + escFreq);
          see.Update();
          this._foundState = found;
          this.Update2();
          return;
        }

        sum += (uint)(this.Freq(s) & mask[current]);
        mask[current] = 0;
        s += 6;
      } while (--i != 0);

      rc.Encode(sum, escFreq, sum + escFreq);
      see.Summ = (ushort)(see.Summ + sum + escFreq);
    }
  }
}
