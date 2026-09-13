#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.TrueSpeech;

/// <summary>
/// DSP Group TrueSpeech (8.5 kbit/s) decoder — a faithful port of FFmpeg's
/// <c>libavcodec/truespeech.c</c>. TrueSpeech is a mono 8000 Hz speech codec: every
/// 32-byte frame decodes to 240 signed 16-bit samples (four 60-sample subframes).
/// <para>Each frame is bit-unpacked (after a per-32-bit-word byte swap, as the reference
/// does) into an 8-element input vector (codebook-indexed at 5/5/4/4/4/3/3/3 bits), four
/// 7-bit two-point-filter offsets, four 27-bit pulse-position fields with 4-bit pulse
/// offsets and 14-bit pulse-value fields, plus a 1-bit filter-merge flag. Synthesis runs
/// the correlation filter, merges with the previous frame's filter, applies the two-point
/// adaptive filter, places the excitation pulses and runs the three-stage output filter.</para>
/// <para>Decode-only — there is no encoder. Inter-frame filter state is carried exactly as
/// the reference does, so multi-frame buffers decode identically to single-frame feeds.</para>
/// </summary>
public static class TrueSpeechCodec {

  private const int FrameBytes = 32;
  private const int SamplesPerFrame = 240;

  /// <summary>
  /// Decodes back-to-back 32-byte TrueSpeech frames to mono 16-bit PCM. A ragged tail
  /// shorter than a full frame is ignored. Output length is
  /// <c>(input.Length / 32) * 240</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> frames) {
    var count = frames.Length / FrameBytes;
    if (count == 0)
      return [];

    var ctx = new Context();
    var output = new short[count * SamplesPerFrame];
    var outPos = 0;

    for (var f = 0; f < count; ++f) {
      ReadFrame(ctx, frames.Slice(f * FrameBytes, FrameBytes));
      CorrelateFilter(ctx);
      FiltersMerge(ctx);

      for (var i = 0; i < 4; ++i) {
        ApplyTwoPointFilter(ctx, i);
        PlacePulses(ctx, output, outPos, i);
        UpdateFilters(ctx, output, outPos);
        Synth(ctx, output, outPos, i);
        outPos += 60;
      }

      SavePrevVec(ctx);
    }
    return output;
  }

  /// <summary>Mutable per-stream decoder state (mirrors <c>TSContext</c>).</summary>
  private sealed class Context {
    public readonly short[] Vector = new short[8];      // input vector: 5/5/4/4/4/3/3/3
    public readonly int[] Offset1 = new int[2];
    public readonly int[] Offset2 = new int[4];
    public readonly int[] PulseOff = new int[4];
    public readonly int[] PulsePos = new int[4];
    public readonly int[] PulseVal = new int[4];
    public int Flag;
    public readonly int[] FiltBuf = new int[146];
    public readonly int[] PrevFilt = new int[8];
    public readonly short[] Tmp1 = new short[8];
    public readonly short[] Tmp2 = new short[8];
    public readonly short[] Tmp3 = new short[8];
    public readonly short[] CVector = new short[8];
    public int FiltVal;
    public readonly short[] NewVec = new short[60];
    public readonly short[] Filters = new short[32];
  }

  private static void ReadFrame(Context dec, ReadOnlySpan<byte> input) {
    // The reference byte-swaps each of the eight 32-bit words before MSB-first reading.
    var swapped = new byte[FrameBytes];
    for (var w = 0; w < 8; ++w) {
      var v = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(w * 4, 4));
      BinaryPrimitives.WriteUInt32BigEndian(swapped.AsSpan(w * 4), v);
    }
    var gb = new BitReader(swapped);

    dec.Vector[7] = TrueSpeechTables.Codebook[7][gb.GetBits(3)];
    dec.Vector[6] = TrueSpeechTables.Codebook[6][gb.GetBits(3)];
    dec.Vector[5] = TrueSpeechTables.Codebook[5][gb.GetBits(3)];
    dec.Vector[4] = TrueSpeechTables.Codebook[4][gb.GetBits(4)];
    dec.Vector[3] = TrueSpeechTables.Codebook[3][gb.GetBits(4)];
    dec.Vector[2] = TrueSpeechTables.Codebook[2][gb.GetBits(4)];
    dec.Vector[1] = TrueSpeechTables.Codebook[1][gb.GetBits(5)];
    dec.Vector[0] = TrueSpeechTables.Codebook[0][gb.GetBits(5)];
    dec.Flag = gb.GetBits(1);

    dec.Offset1[0] = gb.GetBits(4) << 4;
    dec.Offset2[3] = gb.GetBits(7);
    dec.Offset2[2] = gb.GetBits(7);
    dec.Offset2[1] = gb.GetBits(7);
    dec.Offset2[0] = gb.GetBits(7);

    dec.Offset1[1] = gb.GetBits(4);
    dec.PulseVal[1] = gb.GetBits(14);
    dec.PulseVal[0] = gb.GetBits(14);

    dec.Offset1[1] |= gb.GetBits(4) << 4;
    dec.PulseVal[3] = gb.GetBits(14);
    dec.PulseVal[2] = gb.GetBits(14);

    dec.Offset1[0] |= gb.GetBits(1);
    dec.PulsePos[0] = gb.GetBits(27);
    dec.PulseOff[0] = gb.GetBits(4);

    dec.Offset1[0] |= gb.GetBits(1) << 1;
    dec.PulsePos[1] = gb.GetBits(27);
    dec.PulseOff[1] = gb.GetBits(4);

    dec.Offset1[0] |= gb.GetBits(1) << 2;
    dec.PulsePos[2] = gb.GetBits(27);
    dec.PulseOff[2] = gb.GetBits(4);

    dec.Offset1[0] |= gb.GetBits(1) << 3;
    dec.PulsePos[3] = gb.GetBits(27);
    dec.PulseOff[3] = gb.GetBits(4);
  }

  private static void CorrelateFilter(Context dec) {
    var tmp = new short[8];
    for (var i = 0; i < 8; ++i) {
      if (i > 0) {
        Array.Copy(dec.CVector, tmp, i);
        for (var j = 0; j < i; ++j)
          dec.CVector[j] += (short)((tmp[i - j - 1] * dec.Vector[i] + 0x4000) >> 15);
      }
      dec.CVector[i] = (short)((8 - dec.Vector[i]) >> 3);
    }
    for (var i = 0; i < 8; ++i)
      dec.CVector[i] = (short)((dec.CVector[i] * TrueSpeechTables.Decay994_1000[i]) >> 15);

    dec.FiltVal = dec.Vector[0];
  }

  private static void FiltersMerge(Context dec) {
    if (dec.Flag == 0) {
      for (var i = 0; i < 8; ++i) {
        dec.Filters[i + 0] = (short)dec.PrevFilt[i];
        dec.Filters[i + 8] = (short)dec.PrevFilt[i];
      }
    } else {
      for (var i = 0; i < 8; ++i) {
        dec.Filters[i + 0] = (short)((dec.CVector[i] * 21846 + dec.PrevFilt[i] * 10923 + 16384) >> 15);
        dec.Filters[i + 8] = (short)((dec.CVector[i] * 10923 + dec.PrevFilt[i] * 21846 + 16384) >> 15);
      }
    }
    for (var i = 0; i < 8; ++i) {
      dec.Filters[i + 16] = dec.CVector[i];
      dec.Filters[i + 24] = dec.CVector[i];
    }
  }

  private static void ApplyTwoPointFilter(Context dec, int quart) {
    var t = dec.Offset2[quart];
    if (t == 127) {
      Array.Clear(dec.NewVec, 0, 60);
      return;
    }

    // tmp holds the 146-sample filter history followed by the 60 freshly-produced samples.
    var tmp = new short[146 + 60];
    for (var i = 0; i < 146; ++i)
      tmp[i] = (short)dec.FiltBuf[i];

    var off = (t / 25) + dec.Offset1[quart >> 1] + 18;
    off = Math.Clamp(off, 0, 145);
    var ptr0 = 145 - off;            // index into tmp
    const int ptr1 = 146;            // index into tmp (output tail)
    var filter = (t % 25) * 2;       // index into Order2Coeffs
    for (var i = 0; i < 60; ++i) {
      var v = (tmp[ptr0 + 0] * TrueSpeechTables.Order2Coeffs[filter + 0]
             + tmp[ptr0 + 1] * TrueSpeechTables.Order2Coeffs[filter + 1] + 0x2000) >> 14;
      ++ptr0;
      dec.NewVec[i] = (short)v;
      tmp[ptr1 + i] = (short)v;
    }
  }

  private static void PlacePulses(Context dec, short[] outBuf, int outOff, int quart) {
    var tmp = new short[7];
    Array.Clear(outBuf, outOff, 60);

    for (var i = 0; i < 7; ++i) {
      var t = dec.PulseVal[quart] & 3;
      dec.PulseVal[quart] >>= 2;
      tmp[6 - i] = TrueSpeechTables.PulseScales[dec.PulseOff[quart] * 4 + t];
    }

    var coef = dec.PulsePos[quart] >> 15;
    var ptr1 = 30;   // index into PulseValues
    var ptr2 = 0;    // index into tmp
    for (var i = 0; i < 30; ++i) {
      if (ptr2 >= 3) break;
      var t = TrueSpeechTables.PulseValues[ptr1++];
      if (coef >= t) {
        coef -= t;
      } else {
        outBuf[outOff + i] = tmp[ptr2++];
        ptr1 += 30;
      }
    }

    coef = dec.PulsePos[quart] & 0x7FFF;
    ptr1 = 0;
    for (var i = 30; i < 60; ++i) {
      if (ptr2 >= 7) break;
      var t = TrueSpeechTables.PulseValues[ptr1++];
      if (coef >= t) {
        coef -= t;
      } else {
        outBuf[outOff + i] = tmp[ptr2++];
        ptr1 += 30;
      }
    }
  }

  private static void UpdateFilters(Context dec, short[] outBuf, int outOff) {
    Array.Copy(dec.FiltBuf, 60, dec.FiltBuf, 0, 86);
    for (var i = 0; i < 60; ++i) {
      dec.FiltBuf[i + 86] = outBuf[outOff + i] + dec.NewVec[i] - (dec.NewVec[i] >> 3);
      outBuf[outOff + i] = (short)(outBuf[outOff + i] + dec.NewVec[i]);
    }
  }

  private static void Synth(Context dec, short[] outBuf, int outOff, int quart) {
    var t = new int[8];
    var ptr1 = quart * 8;   // index into dec.Filters

    var ptr0 = dec.Tmp1;
    for (var i = 0; i < 60; ++i) {
      var sum = 0;
      for (var k = 0; k < 8; ++k)
        sum += ptr0[k] * dec.Filters[ptr1 + k];
      sum = outBuf[outOff + i] + ((sum + 0x800) >> 12);
      outBuf[outOff + i] = (short)Math.Clamp(sum, -0x7FFE, 0x7FFE);
      for (var k = 7; k > 0; --k) ptr0[k] = ptr0[k - 1];
      ptr0[0] = outBuf[outOff + i];
    }

    for (var i = 0; i < 8; ++i)
      t[i] = (TrueSpeechTables.Decay35_64[i] * dec.Filters[ptr1 + i]) >> 15;

    ptr0 = dec.Tmp2;
    for (var i = 0; i < 60; ++i) {
      var sum = 0;
      for (var k = 0; k < 8; ++k)
        sum += ptr0[k] * t[k];
      for (var k = 7; k > 0; --k) ptr0[k] = ptr0[k - 1];
      ptr0[0] = outBuf[outOff + i];
      outBuf[outOff + i] = (short)(outBuf[outOff + i] + ((-sum) >> 12));
    }

    for (var i = 0; i < 8; ++i)
      t[i] = (TrueSpeechTables.Decay3_4[i] * dec.Filters[ptr1 + i]) >> 15;

    ptr0 = dec.Tmp3;
    for (var i = 0; i < 60; ++i) {
      var sum = outBuf[outOff + i] * (1 << 12);
      for (var k = 0; k < 8; ++k)
        sum += ptr0[k] * t[k];
      for (var k = 7; k > 0; --k) ptr0[k] = ptr0[k - 1];
      ptr0[0] = (short)Math.Clamp((sum + 0x800) >> 12, -0x7FFE, 0x7FFE);

      sum = ((ptr0[1] * (dec.FiltVal - (dec.FiltVal >> 2))) >> 4) + sum;
      sum -= sum >> 3;
      outBuf[outOff + i] = (short)Math.Clamp((sum + 0x800) >> 12, -0x7FFE, 0x7FFE);
    }
  }

  private static void SavePrevVec(Context dec) {
    for (var i = 0; i < 8; ++i)
      dec.PrevFilt[i] = dec.CVector[i];
  }

  /// <summary>Big-endian MSB-first bit reader matching FFmpeg's <c>get_bits</c> over the bswapped buffer.</summary>
  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public BitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._bitPos = 0;
    }

    public int GetBits(int n) {
      var value = 0;
      for (var i = 0; i < n; ++i) {
        var byteIndex = this._bitPos >> 3;
        var bit = byteIndex < this._data.Length
          ? (this._data[byteIndex] >> (7 - (this._bitPos & 7))) & 1
          : 0;
        value = (value << 1) | bit;
        ++this._bitPos;
      }
      return value;
    }
  }
}
