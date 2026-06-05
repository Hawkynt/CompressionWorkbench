#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Ralf;

/// <summary>
/// RealAudio Lossless ("ralf") decoder — a faithful, decode-only port of FFmpeg's
/// <c>libavcodec/ralf.c</c> with the canonical Huffman tables from <c>ralfdata.h</c>. RALF stores
/// 16-bit (mono or stereo) audio losslessly: each packet carries a block-size table followed by
/// coded blocks; each block carries, per channel, a length, an adaptive LPC filter (Rice/Golomb
/// coded coefficients of up to 63 taps), Golomb-coded residuals and a per-channel bias, then an
/// inter-channel decorrelation mode that recombines the two channels.
/// <para>Construct from the 24-byte "LSD:" extradata (version 0x103, channel count, sample rate,
/// max frame size). Decode-only — there is no encoder; the decoder is stateless across packets
/// (each packet is independently decodable), matching the reference for the non-split stream.</para>
/// </summary>
public sealed class RalfCodec {

  private const int FilterNone = 0;
  private const int FilterRaw = 642;

  /// <summary>Channel count (1 or 2).</summary>
  public int Channels { get; }

  /// <summary>Sample rate in Hz.</summary>
  public int SampleRate { get; }

  /// <summary>Maximum decoded samples per channel per packet.</summary>
  public int MaxFrameSize { get; }

  /// <summary>Extradata format version (only 0x103 is supported).</summary>
  public int Version { get; }

  private readonly VlcSet[] _sets = new VlcSet[3];

  // Per-channel scratch decoded between blocks.
  private readonly int[][] _channelData = [new int[4096], new int[4096]];
  private int _filterParams;
  private int _filterLength;
  private int _filterBits;
  private readonly int[] _filter = new int[64];
  private readonly uint[] _bias = new uint[2];

  /// <summary>
  /// Constructs a decoder from RALF "LSD:" extradata: <c>"LSD:"</c> (4) | be16 version |
  /// be16 reserved | be16 channels | be16 reserved | be32 sample_rate | be32 max_frame_size.
  /// </summary>
  public RalfCodec(ReadOnlySpan<byte> extradata) {
    if (extradata.Length < 24 || extradata[0] != (byte)'L' || extradata[1] != (byte)'S'
        || extradata[2] != (byte)'D' || extradata[3] != (byte)':')
      throw new ArgumentException("RALF extradata is missing the 'LSD:' marker.", nameof(extradata));

    this.Version = BinaryPrimitives.ReadUInt16BigEndian(extradata[4..]);
    if (this.Version != 0x103)
      throw new NotSupportedException($"Unsupported RALF version 0x{this.Version:X}.");

    this.Channels = BinaryPrimitives.ReadUInt16BigEndian(extradata[8..]);
    this.SampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(extradata[12..]);
    if (this.Channels is < 1 or > 2 || this.SampleRate is < 8000 or > 96000)
      throw new ArgumentException($"Invalid RALF coding parameters {this.SampleRate} Hz {this.Channels} ch.", nameof(extradata));

    var maxFrame = (int)BinaryPrimitives.ReadUInt32BigEndian(extradata[16..]);
    if (maxFrame is <= 0 or > (1 << 20))
      throw new ArgumentException($"Invalid RALF frame size {maxFrame}.", nameof(extradata));
    this.MaxFrameSize = Math.Max(maxFrame, this.SampleRate);

    BuildVlcSets(this._sets);
  }

  /// <summary>
  /// Decodes one RALF packet to interleaved signed-16-bit PCM. Returns
  /// <c>samples × channels</c> interleaved samples (samples = decoded length for this packet).
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> packet) {
    if (packet.Length < 5)
      return [];

    var src = packet.ToArray();
    var tableSize = BinaryPrimitives.ReadUInt16BigEndian(src);
    var tableBytes = (tableSize + 7) >> 3;
    if (src.Length < tableBytes + 3)
      return [];

    // Block-size table: read while bits remain.
    var tableReader = new RalfBitReader(src, 2, tableSize);
    var blockSizes = new List<int>();
    while (tableReader.BitsLeft > 0) {
      if (blockSizes.Count >= (1 << 12))
        break;
      var size = tableReader.GetBits(13 + this.Channels);
      // Each entry has a "has pts" flag + optional 9-bit pts that we skip past.
      if (tableReader.GetBit() != 0)
        tableReader.GetBits(9);
      blockSizes.Add(size);
    }

    var samples0 = new short[this.MaxFrameSize];
    var samples1 = this.Channels > 1 ? new short[this.MaxFrameSize] : samples0;

    var blockPointer = tableBytes + 2;
    var bytesLeft = src.Length - tableBytes - 2;
    var sampleOffset = 0;

    foreach (var blockSize in blockSizes) {
      if (bytesLeft < blockSize)
        break;
      var gb = new RalfBitReader(src, blockPointer, blockSize * 8);
      if (!this.DecodeBlock(gb, samples0, samples1, ref sampleOffset))
        break;
      blockPointer += blockSize;
      bytesLeft -= blockSize;
    }

    if (sampleOffset == 0)
      return [];

    var interleaved = new short[sampleOffset * this.Channels];
    for (var i = 0; i < sampleOffset; ++i) {
      interleaved[i * this.Channels] = samples0[i];
      if (this.Channels > 1)
        interleaved[i * this.Channels + 1] = samples1[i];
    }
    return interleaved;
  }

  /// <summary>
  /// Decodes a sequence of independently-coded RALF packets and concatenates the result.
  /// </summary>
  public short[] DecodeStream(IReadOnlyList<byte[]> packets) {
    var all = new List<short>();
    foreach (var pkt in packets)
      all.AddRange(this.Decode(pkt));
    return all.ToArray();
  }

  // ── block decode (ralf.c: decode_block) ─────────────────────────────────────────

  private bool DecodeBlock(RalfBitReader gb, short[] dst0, short[] dst1, ref int sampleOffset) {
    var rawLen = 12 - gb.GetUnary(6);
    var len = rawLen;
    if (len <= 7)
      len ^= 1; // codes for length 6 and 7 are swapped
    len = 1 << len;

    if (sampleOffset + len > this.MaxFrameSize)
      return false;

    int dmode;
    if (this.Channels > 1)
      dmode = gb.GetBits(2) + 1;
    else
      dmode = 0;

    var mode0 = dmode == 4 ? 1 : 0;
    var mode1 = dmode >= 2 ? 2 : 0;
    var bits0 = 16;
    var bits1 = mode1 == 2 ? 17 : 16;

    for (var ch = 0; ch < this.Channels; ++ch) {
      var mode = ch == 0 ? mode0 : mode1;
      var bits = ch == 0 ? bits0 : bits1;
      this.DecodeChannel(gb, ch, len, mode, bits);
      if (this._filterParams > 1 && this._filterParams != FilterRaw) {
        this._filterBits += 3;
        this.ApplyLpc(ch, len, bits);
      }
      if (gb.BitsLeft < 0)
        return false;
    }

    var ch0 = this._channelData[0];
    var ch1 = this._channelData[1];
    switch (dmode) {
      case 0:
        for (var i = 0; i < len; ++i)
          dst0[sampleOffset + i] = (short)(ch0[i] + (int)this._bias[0]);
        break;
      case 1:
        for (var i = 0; i < len; ++i) {
          dst0[sampleOffset + i] = (short)(ch0[i] + (int)this._bias[0]);
          dst1[sampleOffset + i] = (short)(ch1[i] + (int)this._bias[1]);
        }
        break;
      case 2:
        for (var i = 0; i < len; ++i) {
          ch0[i] += (int)this._bias[0];
          dst0[sampleOffset + i] = (short)ch0[i];
          dst1[sampleOffset + i] = (short)(ch0[i] - (ch1[i] + (int)this._bias[1]));
        }
        break;
      case 3:
        for (var i = 0; i < len; ++i) {
          var t = (uint)ch0[i] + this._bias[0];
          var t2 = (uint)ch1[i] + this._bias[1];
          dst0[sampleOffset + i] = (short)(t + t2);
          dst1[sampleOffset + i] = (short)t;
        }
        break;
      case 4:
        for (var i = 0; i < len; ++i) {
          var t = (uint)ch1[i] + this._bias[1];
          var t2 = (uint)((ch0[i] + (int)this._bias[0]) * 2) | (t & 1);
          dst0[sampleOffset + i] = (short)((int)(t2 + t) / 2);
          dst1[sampleOffset + i] = (short)((int)(t2 - t) / 2);
        }
        break;
    }

    sampleOffset += len;
    return true;
  }

  // ── channel decode (ralf.c: decode_channel) ─────────────────────────────────────

  private void DecodeChannel(RalfBitReader gb, int ch, int length, int mode, int bits) {
    var set = this._sets[mode];
    var dst = this._channelData[ch];

    this._filterParams = set.FilterParams.Decode(gb);
    if (this._filterParams > 1) {
      this._filterBits = (this._filterParams - 2) >> 6;
      this._filterLength = this._filterParams - (this._filterBits << 6) - 1;
    }

    if (this._filterParams == FilterRaw) {
      for (var i = 0; i < length; ++i)
        dst[i] = gb.GetBits(bits);
      this._bias[ch] = 0;
      return;
    }

    var bias = set.Bias.Decode(gb);
    this._bias[ch] = (uint)ExtendCode(gb, bias, 127, 4);

    if (this._filterParams == FilterNone) {
      Array.Clear(dst, 0, length);
      return;
    }

    if (this._filterParams > 1) {
      var cmode = 0;
      var coeff = 0;
      var addBits = this._filterBits;
      for (var i = 0; i < this._filterLength; ++i) {
        var t = set.FilterCoeffs[this._filterBits][5 + cmode].Decode(gb);
        t = ExtendCode(gb, t, 21, addBits);
        if (cmode == 0)
          coeff -= 12 << addBits;
        coeff = t - coeff;
        this._filter[i] = coeff;

        cmode = coeff >> addBits;
        if (cmode < 0) {
          cmode = -1 - Log2(-cmode);
          if (cmode < -5)
            cmode = -5;
        } else if (cmode > 0) {
          cmode = 1 + Log2(cmode);
          if (cmode > 5)
            cmode = 5;
        }
      }
    }

    var codeParams = set.CodingMode.Decode(gb);
    int range, range2, addBits2;
    RalfVlc codeVlc;
    if (codeParams >= 15) {
      addBits2 = Math.Clamp((codeParams / 5 - 3) / 2, 0, 10);
      if (addBits2 > 9 && codeParams % 5 != 2)
        --addBits2;
      range = 10;
      range2 = 21;
      codeVlc = set.LongCodes[codeParams - 15];
    } else {
      addBits2 = 0;
      range = 6;
      range2 = 13;
      codeVlc = set.ShortCodes[codeParams];
    }

    for (var i = 0; i < length; i += 2) {
      var t = codeVlc.Decode(gb);
      var code1 = t / range2;
      var code2 = t % range2;
      dst[i] = (int)((uint)ExtendCode(gb, code1, range, 0) << addBits2);
      dst[i + 1] = (int)((uint)ExtendCode(gb, code2, range, 0) << addBits2);
      if (addBits2 != 0) {
        dst[i] |= gb.GetBits(addBits2);
        dst[i + 1] |= gb.GetBits(addBits2);
      }
    }
  }

  private static int ExtendCode(RalfBitReader gb, int val, int range, int bits) {
    if (val == 0)
      val = -range - gb.GetUeGolomb();
    else if (val == range * 2)
      val = range + gb.GetUeGolomb();
    else
      val -= range;
    if (bits != 0)
      val = (int)((uint)val << bits) | gb.GetBits(bits);
    return val;
  }

  private void ApplyLpc(int ch, int length, int bits) {
    var audio = this._channelData[ch];
    var bias = 1 << (this._filterBits - 1);
    var maxClip = (1 << bits) - 1;
    var minClip = -maxClip - 1;

    for (var i = 1; i < length; ++i) {
      var flen = Math.Min(this._filterLength, i);
      long acc = 0;
      for (var j = 0; j < flen; ++j)
        acc += (long)(uint)this._filter[j] * (uint)audio[i - j - 1];
      var accI = (int)acc;
      if (accI < 0) {
        accI = (accI + bias - 1) >> this._filterBits;
        accI = Math.Max(accI, minClip);
      } else {
        accI = (int)(((uint)accI + (uint)bias) >> this._filterBits);
        accI = Math.Min(accI, maxClip);
      }
      audio[i] += accI;
    }
  }

  private static int Log2(int v) {
    var n = -1;
    while (v > 0) { v >>= 1; ++n; }
    return n;
  }

  // ── VLC set construction (ralf.c: decode_init) ───────────────────────────────────

  private sealed class VlcSet {
    public RalfVlc FilterParams = null!;
    public RalfVlc Bias = null!;
    public RalfVlc CodingMode = null!;
    public readonly RalfVlc[][] FilterCoeffs = new RalfVlc[10][];
    public readonly RalfVlc[] ShortCodes = new RalfVlc[15];
    public readonly RalfVlc[] LongCodes = new RalfVlc[125];
  }

  private static void BuildVlcSets(VlcSet[] sets) {
    var filterParam = RalfTables.FilterParamBytes;   // [3][324]
    var biasData = RalfTables.BiasBytes;             // [3][128]
    var codingMode = RalfTables.CodingModeBytes;     // [3][72]
    var filterCoeffs = RalfTables.FilterCoeffsBytes; // [3][10][11][24]
    var shortCodes = RalfTables.ShortCodesBytes;     // [3][15][88]
    var longCodes = RalfTables.LongCodesBytes;       // [3][125][224]

    for (var i = 0; i < 3; ++i) {
      var set = new VlcSet {
        FilterParams = new RalfVlc(filterParam, i * 324, RalfTables.FilterParamElements),
        Bias = new RalfVlc(biasData, i * 128, RalfTables.BiasElements),
        CodingMode = new RalfVlc(codingMode, i * 72, RalfTables.CodingModeElements),
      };
      for (var j = 0; j < 10; ++j) {
        set.FilterCoeffs[j] = new RalfVlc[11];
        for (var k = 0; k < 11; ++k) {
          var off = ((i * 10 + j) * 11 + k) * 24;
          set.FilterCoeffs[j][k] = new RalfVlc(filterCoeffs, off, RalfTables.FilterCoeffsElements);
        }
      }
      for (var j = 0; j < 15; ++j)
        set.ShortCodes[j] = new RalfVlc(shortCodes, (i * 15 + j) * 88, RalfTables.ShortCodesElements);
      for (var j = 0; j < 125; ++j)
        set.LongCodes[j] = new RalfVlc(longCodes, (i * 125 + j) * 224, RalfTables.LongCodesElements);
      sets[i] = set;
    }
  }
}
