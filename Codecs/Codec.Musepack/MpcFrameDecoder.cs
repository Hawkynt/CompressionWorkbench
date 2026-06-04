#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Decodes Musepack SV8 audio sub-frames: subband resolutions, scale factors and
/// quantised coefficients (<c>mpc8_decode_frame</c>), then dequantises and runs
/// the 32-band polyphase synthesis (<c>ff_mpc_dequantize_and_synth</c>). A faithful
/// port of FFmpeg's <c>libavcodec/mpc8.c</c> + <c>mpc.c</c>. State (the per-band
/// "old DSCF" flags, the previous max-band and the per-channel synthesis ring
/// buffers) persists across the sub-frames of a stream.
/// </summary>
internal sealed class MpcFrameDecoder {

  private const int Bands = MpcTables.Bands;                   // 32
  private const int SamplesPerBand = MpcTables.SamplesPerBand; // 36
  private const int FrameSize = MpcTables.FrameSize;           // 1152

  private sealed class Band {
    public bool Msf;
    public readonly int[] Res = new int[2];
    public readonly int[] Scfi = new int[2];
    public readonly int[][] ScfIdx = { new int[3], new int[3] };
  }

  private readonly int _channels;
  private readonly int _maxBands;
  private readonly bool _mss;
  private readonly MpcVlcBooks _books = MpcVlcBooks.Shared;

  private readonly Band[] _bands = new Band[Bands];
  private readonly int[][] _oldDscf = { new int[Bands], new int[Bands] };
  private readonly int[][] _q = { new int[FrameSize], new int[FrameSize] };
  private int _lastMaxBand;

  private readonly MpcSynthesis[] _synth;
  private readonly MpcRandom _rnd = new(0xDEADBEEF);

  public int FramesPerPacket { get; }

  public MpcFrameDecoder(int channels, int maxBands, bool midSideUsed, int framesPerPacket = 1) {
    this._channels = channels;
    this._maxBands = maxBands;
    this._mss = midSideUsed;
    this.FramesPerPacket = framesPerPacket;
    for (var i = 0; i < Bands; ++i)
      this._bands[i] = new Band();
    this._synth = new MpcSynthesis[channels];
    for (var ch = 0; ch < channels; ++ch)
      this._synth[ch] = new MpcSynthesis();
  }

  /// <summary>Decodes one sub-frame; returns per-channel mono PCM of <see cref="FrameSize"/> samples each.</summary>
  public short[][] DecodeFrame(MpcBitReader gb, bool keyframe) {
    if (keyframe)
      Array.Clear(this._q[0]); // matches memset(c->Q, 0) — both channels cleared below too
    if (keyframe)
      Array.Clear(this._q[1]);

    int maxband;
    if (keyframe) {
      maxband = GetModGolomb(gb, this._maxBands + 1);
    } else {
      maxband = this._lastMaxBand + this._books.Band.Read(gb);
      if (maxband > 32)
        maxband -= 33;
    }
    if (maxband > this._maxBands + 1)
      throw new InvalidDataException($"Musepack: maxband {maxband} exceeds stream maximum.");
    this._lastMaxBand = maxband;

    // --- subband resolutions ---
    if (maxband > 0) {
      var last0 = 0;
      var last1 = 0;
      for (var i = maxband - 1; i >= 0; --i) {
        last0 = this._books.Res[last0 > 2 ? 1 : 0].Read(gb) + last0;
        if (last0 > 15) last0 -= 17;
        this._bands[i].Res[0] = last0;
        last1 = this._books.Res[last1 > 2 ? 1 : 0].Read(gb) + last1;
        if (last1 > 15) last1 -= 17;
        this._bands[i].Res[1] = last1;
      }

      if (this._mss) {
        var cnt = 0;
        for (var i = 0; i < maxband; ++i)
          if (this._bands[i].Res[0] != 0 || this._bands[i].Res[1] != 0)
            ++cnt;
        var t = GetModGolomb(gb, cnt);
        var mask = GetMask(gb, cnt, t);
        for (var i = maxband - 1; i >= 0; --i)
          if (this._bands[i].Res[0] != 0 || this._bands[i].Res[1] != 0) {
            this._bands[i].Msf = (mask & 1) != 0;
            mask >>= 1;
          }
      }
    }
    for (var i = maxband; i < this._maxBands; ++i)
      this._bands[i].Res[0] = this._bands[i].Res[1] = 0;

    if (keyframe)
      for (var i = 0; i < 32; ++i)
        this._oldDscf[0][i] = this._oldDscf[1][i] = 1;

    // --- scale-factor selection info (SCFI) ---
    for (var i = 0; i < maxband; ++i) {
      var band = this._bands[i];
      if (band.Res[0] == 0 && band.Res[1] == 0)
        continue;
      var cnt = (band.Res[0] != 0 ? 1 : 0) + (band.Res[1] != 0 ? 1 : 0) - 1;
      if (cnt < 0)
        continue;
      var t = this._books.Scfi[cnt].Read(gb);
      if (band.Res[0] != 0) band.Scfi[0] = t >> (2 * cnt);
      if (band.Res[1] != 0) band.Scfi[1] = t & 3;
    }

    // --- scale factors (DSCF deltas) ---
    for (var i = 0; i < maxband; ++i) {
      var band = this._bands[i];
      for (var ch = 0; ch < 2; ++ch) {
        if (band.Res[ch] == 0)
          continue;

        if (this._oldDscf[ch][i] != 0) {
          band.ScfIdx[ch][0] = gb.GetBits(7) - 6;
          this._oldDscf[ch][i] = 0;
        } else {
          var t = this._books.Dscf[1].Read(gb);
          if (t == 64)
            t += gb.GetBits(6);
          band.ScfIdx[ch][0] = ((band.ScfIdx[ch][2] + t - 25) & 0x7F) - 6;
        }
        for (var j = 0; j < 2; ++j) {
          if (((band.Scfi[ch] << j) & 2) != 0) {
            band.ScfIdx[ch][j + 1] = band.ScfIdx[ch][j];
          } else {
            var t = this._books.Dscf[0].Read(gb);
            if (t == 31)
              t = 64 + gb.GetBits(6);
            band.ScfIdx[ch][j + 1] = ((band.ScfIdx[ch][j] + t - 25) & 0x7F) - 6;
          }
        }
      }
    }

    // --- quantised coefficients ---
    for (int i = 0, off = 0; i < maxband; ++i, off += SamplesPerBand) {
      var band = this._bands[i];
      for (var ch = 0; ch < 2; ++ch)
        DecodeBandCoefficients(gb, band.Res[ch], this._q[ch], off);
    }

    return DequantizeAndSynth(maxband - 1);
  }

  private void DecodeBandCoefficients(MpcBitReader gb, int res, int[] q, int off) {
    switch (res) {
      case -1:
        for (var j = 0; j < SamplesPerBand; ++j)
          q[off + j] = (int)(this._rnd.Next() & 0x3FC) - 510;
        break;
      case 0:
        break;
      case 1:
        for (var j = 0; j < SamplesPerBand; j += SamplesPerBand / 2) {
          var cnt = this._books.Q1.Read(gb);
          var t = GetMask(gb, 18, cnt);
          for (var k = 0; k < SamplesPerBand / 2; ++k)
            q[off + j + k] = (t & (1 << (SamplesPerBand / 2 - k - 1))) != 0 ? (gb.GetBit() << 1) - 1 : 0;
        }
        break;
      case 2: {
        var cnt = 6; // 2*mpc8_thres[2]
        for (var j = 0; j < SamplesPerBand; j += 3) {
          var t = this._books.Q2[cnt > 3 ? 1 : 0].Read(gb);
          q[off + j + 0] = MpcTables.Idx50[t];
          q[off + j + 1] = MpcTables.Idx51[t];
          q[off + j + 2] = MpcTables.Idx52[t];
          cnt = (cnt >> 1) + MpcTables.HuffQ2[t];
        }
        break;
      }
      case 3:
      case 4:
        for (var j = 0; j < SamplesPerBand; j += 2) {
          var t = this._books.Q3[res - 3].Read(gb);
          q[off + j + 1] = t >> 4;
          q[off + j + 0] = SignExtend(t, 4);
        }
        break;
      case 5:
      case 6:
      case 7:
      case 8: {
        var cnt = 2 * MpcTables.Thres[res];
        for (var j = 0; j < SamplesPerBand; ++j) {
          var vlc = this._books.Quant[res - 5][cnt > MpcTables.Thres[res] ? 1 : 0];
          var v = vlc.Read(gb);
          q[off + j] = v;
          cnt = (cnt >> 1) + Math.Abs(v);
        }
        break;
      }
      default: // res >= 9
        for (var j = 0; j < SamplesPerBand; ++j) {
          var v = this._books.Q9Up.Read(gb);
          if (res != 9) {
            v <<= res - 9;
            v |= gb.GetBits(res - 9);
          }
          v -= (1 << (res - 2)) - 1;
          q[off + j] = v;
        }
        break;
    }
  }

  // ff_mpc_dequantize_and_synth: scale Q by CC[res]*SCF[scf_idx], optional M/S
  // undo, then per-band-sample run the polyphase synthesis filter.
  private short[][] DequantizeAndSynth(int maxband) {
    // sbSamples[ch][sampleIndex 0..35][band 0..31]
    var sbSamples = new float[2][][];
    for (var ch = 0; ch < 2; ++ch) {
      sbSamples[ch] = new float[SamplesPerBand][];
      for (var s = 0; s < SamplesPerBand; ++s)
        sbSamples[ch][s] = new float[Bands];
    }

    for (int i = 0, off = 0; i <= maxband; ++i, off += SamplesPerBand) {
      var band = this._bands[i];
      for (var ch = 0; ch < 2; ++ch) {
        if (band.Res[ch] == 0)
          continue;
        var cc = MpcTables.CcByRes(band.Res[ch]);
        var j = 0;
        for (var seg = 0; seg < 3; ++seg) {
          var mul = cc * MpcTables.ScfTable[band.ScfIdx[ch][seg] & 0xFF];
          var limit = (seg + 1) * 12;
          for (; j < limit; ++j)
            sbSamples[ch][j][i] = ClipInt32(mul * this._q[ch][j + off]);
        }
      }

      if (band.Msf)
        for (var j = 0; j < SamplesPerBand; ++j) {
          var t1 = sbSamples[0][j][i];
          var t2 = sbSamples[1][j][i];
          sbSamples[0][j][i] = t1 + t2;
          sbSamples[1][j][i] = t1 - t2;
        }
    }

    var pcm = new short[this._channels][];
    for (var ch = 0; ch < this._channels; ++ch) {
      pcm[ch] = new short[FrameSize];
      for (var s = 0; s < SamplesPerBand; ++s)
        this._synth[ch].Filter(sbSamples[ch][s], pcm[ch], 32 * s, 1);
    }
    return pcm;
  }

  // --- enumerative / Golomb helpers (mpc8.c) --------------------------------

  private static int DecBase(MpcBitReader gb, int k, int n) {
    var len = MpcCnkTables.CnkLen[k - 1][n - 1] - 1;
    var code = len > 0 ? gb.GetBits(len) : 0;
    if (code >= MpcCnkTables.CnkLost[k - 1][n - 1])
      code = ((code << 1) | gb.GetBit()) - (int)MpcCnkTables.CnkLost[k - 1][n - 1];
    return code;
  }

  private static int DecEnum(MpcBitReader gb, int k, int n) {
    var bits = 0;
    var cIdx = k - 1;
    var code = DecBase(gb, k, n);
    do {
      n--;
      if (code >= MpcCnkTables.Cnk[cIdx][n]) {
        bits |= 1 << n;
        code -= (int)MpcCnkTables.Cnk[cIdx][n];
        --cIdx;
        --k;
      }
    } while (k > 0);
    return bits;
  }

  private static int GetModGolomb(MpcBitReader gb, int m) {
    if (MpcCnkTables.CnkLen[0][m] < 1)
      return 0;
    return DecBase(gb, 1, m + 1);
  }

  private static int GetMask(MpcBitReader gb, int size, int t) {
    var mask = 0;
    if (t != 0 && t != size)
      mask = DecEnum(gb, Math.Min(t, size - t), size);
    if ((t << 1) > size)
      mask = ~mask;
    return mask;
  }

  private static int SignExtend(int value, int bits) {
    var shift = 32 - bits;
    return (value << shift) >> shift;
  }

  private static float ClipInt32(float v) {
    if (v > int.MaxValue) return int.MaxValue;
    if (v < int.MinValue) return int.MinValue;
    return v;
  }
}
