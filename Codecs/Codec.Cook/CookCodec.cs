#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Cook;

/// <summary>
/// Cook / RealAudio G2 ("cook" FOURCC) decoder — a faithful, decode-only port of FFmpeg's
/// <c>libavcodec/cook.c</c> (float path). Cook is a modulated-lapped-transform audio coder
/// derived from G.722.1: each subpacket carries a gain envelope, a per-subband category /
/// bit-allocation, scalar-quantised MLT coefficients (with dithered noise filling), an
/// inverse MLT with 50% overlap-add and a gain profile, plus optional joint-stereo
/// decoupling for the higher subbands.
/// <para>Construct with the extradata that follows the RA header in the RealMedia MDPR blob
/// (cookversion, samples-per-frame, subbands, and — for joint stereo / multichannel — the
/// joint-stereo subband start and VLC bits / channel mask) together with the RA framing
/// parameters. <see cref="Decode"/> turns one <c>block_align</c>-sized coded frame into
/// interleaved 16-bit PCM; <see cref="DecodeStream"/> walks a concatenation of frames.</para>
/// <para>Like the reference, the first two decoded frames carry no valid audio (the MLT
/// overlap is not yet primed) and are discarded.</para>
/// </summary>
public sealed class CookCodec {

  private const int SubbandSize = 20;
  private const int MaxSubpackets = 5;
  private const int Mono = 0x1000001;
  private const int Stereo = 0x1000002;
  private const int JointStereo = 0x1000003;
  private const int McCook = 0x2000000;

  private readonly Subpacket[] _subpackets;
  private readonly int _numSubpackets;
  private readonly int _samplesPerChannel;
  private readonly int _channels;
  private readonly int _blockAlign;
  private readonly int _gainSizeFactor;
  private readonly float[] _gainTable = new float[31];
  private readonly float[] _mltWindow;
  private readonly CookLfg _random = new(0);

  // Shared scratch buffers (mirroring the reference's per-context arrays).
  private readonly float[] _monoMdctOutput;
  private readonly float[] _decodeBuffer1 = new float[1024];
  private readonly float[] _decodeBuffer2 = new float[1024];
  private readonly float[] _decodeBuffer0 = new float[1060];

  private readonly CookVlc[] _envelopeQuantIndex = new CookVlc[13];
  private readonly CookVlc[] _sqvh = new CookVlc[7];

  private static readonly float[] Pow2Tab = new float[127];
  private static readonly float[] RootPow2Tab = new float[127];

  static CookCodec() {
    // init_pow2table — 2^i and 2^(0.5*i) for -63 <= i < 64.
    float[] exp2Tab = [1f, (float)Math.Sqrt(2.0)];
    var exp2Val = (float)Math.Pow(2, -63);
    var rootVal = (float)Math.Pow(2, -32);
    for (var i = -63; i < 64; ++i) {
      if ((i & 1) == 0)
        rootVal *= 2;
      Pow2Tab[63 + i] = exp2Val;
      RootPow2Tab[63 + i] = rootVal * exp2Tab[((i % 2) + 2) % 2];
      exp2Val *= 2;
    }
  }

  /// <summary>Parsed RA framing + cook extradata describing one cook stream.</summary>
  public sealed class StreamInfo {
        /// <summary>
    /// Provides the channels value.
    /// </summary>
public int Channels;
        /// <summary>
    /// Provides the sample rate value.
    /// </summary>
public int SampleRate;
        /// <summary>
    /// Provides the block align value.
    /// </summary>
public int BlockAlign;       // coded frame size fed to the decoder (== sub_packet_size for cook)
        /// <summary>
    /// Provides the extradata value.
    /// </summary>
public byte[] Extradata = [];
  }

  private sealed class Subpacket {
    public int Cookversion;
    public int Subbands;
    public int JsSubbandStart;
    public int JsVlcBits;
    public int SamplesPerChannel;
    public int Log2NumvectorSize = 5;
    public int NumvectorSize;
    public int TotalSubbands;
    public int NumChannels = 1;
    public bool JointStereo;
    public int BitsPerSubpacket;
    public int BitsPerSubpdiv;
    public int ChIdx;
    public int Size;
    public uint ChannelMask;
    public CookVlc? ChannelCoupling;

    public readonly float[] MonoPreviousBuffer1 = new float[1024];
    public readonly float[] MonoPreviousBuffer2 = new float[1024];
    public int[] Gain1Now = new int[9];
    public int[] Gain1Prev = new int[9];
    public int[] Gain2Now = new int[9];
    public int[] Gain2Prev = new int[9];
  }

  private int _numVectors;
  private int _discardedPackets;

  /// <summary>
  /// Builds a decoder from a parsed <see cref="StreamInfo"/>. Throws
  /// <see cref="NotSupportedException"/> for cook variants outside the supported set
  /// (mono / stereo / joint-stereo / single-pair multichannel, samples-per-channel in
  /// {256, 512, 1024}) — the caller catches this to fall back to a blob-only view.
  /// </summary>
  public CookCodec(StreamInfo info) {
    this._channels = info.Channels;
    this._blockAlign = info.BlockAlign;
    if (this._channels <= 0)
      throw new NotSupportedException("Cook: invalid channel count.");
    if (this._blockAlign <= 0)
      throw new NotSupportedException("Cook: invalid block align.");
    if (info.Extradata.Length < 8)
      throw new NotSupportedException("Cook: extradata too short.");

    var subpackets = new List<Subpacket>();
    var pos = 0;
    var ed = info.Extradata;
    var samplesPerChannel = 0;
    uint channelMask = 0;

    while (pos < ed.Length) {
      if (subpackets.Count >= Math.Min(MaxSubpackets, this._blockAlign))
        throw new NotSupportedException("Cook: too many subpackets.");
      if (pos + 12 > ed.Length)
        throw new NotSupportedException("Cook: truncated extradata.");

      var p = new Subpacket {
        Cookversion = (int)BinaryPrimitives.ReadUInt32BigEndian(ed.AsSpan(pos)),
      };
      var samplesPerFrame = BinaryPrimitives.ReadUInt16BigEndian(ed.AsSpan(pos + 4));
      p.Subbands = BinaryPrimitives.ReadUInt16BigEndian(ed.AsSpan(pos + 6));
      // pos+8..11: unknown unused u32
      p.JsSubbandStart = BinaryPrimitives.ReadUInt16BigEndian(ed.AsSpan(pos + 12 - 2));
      pos += 14;
      if (p.JsSubbandStart >= 51)
        throw new NotSupportedException("Cook: js_subband_start too large.");
      if (pos + 2 > ed.Length)
        throw new NotSupportedException("Cook: truncated extradata.");
      p.JsVlcBits = BinaryPrimitives.ReadUInt16BigEndian(ed.AsSpan(pos));
      pos += 2;

      p.SamplesPerChannel = samplesPerFrame / this._channels;
      p.BitsPerSubpacket = this._blockAlign * 8;
      p.TotalSubbands = p.Subbands;

      switch (p.Cookversion) {
        case Mono:
          if (this._channels != 1)
            throw new NotSupportedException("Cook: MONO with channels != 1.");
          break;
        case Stereo:
          if (this._channels != 1) {
            p.BitsPerSubpdiv = 1;
            p.NumChannels = 2;
          }
          break;
        case JointStereo:
          if (this._channels != 2)
            throw new NotSupportedException("Cook: JOINT_STEREO with channels != 2.");
          if (ed.Length >= 16) {
            p.TotalSubbands = p.Subbands + p.JsSubbandStart;
            p.JointStereo = true;
            p.NumChannels = 2;
          }
          if (p.SamplesPerChannel > 256) p.Log2NumvectorSize = 6;
          if (p.SamplesPerChannel > 512) p.Log2NumvectorSize = 7;
          break;
        case McCook:
          if (pos + 4 > ed.Length)
            throw new NotSupportedException("Cook: truncated multichannel mask.");
          p.ChannelMask = BinaryPrimitives.ReadUInt32BigEndian(ed.AsSpan(pos));
          pos += 4;
          channelMask |= p.ChannelMask;
          if (PopCount(p.ChannelMask) > 1) {
            p.TotalSubbands = p.Subbands + p.JsSubbandStart;
            p.JointStereo = true;
            p.NumChannels = 2;
            p.SamplesPerChannel = samplesPerFrame >> 1;
            if (p.SamplesPerChannel > 256) p.Log2NumvectorSize = 6;
            if (p.SamplesPerChannel > 512) p.Log2NumvectorSize = 7;
          } else {
            p.SamplesPerChannel = samplesPerFrame;
          }
          break;
        default:
          throw new NotSupportedException($"Cook: unsupported version {p.Cookversion:X}.");
      }

      samplesPerChannel = subpackets.Count == 0 ? p.SamplesPerChannel : samplesPerChannel;
      p.NumvectorSize = 1 << p.Log2NumvectorSize;

      if (p.TotalSubbands > 53)
        throw new NotSupportedException("Cook: total_subbands > 53.");
      if (p.JsVlcBits > 6 || p.JsVlcBits < 2 * (p.JointStereo ? 1 : 0))
        throw new NotSupportedException("Cook: invalid js_vlc_bits.");
      if (p.Subbands is 0 or > 50)
        throw new NotSupportedException("Cook: subbands out of range.");

      subpackets.Add(p);
    }

    if (samplesPerChannel is not (256 or 512 or 1024))
      throw new NotSupportedException($"Cook: samples_per_channel = {samplesPerChannel}.");

    this._subpackets = subpackets.ToArray();
    this._numSubpackets = this._subpackets.Length;
    this._samplesPerChannel = samplesPerChannel;
    this._monoMdctOutput = new float[2 * this._samplesPerChannel];

    // init_gain_table.
    this._gainSizeFactor = this._samplesPerChannel / 8;
    for (var i = 0; i < 31; ++i)
      this._gainTable[i] = (float)Math.Pow(Pow2Tab[i + 48], 1.0 / this._gainSizeFactor);

    // init_cook_vlc_tables.
    for (var i = 0; i < 13; ++i)
      this._envelopeQuantIndex[i] = new CookVlc(
        CookTables.EnvelopeQuantIndexHuffCounts[i],
        Array.ConvertAll(CookTables.EnvelopeQuantIndexHuffSyms[i], b => (int)b), -12);
    for (var i = 0; i < 7; ++i)
      this._sqvh[i] = new CookVlc(CookTables.CvhHuffCounts[i], CookTables.CvhHuffSyms[i], 0);
    foreach (var p in this._subpackets)
      if (p.JointStereo)
        p.ChannelCoupling = new CookVlc(
          CookTables.CcplHuffCounts[p.JsVlcBits - 2], CookTables.CcplHuffSyms[p.JsVlcBits - 2], 0);

    // init_cook_mlt — sine window scaled by sqrt(2/N).
    this._mltWindow = new float[this._samplesPerChannel];
    var winScale = Math.Sqrt(2.0 / this._samplesPerChannel);
    for (var j = 0; j < this._samplesPerChannel; ++j)
      this._mltWindow[j] = (float)(Math.Sin((j + 0.5) * (Math.PI / (2.0 * this._samplesPerChannel))) * winScale);
  }

  /// <summary>Samples produced per channel per coded frame.</summary>
  public int SamplesPerChannel => this._samplesPerChannel;

  /// <summary>Channels in the decoded output.</summary>
  public int Channels => this._channels;

  /// <summary>
  /// Decodes one <c>block_align</c>-sized coded frame to interleaved 16-bit PCM. The first
  /// two frames return an all-zero buffer (the reference discards them while the MLT
  /// overlap primes). Returns <see cref="SamplesPerChannel"/> × <see cref="Channels"/>
  /// samples on every call so a stream walk has an exact, predictable length.
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> frame) {
    var floatOut = new float[this._channels][];
    for (var c = 0; c < this._channels; ++c)
      floatOut[c] = new float[this._samplesPerChannel];

    var buf = new byte[this._blockAlign];
    var copy = Math.Min(frame.Length, this._blockAlign);
    frame[..copy].CopyTo(buf);

    // Estimate subpacket sizes (decode_frame).
    this._subpackets[0].Size = this._blockAlign;
    for (var i = 1; i < this._numSubpackets; ++i) {
      this._subpackets[i].Size = 2 * buf[this._blockAlign - this._numSubpackets + i];
      this._subpackets[0].Size -= this._subpackets[i].Size + 1;
      if (this._subpackets[0].Size < 0)
        throw new InvalidDataException("Cook: subpacket size total > block_align.");
    }

    var offset = 0;
    var chidx = 0;
    for (var i = 0; i < this._numSubpackets; ++i) {
      var p = this._subpackets[i];
      p.BitsPerSubpacket = (p.Size * 8) >> p.BitsPerSubpdiv;
      p.ChIdx = chidx;
      this.DecodeSubpacket(p, buf, offset, floatOut);
      offset += p.Size;
      chidx += p.NumChannels;
    }

    var pcm = new short[this._samplesPerChannel * this._channels];
    var discarded = this._discardedPackets < 2;
    if (discarded)
      ++this._discardedPackets;

    if (!discarded) {
      for (var n = 0; n < this._samplesPerChannel; ++n)
        for (var c = 0; c < this._channels; ++c) {
          var v = floatOut[c][n];
          v = v < -1f ? -1f : v > 1f ? 1f : v;
          pcm[n * this._channels + c] = (short)Math.Round(v * 32767.0f);
        }
    }
    return pcm;
  }

  /// <summary>
  /// Decodes a concatenation of <see cref="Decode"/> frames (already deinterleaved into the
  /// codec's per-frame coded layout) to interleaved 16-bit PCM. Frames shorter than a full
  /// <c>block_align</c> at the tail are still padded and decoded (matching the reference's
  /// zero-padded over-read tolerance).
  /// </summary>
  public short[] DecodeStream(ReadOnlySpan<byte> framesConcat) {
    var frames = framesConcat.Length / this._blockAlign;
    if (framesConcat.Length % this._blockAlign != 0 && framesConcat.Length > 0)
      ++frames;
    if (frames == 0)
      return [];

    var perFrame = this._samplesPerChannel * this._channels;
    var output = new short[frames * perFrame];
    for (var f = 0; f < frames; ++f) {
      var start = f * this._blockAlign;
      var len = Math.Min(this._blockAlign, framesConcat.Length - start);
      var pcm = this.Decode(framesConcat.Slice(start, len));
      pcm.CopyTo(output, f * perFrame);
    }
    return output;
  }

  // ── subpacket decode ──────────────────────────────────────────────────────

  private void DecodeSubpacket(Subpacket p, byte[] inbuffer, int inOffset, float[][] outbuffer) {
    Array.Clear(this._decodeBuffer1);

    var subPacketSize = p.Size;
    var gb1 = this.DecodeBytesAndGain(p, inbuffer, inOffset, gainsSet: 1);

    if (p.JointStereo) {
      this.JointDecode(p, gb1, this._decodeBuffer1, this._decodeBuffer2);
    } else {
      this.MonoDecode(p, gb1, this._decodeBuffer1);
      if (p.NumChannels == 2) {
        var gb2 = this.DecodeBytesAndGain(p, inbuffer, inOffset + subPacketSize / 2, gainsSet: 2);
        this.MonoDecode(p, gb2, this._decodeBuffer2);
      }
    }

    // After the swap inside DecodeBytesAndGain, Gain*Now holds the previous-frame gains
    // (used for the gain profile) and Gain*Prev holds the just-decoded gains (used by the
    // window via index 0), exactly as the reference's post-swap cook_gains pointers.
    this.MltCompensateOutput(this._decodeBuffer1, p.Gain1Now, p.Gain1Prev, p.MonoPreviousBuffer1,
      outbuffer, p.ChIdx);

    if (p.NumChannels == 2) {
      if (p.JointStereo)
        this.MltCompensateOutput(this._decodeBuffer2, p.Gain1Now, p.Gain1Prev, p.MonoPreviousBuffer2,
          outbuffer, p.ChIdx + 1);
      else
        this.MltCompensateOutput(this._decodeBuffer2, p.Gain2Now, p.Gain2Prev, p.MonoPreviousBuffer2,
          outbuffer, p.ChIdx + 1);
    }
  }

  private CookBitReader DecodeBytesAndGain(Subpacket p, byte[] inbuffer, int inOffset, int gainsSet) {
    var decoded = DecodeBytes(inbuffer, inOffset, p.BitsPerSubpacket / 8, out var offset);
    var gb = new CookBitReader(decoded, offset, p.BitsPerSubpacket);
    // Fill the "now" gains, then swap now<->previous (persists across frames like FFmpeg's
    // pointer swap). The swapped state is what the synthesis path reads afterward.
    if (gainsSet == 1) {
      DecodeGainInfo(gb, p.Gain1Now);
      (p.Gain1Now, p.Gain1Prev) = (p.Gain1Prev, p.Gain1Now);
    } else {
      DecodeGainInfo(gb, p.Gain2Now);
      (p.Gain2Now, p.Gain2Prev) = (p.Gain2Prev, p.Gain2Now);
    }
    return gb;
  }

  // Big-endian bytes of the 0x37c511f2 descramble key. Reading the input as native-endian
  // uint32 and XOR-ing with AV_BE2NE32C(0x37c511f2) — then storing native-endian — is, byte
  // for byte and on either host endianness, just a XOR of the input stream with this BE
  // pattern. We always read from a fresh buffer (offset 0), so no word-rotation is needed.
  private static readonly byte[] DescrambleKey = [0x37, 0xc5, 0x11, 0xf2];

  /// <summary>
  /// decode_bytes — descramble the input by XOR-ing it word-aligned with 0x37c511f2 into a
  /// fresh, word-padded buffer; returns it and the within-word read offset (always 0 here).
  /// Reproduces the reference's word-wise descramble and tail padding.
  /// </summary>
  private static byte[] DecodeBytes(byte[] inbuffer, int inOffset, int bytes, out int offset) {
    offset = 0;
    var total = bytes + 3 + offset;
    var words = total / 4;
    var output = new byte[words * 4 + 4];
    for (var i = 0; i < words * 4; ++i) {
      var s = inOffset + i;
      var bv = s < inbuffer.Length ? inbuffer[s] : (byte)0;
      output[i] = (byte)(bv ^ DescrambleKey[i & 3]);
    }
    return output;
  }

  private static void DecodeGainInfo(CookBitReader gb, int[] gaininfo) {
    var n = GetUnary(gb, 0, gb.BitsLeft);
    var i = 0;
    while (n-- > 0) {
      var index = gb.GetBits(3);
      var gain = gb.GetBit() != 0 ? gb.GetBits(4) - 7 : -1;
      while (i <= index)
        gaininfo[i++] = gain;
    }
    while (i <= 8)
      gaininfo[i++] = 0;
  }

  private static int GetUnary(CookBitReader gb, int stop, int len) {
    var i = 0;
    while (i < len && gb.GetBit() != stop)
      ++i;
    return i;
  }

  private void MonoDecode(Subpacket p, CookBitReader gb, float[] mltBuffer) {
    var categoryIndex = new int[128];
    var category = new int[128];
    var quantIndexTable = new int[102];

    this.DecodeEnvelope(p, gb, quantIndexTable);
    this._numVectors = gb.GetBits(p.Log2NumvectorSize);
    this.Categorize(p, gb, quantIndexTable, category, categoryIndex);
    this.ExpandCategory(category, categoryIndex);
    for (var i = 0; i < p.TotalSubbands; ++i)
      if (category[i] > 7)
        throw new InvalidDataException("Cook: category > 7.");
    this.DecodeVectors(p, gb, category, quantIndexTable, mltBuffer);
  }

  private void DecodeEnvelope(Subpacket p, CookBitReader gb, int[] quantIndexTable) {
    quantIndexTable[0] = gb.GetBits(6) - 6;
    for (var i = 1; i < p.TotalSubbands; ++i) {
      var vlcIndex = i;
      if (i >= p.JsSubbandStart * 2) {
        vlcIndex -= p.JsSubbandStart;
      } else {
        vlcIndex /= 2;
        if (vlcIndex < 1)
          vlcIndex = 1;
      }
      if (vlcIndex > 13)
        vlcIndex = 13;
      var j = this._envelopeQuantIndex[vlcIndex - 1].Decode(gb);
      quantIndexTable[i] = quantIndexTable[i - 1] + j;
      if (quantIndexTable[i] is > 63 or < -63)
        throw new InvalidDataException("Cook: quantizer out of [-63,63].");
    }
  }

  private void Categorize(Subpacket p, CookBitReader gb, int[] quantIndexTable,
      int[] category, int[] categoryIndex) {
    var expIndex1 = new int[102];
    var expIndex2 = new int[102];
    var tmpCategorizeArray = new int[128 * 2];
    var tmpIdx1 = p.NumvectorSize;
    var tmpIdx2 = p.NumvectorSize;

    var bitsLeft = p.BitsPerSubpacket - gb.BitsCount;
    if (bitsLeft > this._samplesPerChannel)
      bitsLeft = this._samplesPerChannel + (bitsLeft - this._samplesPerChannel) * 5 / 8;

    var bias = -32;
    for (var i = 32; i > 0; i /= 2) {
      var numBits = 0;
      var index = 0;
      for (var jj = p.TotalSubbands; jj > 0; --jj) {
        var expIdx = ClipUintP2((i - quantIndexTable[index] + bias) / 2, 3);
        ++index;
        numBits += CookTables.ExpBitsTab[expIdx];
      }
      if (numBits >= bitsLeft - 32)
        bias += i;
    }

    var totalBits = 0;
    for (var i = 0; i < p.TotalSubbands; ++i) {
      var expIdx = ClipUintP2((bias - quantIndexTable[i]) / 2, 3);
      totalBits += CookTables.ExpBitsTab[expIdx];
      expIndex1[i] = expIdx;
      expIndex2[i] = expIdx;
    }
    var tmpbias1 = totalBits;
    var tmpbias2 = totalBits;

    for (var j = 1; j < p.NumvectorSize; ++j) {
      if (tmpbias1 + tmpbias2 > 2 * bitsLeft) {
        var max = -999999;
        var index = -1;
        for (var i = 0; i < p.TotalSubbands; ++i)
          if (expIndex1[i] < 7) {
            var v = (-2 * expIndex1[i]) - quantIndexTable[i] + bias;
            if (v >= max) { max = v; index = i; }
          }
        if (index == -1) break;
        tmpCategorizeArray[tmpIdx1++] = index;
        tmpbias1 -= CookTables.ExpBitsTab[expIndex1[index]] - CookTables.ExpBitsTab[expIndex1[index] + 1];
        ++expIndex1[index];
      } else {
        var min = 999999;
        var index = -1;
        for (var i = 0; i < p.TotalSubbands; ++i)
          if (expIndex2[i] > 0) {
            var v = (-2 * expIndex2[i]) - quantIndexTable[i] + bias;
            if (v < min) { min = v; index = i; }
          }
        if (index == -1) break;
        tmpCategorizeArray[--tmpIdx2] = index;
        tmpbias2 -= CookTables.ExpBitsTab[expIndex2[index]] - CookTables.ExpBitsTab[expIndex2[index] - 1];
        --expIndex2[index];
      }
    }

    for (var i = 0; i < p.TotalSubbands; ++i)
      category[i] = expIndex2[i];
    for (var i = 0; i < p.NumvectorSize - 1; ++i)
      categoryIndex[i] = tmpCategorizeArray[tmpIdx2++];
  }

  private void ExpandCategory(int[] category, int[] categoryIndex) {
    for (var i = 0; i < this._numVectors; ++i) {
      var idx = categoryIndex[i];
      if (++category[idx] >= CookTables.DitherTab.Length)
        --category[idx];
    }
  }

  private void DecodeVectors(Subpacket p, CookBitReader gb, int[] category,
      int[] quantIndexTable, float[] mltBuffer) {
    var subbandCoefIndex = new int[SubbandSize];
    var subbandCoefSign = new int[SubbandSize];

    for (var band = 0; band < p.TotalSubbands; ++band) {
      var index = category[band];
      if (category[band] < 7) {
        if (this.UnpackSqvh(p, gb, category[band], subbandCoefIndex, subbandCoefSign)) {
          index = 7;
          for (var j = 0; j < p.TotalSubbands; ++j)
            if (band + j < category.Length)
              category[band + j] = 7;
        }
      }
      if (index >= 7) {
        Array.Clear(subbandCoefIndex);
        Array.Clear(subbandCoefSign);
      }
      this.ScalarDequant(index, quantIndexTable[band], subbandCoefIndex, subbandCoefSign,
        mltBuffer, band * SubbandSize);
    }
  }

  private bool UnpackSqvh(Subpacket p, CookBitReader gb, int category,
      int[] subbandCoefIndex, int[] subbandCoefSign) {
    var vd = CookTables.VdTab[category];
    var result = false;
    for (var i = 0; i < CookTables.VprTab[category]; ++i) {
      var vlc = this._sqvh[category].Decode(gb);
      if (p.BitsPerSubpacket < gb.BitsCount) {
        vlc = 0;
        result = true;
      }
      for (var j = vd - 1; j >= 0; --j) {
        var tmp = (vlc * CookTables.InvRadixTab[category]) / 0x100000;
        subbandCoefIndex[vd * i + j] = vlc - tmp * (CookTables.KmaxTab[category] + 1);
        vlc = tmp;
      }
      for (var j = 0; j < vd; ++j) {
        if (subbandCoefIndex[i * vd + j] != 0) {
          if (gb.BitsCount < p.BitsPerSubpacket) {
            subbandCoefSign[i * vd + j] = gb.GetBit();
          } else {
            result = true;
            subbandCoefSign[i * vd + j] = 0;
          }
        } else {
          subbandCoefSign[i * vd + j] = 0;
        }
      }
    }
    return result;
  }

  private void ScalarDequant(int index, int quantIndex, int[] subbandCoefIndex,
      int[] subbandCoefSign, float[] mltP, int mltOffset) {
    for (var i = 0; i < SubbandSize; ++i) {
      float f1;
      if (subbandCoefIndex[i] != 0) {
        f1 = CookTables.QuantCentroidTab[index][subbandCoefIndex[i]];
        if (subbandCoefSign[i] != 0)
          f1 = -f1;
      } else {
        f1 = CookTables.DitherTab[index];
        if (this._random.Get() < 0x80000000u)
          f1 = -f1;
      }
      mltP[mltOffset + i] = f1 * RootPow2Tab[quantIndex + 63];
    }
  }

  // ── joint stereo ──────────────────────────────────────────────────────────

  private void JointDecode(Subpacket p, CookBitReader gb, float[] mltLeft, float[] mltRight) {
    var decoupleTab = new int[SubbandSize];
    var decodeBuffer = this._decodeBuffer0;
    Array.Clear(decodeBuffer);
    Array.Clear(mltLeft, 0, 1024);
    Array.Clear(mltRight, 0, 1024);

    this.DecoupleInfo(p, gb, decoupleTab);
    this.MonoDecode(p, gb, decodeBuffer);

    for (var i = 0; i < p.JsSubbandStart; ++i)
      for (var j = 0; j < SubbandSize; ++j) {
        mltLeft[i * 20 + j] = decodeBuffer[i * 40 + j];
        mltRight[i * 20 + j] = decodeBuffer[i * 40 + 20 + j];
      }

    var idx = (1 << p.JsVlcBits) - 1;
    var cplscale = CookTables.CplScales[p.JsVlcBits - 2];
    for (var i = p.JsSubbandStart; i < p.Subbands; ++i) {
      var cplTmp = CookTables.CplBand[i];
      idx -= decoupleTab[cplTmp];
      var f1 = cplscale[decoupleTab[cplTmp] + 1];
      var f2 = cplscale[idx];
      for (var j = 0; j < SubbandSize; ++j) {
        var tmpIdx = ((p.JsSubbandStart + i) * SubbandSize) + j;
        mltLeft[SubbandSize * i + j] = f1 * decodeBuffer[tmpIdx];
        mltRight[SubbandSize * i + j] = f2 * decodeBuffer[tmpIdx];
      }
      idx = (1 << p.JsVlcBits) - 1;
    }
  }

  private void DecoupleInfo(Subpacket p, CookBitReader gb, int[] decoupleTab) {
    var vlc = gb.GetBit();
    var start = CookTables.CplBand[p.JsSubbandStart];
    var end = CookTables.CplBand[p.Subbands - 1];
    var length = end - start + 1;
    if (start > end)
      return;
    if (vlc != 0) {
      for (var i = 0; i < length; ++i)
        decoupleTab[start + i] = p.ChannelCoupling!.Decode(gb);
    } else {
      for (var i = 0; i < length; ++i) {
        var v = gb.GetBits(p.JsVlcBits);
        if (v == (1 << p.JsVlcBits) - 1)
          throw new InvalidDataException("Cook: decouple value too large.");
        decoupleTab[start + i] = v;
      }
    }
  }

  // ── MLT / gain ────────────────────────────────────────────────────────────

  private void MltCompensateOutput(float[] decodeBuffer, int[] gainsNow, int[] gainsPrev,
      float[] previousBuffer, float[][] outbuffer, int chIdx) {
    this.ImltGain(decodeBuffer, gainsNow, gainsPrev, previousBuffer);
    if (outbuffer != null && chIdx < outbuffer.Length) {
      var outArr = outbuffer[chIdx];
      for (var i = 0; i < this._samplesPerChannel; ++i) {
        var v = this._monoMdctOutput[this._samplesPerChannel + i];
        outArr[i] = v < -1f ? -1f : v > 1f ? 1f : v;
      }
    }
  }

  private void ImltGain(float[] inbuffer, int[] gainsNow, int[] gainsPrev, float[] previousBuffer) {
    // Full inverse MLT into mono_mdct_output[0 .. 2N-1].
    this.FullImdct(inbuffer, this._monoMdctOutput);

    // imlt_window: window + overlap into the second half (buffer1).
    var fc = Pow2Tab[gainsPrev[0] + 63];
    for (var i = 0; i < this._samplesPerChannel; ++i)
      this._monoMdctOutput[this._samplesPerChannel + i] =
        this._monoMdctOutput[this._samplesPerChannel + i] * fc * this._mltWindow[i] -
        previousBuffer[i] * this._mltWindow[this._samplesPerChannel - 1 - i];

    // Apply gain profile.
    for (var i = 0; i < 8; ++i)
      if (gainsNow[i] != 0 || gainsNow[i + 1] != 0)
        this.Interpolate(this._monoMdctOutput, this._samplesPerChannel + this._gainSizeFactor * i,
          gainsNow[i], gainsNow[i + 1]);

    // Save current (first half) as next previous_buffer.
    Array.Copy(this._monoMdctOutput, 0, previousBuffer, 0, this._samplesPerChannel);
  }

  private void Interpolate(float[] buffer, int bufOffset, int gainIndex, int gainIndexNext) {
    var fc1 = Pow2Tab[gainIndex + 63];
    if (gainIndex == gainIndexNext) {
      for (var i = 0; i < this._gainSizeFactor; ++i)
        buffer[bufOffset + i] *= fc1;
    } else {
      var fc2 = this._gainTable[15 + (gainIndexNext - gainIndex)];
      for (var i = 0; i < this._gainSizeFactor; ++i) {
        buffer[bufOffset + i] *= fc1;
        fc1 *= fc2;
      }
    }
  }

  /// <summary>
  /// Full inverse MDCT (FFmpeg <c>AV_TX_FLOAT_MDCT</c> with <c>AV_TX_FULL_IMDCT</c>): N
  /// frequency coefficients → 2N time samples with scale 1/32768. Implemented as the
  /// reference composes it — a naive inverse MDCT producing N samples placed at offset N/2,
  /// then the half-mirror extension that fills the outer quarters — so the sign/scaling
  /// convention is identical. O(N²); acceptable for the 256/512/1024 lengths here.
  /// </summary>
  private void FullImdct(float[] input, float[] output) {
    var n = this._samplesPerChannel;       // s->len for the sub-transform
    const double scale = 1.0 / 32768.0;

    // Sub-transform: naive inverse MDCT of length n -> n samples (two halves).
    var half = new float[n];
    var len = n >> 1;
    var len2 = len * 2;                     // == n
    var phase = Math.PI / (4.0 * len2);
    for (var i = 0; i < len; ++i) {
      double sumD = 0.0, sumU = 0.0;
      var iD = phase * (4 * len - 2 * i - 1);
      var iU = phase * (3 * len2 + 2 * i + 1);
      for (var j = 0; j < len2; ++j) {
        var a = 2 * j + 1;
        sumD += Math.Cos(a * iD) * input[j];
        sumU += Math.Cos(a * iU) * input[j];
      }
      half[i] = (float)(sumD * scale);
      half[i + len] = (float)(-sumU * scale);
    }

    // inv_full wrapper: place half at offset len4 of the 2N output, then mirror.
    var full = 2 * n;
    var fHalf = full >> 1;                  // == n
    var fQuarter = full >> 2;               // == n/2
    Array.Clear(output, 0, full);
    for (var i = 0; i < n; ++i)
      output[fQuarter + i] = half[i];
    for (var i = 0; i < fQuarter; ++i) {
      output[i] = -output[fHalf - i - 1];
      output[full - i - 1] = output[fHalf + i];
    }
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  private static int ClipUintP2(int a, int p) {
    var max = (1 << p) - 1;
    if (a < 0) return 0;
    return a > max ? max : a;
  }

  private static int PopCount(uint v) {
    var c = 0;
    while (v != 0) { c += (int)(v & 1); v >>= 1; }
    return c;
  }
}
