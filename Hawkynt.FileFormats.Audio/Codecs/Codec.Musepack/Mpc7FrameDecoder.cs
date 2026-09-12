#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Decodes Musepack SV7 (<c>MP+</c>) audio frames: subband resolutions, scale-factor
/// selection and indices, and quantised coefficients (<c>mpc7_decode_frame</c> /
/// <c>idx_to_quant</c>), then dequantises and runs the shared 32-band polyphase
/// synthesis (<c>ff_mpc_dequantize_and_synth</c>). A faithful port of FFmpeg's
/// <c>libavcodec/mpc7.c</c> + <c>mpc.c</c> (LGPL 2.1, © Konstantin Shishkov). SV7 is
/// always stereo. The per-band "old DSCF" scale references and the per-channel synthesis
/// ring buffers persist across the frames of a stream.
/// <para>
/// Each SV7 frame's bitstream is byte-swapped per 32-bit word before reading, matching
/// FFmpeg's <c>bswap_buf</c> step; the caller supplies the already-byteswapped buffer.
/// </para>
/// </summary>
internal sealed class Mpc7FrameDecoder {

  private const int Bands = MpcTables.Bands;                   // 32
  private const int SamplesPerBand = MpcTables.SamplesPerBand; // 36
  private const int FrameSize = MpcTables.FrameSize;           // 1152

  private sealed class Band {
    public bool Msf;
    public readonly int[] Res = new int[2];
    public readonly int[] Scfi = new int[2];
    public readonly int[][] ScfIdx = { new int[3], new int[3] };
  }

  private readonly int _maxBands; // SV7 'maxbands': highest possibly-coded band index
  private readonly bool _mss;
  private readonly Mpc7VlcBooks _books = Mpc7VlcBooks.Shared;

  private readonly Band[] _bands = new Band[Bands];
  private readonly int[][] _oldDscf = { new int[Bands], new int[Bands] };
  private readonly int[][] _q = { new int[FrameSize], new int[FrameSize] };

  private readonly MpcSynthesis[] _synth = { new(), new() };
  private readonly MpcRandom _rnd = new(0xDEADBEEF);

  public Mpc7FrameDecoder(int maxBands, bool midSideUsed) {
    this._maxBands = maxBands;
    this._mss = midSideUsed;
    for (var i = 0; i < Bands; ++i)
      this._bands[i] = new Band();
  }

  /// <summary>
  /// Decodes one SV7 frame from <paramref name="gb"/> (positioned past the leading
  /// <c>skip</c> bits) into per-channel mono PCM of <see cref="FrameSize"/> samples each.
  /// </summary>
  public short[][] DecodeFrame(MpcBitReader gb) {
    // memset(bands, 0, sizeof(*bands) * (maxbands + 1))
    for (var i = 0; i <= this._maxBands && i < Bands; ++i) {
      var b = this._bands[i];
      b.Msf = false;
      b.Res[0] = b.Res[1] = 0;
      b.Scfi[0] = b.Scfi[1] = 0;
      for (var ch = 0; ch < 2; ++ch)
        b.ScfIdx[ch][0] = b.ScfIdx[ch][1] = b.ScfIdx[ch][2] = 0;
    }

    var mb = -1;

    // --- subband resolutions (i = 0..maxbands) ---
    for (var i = 0; i <= this._maxBands; ++i) {
      var band = this._bands[i];
      for (var ch = 0; ch < 2; ++ch) {
        var t = i != 0 ? this._books.Hdr.Read(gb) : 4;
        if (t == 4)
          band.Res[ch] = gb.GetBits(4);
        else
          band.Res[ch] = this._bands[i - 1].Res[ch] + t;
        if (band.Res[ch] is < -1 or > 17)
          throw new InvalidDataException("Musepack SV7: subband index invalid.");
      }
      if (band.Res[0] != 0 || band.Res[1] != 0) {
        mb = i;
        if (this._mss)
          band.Msf = gb.GetBit() != 0;
      }
    }

    // --- scale-factor selection info ---
    for (var i = 0; i <= mb; ++i)
      for (var ch = 0; ch < 2; ++ch)
        if (this._bands[i].Res[ch] != 0)
          this._bands[i].Scfi[ch] = this._books.Scfi.Read(gb);

    // --- scale indices ---
    for (var i = 0; i <= mb; ++i) {
      var band = this._bands[i];
      for (var ch = 0; ch < 2; ++ch) {
        if (band.Res[ch] == 0)
          continue;
        band.ScfIdx[ch][2] = this._oldDscf[ch][i];
        band.ScfIdx[ch][0] = GetScaleIdx(gb, band.ScfIdx[ch][2]);
        switch (band.Scfi[ch]) {
          case 0:
            band.ScfIdx[ch][1] = GetScaleIdx(gb, band.ScfIdx[ch][0]);
            band.ScfIdx[ch][2] = GetScaleIdx(gb, band.ScfIdx[ch][1]);
            break;
          case 1:
            band.ScfIdx[ch][1] = GetScaleIdx(gb, band.ScfIdx[ch][0]);
            band.ScfIdx[ch][2] = band.ScfIdx[ch][1];
            break;
          case 2:
            band.ScfIdx[ch][1] = band.ScfIdx[ch][0];
            band.ScfIdx[ch][2] = GetScaleIdx(gb, band.ScfIdx[ch][1]);
            break;
          default: // 3
            band.ScfIdx[ch][2] = band.ScfIdx[ch][1] = band.ScfIdx[ch][0];
            break;
        }
        this._oldDscf[ch][i] = band.ScfIdx[ch][2];
      }
    }

    // --- quantised coefficients (i = 0..BANDS-1) ---
    Array.Clear(this._q[0]);
    Array.Clear(this._q[1]);
    for (int i = 0, off = 0; i < Bands; ++i, off += SamplesPerBand)
      for (var ch = 0; ch < 2; ++ch)
        IdxToQuant(gb, this._bands[i].Res[ch], this._q[ch], off);

    return DequantizeAndSynth(mb);
  }

  private int GetScaleIdx(MpcBitReader gb, int refIdx) {
    var t = this._books.Dscf.Read(gb);
    if (t == 8)
      return gb.GetBits(6);
    return refIdx + t;
  }

  // idx_to_quant: fills SAMPLES_PER_BAND quantised coefficients for one band/channel.
  private void IdxToQuant(MpcBitReader gb, int idx, int[] dst, int off) {
    switch (idx) {
      case -1:
        for (var i = 0; i < SamplesPerBand; ++i)
          dst[off + i] = (int)(this._rnd.Next() & 0x3FC) - 510;
        break;
      case 1: {
        var i1 = gb.GetBit();
        var d = off;
        for (var i = 0; i < SamplesPerBand / 3; ++i) {
          var t = this._books.Quant[0][i1].Read(gb);
          dst[d++] = Mpc7Tables.Idx30[t];
          dst[d++] = Mpc7Tables.Idx31[t];
          dst[d++] = Mpc7Tables.Idx32[t];
        }
        break;
      }
      case 2: {
        var i1 = gb.GetBit();
        var d = off;
        for (var i = 0; i < SamplesPerBand / 2; ++i) {
          var t = this._books.Quant[1][i1].Read(gb);
          dst[d++] = Mpc7Tables.Idx50[t];
          dst[d++] = Mpc7Tables.Idx51[t];
        }
        break;
      }
      case 3:
      case 4:
      case 5:
      case 6:
      case 7: {
        var i1 = gb.GetBit();
        for (var i = 0; i < SamplesPerBand; ++i)
          dst[off + i] = this._books.Quant[idx - 1][i1].Read(gb);
        break;
      }
      case 8:
      case 9:
      case 10:
      case 11:
      case 12:
      case 13:
      case 14:
      case 15:
      case 16:
      case 17: {
        var t = (1 << (idx - 2)) - 1;
        for (var i = 0; i < SamplesPerBand; ++i)
          dst[off + i] = gb.GetBits(idx - 1) - t;
        break;
      }
      default: // 0 and -2..-17 → nothing coded
        break;
    }
  }

  // ff_mpc_dequantize_and_synth (channels == 2 for SV7).
  private short[][] DequantizeAndSynth(int maxband) {
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

    var pcm = new short[2][];
    for (var ch = 0; ch < 2; ++ch) {
      pcm[ch] = new short[FrameSize];
      for (var s = 0; s < SamplesPerBand; ++s)
        this._synth[ch].Filter(sbSamples[ch][s], pcm[ch], 32 * s, 1);
    }
    return pcm;
  }

  private static float ClipInt32(float v) {
    if (v > int.MaxValue) return int.MaxValue;
    if (v < int.MinValue) return int.MinValue;
    return v;
  }
}
