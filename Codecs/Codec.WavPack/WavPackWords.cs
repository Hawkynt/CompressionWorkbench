#pragma warning disable CS1591

namespace Codec.WavPack;

/// <summary>
/// WavPack's adaptive entropy word coder, a faithful port of the reference
/// <c>read_words.c</c> (<c>get_words_lossless</c>) and <c>write_words.c</c>
/// (<c>send_words_lossless</c> / <c>flush_word</c>). Each channel keeps three
/// running "medians" (<see cref="Entropy"/>) that partition every sample's
/// magnitude into a low / mid / high zone; the zone is sent as a unary "ones"
/// count, the position within the zone as a truncated-binary <c>read_code</c>
/// tail, and the sign as one bit. The medians adapt after every sample (the
/// <see cref="GetMed"/>/<see cref="IncMed"/>/<see cref="DecMed"/> macros, exactly
/// the reference <c>GET_MED</c>/<c>INC_MEDn</c>/<c>DEC_MEDn</c> with
/// <c>DIV0=128</c>, <c>DIV1=64</c>, <c>DIV2=32</c>) so the code tracks the signal
/// envelope.
/// <para>
/// The reference shares a terminating bit between adjacent unary runs via the
/// <c>holding_one</c>/<c>holding_zero</c> state machine and run-length-codes
/// all-zero spans (the <c>zeros_acc</c> Elias-gamma-style coder, triggered when
/// both channels' <c>median[0]</c> are below 2). Both are implemented here, so
/// the bitstream this encoder writes is byte-structurally identical to a
/// reference encoder's and a stream a reference encoder writes decodes here.
/// Only the pure-lossless path is implemented (<c>error_limit == 0</c>); hybrid
/// blocks are rejected upstream, but the <c>update_error_limit</c> structure and
/// the <c>LIMIT_ONES</c> long-run escape are kept faithful.
/// </para>
/// </summary>
internal sealed class WavPackWords {

  // these control the time constant of the 3 median level breakpoints (wavpack_local.h)
  private const uint Div0 = 128; // 5/7 of samples
  private const uint Div1 = 64;  // 10/49 of samples
  private const uint Div2 = 32;  // 20/343 of samples

  // maximum consecutive 1s sent for "div" data before the run-length escape kicks in
  private const int LimitOnes = 16;

  /// <summary>Per-channel adaptive state: the three magnitude medians. The hybrid
  /// <c>slow_level</c>/<c>error_limit</c> fields are omitted because hybrid blocks
  /// are rejected upstream (the pure-lossless path uses <c>error_limit == 0</c>).</summary>
  public sealed class Entropy {
    public readonly uint[] Median = new uint[3];
  }

  private readonly Entropy[] _channels;
  private readonly bool _mono;

  // Cross-word state machine (shared across channels), mirroring "struct words_data".
  private uint _holdingOne;
  private int _holdingZero;
  private uint _zerosAcc;

  public WavPackWords(int channels) {
    this._channels = new Entropy[channels];
    for (var c = 0; c < channels; ++c)
      this._channels[c] = new Entropy();
    this._mono = channels < 2;
  }

  public Entropy Channel(int index) => this._channels[index];

  // ── median macros (wavpack_local.h) ─────────────────────────────────────────
  //   GET_MED(med) = (median[med] >> 4) + 1
  //   INC_MEDn()   = median[n] += ((median[n] + DIVn)       / DIVn) * 5
  //   DEC_MEDn()   = median[n] -= ((median[n] + (DIVn - 2)) / DIVn) * 2

  private static uint GetMed(uint[] med, int i) => (med[i] >> 4) + 1;

  private static void IncMed(uint[] med, int i, uint div) => med[i] += (med[i] + div) / div * 5;
  private static void DecMed(uint[] med, int i, uint div) => med[i] -= (med[i] + (div - 2)) / div * 2;

  // ── low-level read_code / send (truncated binary) ───────────────────────────

  /// <summary>Reads a single unsigned value from 0..<paramref name="maxcode"/>,
  /// the reference <c>read_code</c>. A power-of-two span reads a fixed number of
  /// bits; otherwise it reads the minimum and conditionally one more.</summary>
  private static uint ReadCode(WavPackBitReader r, uint maxcode) {
    if (maxcode < 2)
      return maxcode != 0 ? (uint)r.GetBit() : 0;

    var bitcount = CountBits(maxcode);
    var extras = (1u << bitcount) - maxcode - 1;
    var code = r.GetBits(bitcount - 1);
    if (code >= extras)
      code = (code << 1) - extras + (uint)r.GetBit();
    return code;
  }

  /// <summary>The exact inverse of <see cref="ReadCode"/>: appends the
  /// truncated-binary encoding of <paramref name="code"/> (in 0..<paramref name="maxcode"/>)
  /// to the writer's pending tail accumulator, matching the reference
  /// <c>send_words_lossless</c> (which builds the tail into <c>pend_data</c>).</summary>
  private static void SendCode(WavPackBitWriter w, uint maxcode, uint code) {
    if (maxcode == 0)
      return;

    var bitcount = CountBits(maxcode);
    var extras = (1u << bitcount) - maxcode - 1;
    if (code < extras) {
      w.PutPendBits(code, bitcount - 1);
    } else {
      w.PutPendBits((code + extras) >> 1, bitcount - 1);
      w.PutPendBit((int)((code + extras) & 1));
    }
  }

  // count_bits(av) = number of significant bits = position of the highest set bit + 1.
  private static int CountBits(uint av) {
    var n = 0;
    while (av != 0) {
      ++n;
      av >>= 1;
    }
    return n;
  }

  // ── decode an entire mono/stereo buffer (get_words_lossless) ─────────────────

  /// <summary>Decodes <paramref name="nsamples"/> sample frames into
  /// <paramref name="buffer"/> (one int array per channel), a faithful port of
  /// <c>get_words_lossless</c>. Returns the number of frames produced; a short
  /// count means the bitstream ran out (all-ones EOF).</summary>
  public int GetWordsLossless(WavPackBitReader r, int[][] buffer, int nsamples) {
    var med0 = this._channels[0].Median;
    var med1 = this._mono ? med0 : this._channels[1].Median;

    var total = this._mono ? nsamples : nsamples * 2;
    var csamples = 0;

    while (csamples < total) {
      var chan = this._mono ? 0 : csamples & 1;
      var med = this._channels[chan].Median;

      if (this._holdingZero != 0) {
        this._holdingZero = 0;
        var low0 = ReadCode(r, GetMed(med, 0) - 1);
        DecMed(med, 0, Div0);
        Store(buffer, csamples, r.GetBit() != 0 ? ~(int)low0 : (int)low0);

        if (++csamples == total)
          break;
        chan = this._mono ? 0 : csamples & 1;
        med = this._channels[chan].Median;
      }

      if (med0[0] < 2 && this._holdingOne == 0 && med1[0] < 2) {
        if (this._zerosAcc != 0) {
          if (--this._zerosAcc != 0) {
            Store(buffer, csamples, 0);
            ++csamples;
            continue;
          }
        } else {
          int cbits;
          for (cbits = 0; cbits < 33 && r.GetBit() != 0; ++cbits) { }

          if (cbits == 33)
            break; // WORD_EOF

          if (cbits < 2) {
            this._zerosAcc = (uint)cbits;
          } else {
            uint mask = 1;
            this._zerosAcc = 0;
            while (--cbits != 0) {
              if (r.GetBit() != 0)
                this._zerosAcc |= mask;
              mask <<= 1;
            }
            this._zerosAcc |= mask;
          }

          if (this._zerosAcc != 0) {
            Array.Clear(med0);
            Array.Clear(med1);
            Store(buffer, csamples, 0);
            ++csamples;
            continue;
          }
        }
      }

      // Read the unary "ones" run, honouring the LIMIT_ONES long-run escape.
      var onesCount = ReadOnesRun(r);
      if (onesCount < 0)
        break; // WORD_EOF

      var low = (uint)this._holdingOne;
      this._holdingOne = (uint)onesCount & 1;
      this._holdingZero = ~onesCount & 1;
      var ones = ((uint)onesCount >> 1) + low;

      uint baseLow;
      uint high;
      if (ones == 0) {
        baseLow = 0;
        high = GetMed(med, 0) - 1;
        DecMed(med, 0, Div0);
      } else {
        baseLow = GetMed(med, 0);
        IncMed(med, 0, Div0);
        if (ones == 1) {
          high = baseLow + GetMed(med, 1) - 1;
          DecMed(med, 1, Div1);
        } else {
          baseLow += GetMed(med, 1);
          IncMed(med, 1, Div1);
          if (ones == 2) {
            high = baseLow + GetMed(med, 2) - 1;
            DecMed(med, 2, Div2);
          } else {
            baseLow += (ones - 2) * GetMed(med, 2);
            high = baseLow + GetMed(med, 2) - 1;
            IncMed(med, 2, Div2);
          }
        }
      }

      var value = baseLow + ReadCode(r, high - baseLow);
      Store(buffer, csamples, r.GetBit() != 0 ? ~(int)value : (int)value);
      ++csamples;
    }

    return this._mono ? csamples : csamples / 2;
  }

  // The non-optimized reference unary reader: count ones up to LIMIT_ONES+1, then
  // (at LIMIT_ONES) read a count-of-bits-then-bits escape (Elias-gamma-style).
  private static int ReadOnesRun(WavPackBitReader r) {
    int onesCount;
    for (onesCount = 0; onesCount < LimitOnes + 1 && r.GetBit() != 0; ++onesCount) { }

    if (onesCount < LimitOnes)
      return onesCount;

    if (onesCount == LimitOnes + 1)
      return -1; // WORD_EOF

    // onesCount == LIMIT_ONES: read the extended run length.
    int cbits;
    for (cbits = 0; cbits < 33 && r.GetBit() != 0; ++cbits) { }

    if (cbits == 33)
      return -1; // WORD_EOF

    uint extended;
    if (cbits < 2) {
      extended = (uint)cbits;
    } else {
      uint mask = 1;
      extended = 0;
      while (--cbits != 0) {
        if (r.GetBit() != 0)
          extended |= mask;
        mask <<= 1;
      }
      extended |= mask;
    }

    return (int)(extended + LimitOnes);
  }

  private static void Store(int[][] buffer, int csamples, int value) {
    // buffer is laid out per-channel; csamples interleaves channel 0/1.
    if (buffer.Length == 1) {
      buffer[0][csamples] = value;
    } else {
      buffer[csamples & 1][csamples >> 1] = value;
    }
  }

  // ── encode an entire mono/stereo buffer (send_words_lossless + flush_word) ───

  /// <summary>Encodes <paramref name="nsamples"/> frames from <paramref name="buffer"/>
  /// (one int array per channel) to the writer, a faithful port of
  /// <c>send_words_lossless</c>. <see cref="FlushFinal"/> must be called once after
  /// the last sample to emit any held-back terminating bits.</summary>
  public void SendWordsLossless(WavPackBitWriter w, int[][] buffer, int nsamples) {
    var med0 = this._channels[0].Median;
    var med1 = this._mono ? med0 : this._channels[1].Median;

    var total = this._mono ? nsamples : nsamples * 2;

    for (var csamples = 0; csamples < total; ++csamples) {
      var value = Load(buffer, csamples);
      var sign = value < 0 ? 1 : 0;
      var med = this._channels[this._mono ? 0 : csamples & 1].Median;

      if (med0[0] < 2 && this._holdingZero == 0 && med1[0] < 2) {
        if (this._zerosAcc != 0) {
          if (value != 0) {
            this.FlushWord(w);
          } else {
            ++this._zerosAcc;
            continue;
          }
        } else if (value != 0) {
          w.PutBit(0); // putbit_0
        } else {
          Array.Clear(med0);
          Array.Clear(med1);
          this._zerosAcc = 1;
          continue;
        }
      }

      var v = sign != 0 ? ~value : value;

      uint onesCount;
      uint low;
      uint high;
      if (v < (int)GetMed(med, 0)) {
        onesCount = low = 0;
        high = GetMed(med, 0) - 1;
        DecMed(med, 0, Div0);
      } else {
        low = GetMed(med, 0);
        IncMed(med, 0, Div0);
        if ((uint)v - low < GetMed(med, 1)) {
          onesCount = 1;
          high = low + GetMed(med, 1) - 1;
          DecMed(med, 1, Div1);
        } else {
          low += GetMed(med, 1);
          IncMed(med, 1, Div1);
          if ((uint)v - low < GetMed(med, 2)) {
            onesCount = 2;
            high = low + GetMed(med, 2) - 1;
            DecMed(med, 2, Div2);
          } else {
            onesCount = 2 + ((uint)v - low) / GetMed(med, 2);
            low += (onesCount - 2) * GetMed(med, 2);
            high = low + GetMed(med, 2) - 1;
            IncMed(med, 2, Div2);
          }
        }
      }

      if (this._holdingZero != 0) {
        if (onesCount != 0)
          ++this._holdingOne;

        this.FlushWord(w);

        if (onesCount != 0) {
          this._holdingZero = 1;
          --onesCount;
        } else {
          this._holdingZero = 0;
        }
      } else {
        this._holdingZero = 1;
      }

      this._holdingOne = onesCount * 2;

      if (high != low)
        SendCode(w, high - low, (uint)v - low);

      w.PutPendBit(sign);

      if (this._holdingZero == 0)
        this.FlushWord(w);
    }
  }

  private static int Load(int[][] buffer, int csamples) =>
    buffer.Length == 1 ? buffer[0][csamples] : buffer[csamples & 1][csamples >> 1];

  /// <summary>Port of the reference <c>flush_word</c>: spills the accumulated
  /// zero-run, the held unary "ones" run (with the LIMIT_ONES escape), the held
  /// terminating zero, and the pending tail/sign bits onto the bitstream.</summary>
  private void FlushWord(WavPackBitWriter w) {
    if (this._zerosAcc != 0) {
      var cbits = CountBits(this._zerosAcc);
      while (cbits-- != 0)
        w.PutBit(1);
      w.PutBit(0);
      while (this._zerosAcc > 1) {
        w.PutBit((int)(this._zerosAcc & 1));
        this._zerosAcc >>= 1;
      }
      this._zerosAcc = 0;
    }

    if (this._holdingOne != 0) {
      if (this._holdingOne >= LimitOnes) {
        w.PutBits((1u << LimitOnes) - 1, LimitOnes + 1);
        this._holdingOne -= LimitOnes;
        var cbits = CountBits(this._holdingOne);
        while (cbits-- != 0)
          w.PutBit(1);
        w.PutBit(0);
        while (this._holdingOne > 1) {
          w.PutBit((int)(this._holdingOne & 1));
          this._holdingOne >>= 1;
        }
        this._holdingZero = 0;
      } else {
        // putbits(bitmask[holding_one], holding_one): holding_one one-bits.
        w.PutBits((1u << (int)this._holdingOne) - 1, (int)this._holdingOne);
      }
      this._holdingOne = 0;
    }

    if (this._holdingZero != 0) {
      w.PutBit(0);
      this._holdingZero = 0;
    }

    w.FlushPending(w);
  }

  /// <summary>Flushes any final held state after the last sample, exactly as the
  /// reference clients call <c>flush_word</c> once all words have been sent.</summary>
  public void FlushFinal(WavPackBitWriter w) => this.FlushWord(w);
}
