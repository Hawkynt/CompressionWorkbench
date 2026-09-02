#pragma warning disable CS1591

namespace Codec.WmaLossless;

/// <summary>
/// Clean-room Windows Media Audio Lossless (WAVEFORMATEX tag <c>0x0163</c>) decoder, a
/// faithful port of FFmpeg's <c>libavcodec/wmalosslessdec.c</c> (LGPL 2.1) and the
/// integer scalar-product kernels from <c>libavcodec/lossless_audiodsp.c</c>. WMA
/// Lossless shares WMA Pro's ASF packet / bit-reservoir framing (sequence number,
/// frame-spanning <c>num_bits_prev_frame</c>, length-prefixed frames) but replaces the
/// MDCT path with an exactly-reversible integer residue pipeline: per-channel CDLMS
/// adaptive filters, optional MCLMS inter-channel prediction, an autocorrelation filter,
/// inter-channel decorrelation and a raw-PCM bypass mode. Output is interleaved
/// little-endian signed 16-bit PCM (24-bit input is decoded into 32-bit intermediates
/// and emitted as 16-bit by the surfacing layer).
/// <para>
/// <b>Reconstruction is bit-exact</b> for the supported modes; arithmetic coding and the
/// (reference-incomplete) inverse-LPC path are rejected, matching FFmpeg's
/// <c>avpriv_request_sample</c> behaviour so callers can fall back gracefully.
/// </para>
/// </summary>
public sealed class WmaLosslessCodec {

  private const int MaxChannels = 8;
  private const int MaxSubframes = 32;
  private const int MaxOrder = 256;
  private const int BlockMaxBits = 14;
  private const int BlockMaxSize = 1 << BlockMaxBits;
  private const int MaxFrameSizeBytes = (BlockMaxSize + (1 << (BlockMaxBits - 1))) * MaxChannels;

  private sealed class ChannelCtx {
    public int PrevBlockLen;
    public int DecodedSamples;
    public int CurSubframe;
    public int NumSubframes;
    public readonly int[] SubframeLen = new int[MaxSubframes];
    public readonly int[] SubframeOffsets = new int[MaxSubframes];
    public int TransientCounter;
  }

  private sealed class Cdlms {
    public int Order;
    public int Scaling;
    public int CoefSend;
    public int BitSend;
    public readonly short[] Coefs = new short[MaxOrder];
    // Backing store sized for the "duplicate the front" doubling trick.
    public readonly int[] LmsPrevValues = new int[MaxOrder * 2 + 16];
    public readonly short[] LmsUpdates = new short[MaxOrder * 2 + 16];
    public int Recent;
  }

  // ── stream parameters ─────────────────────────────────────────────────────
  private readonly uint _decodeFlags;
  private readonly int _bitsPerSample;
  private readonly int _numChannels;
  private readonly int _blockAlign;
  private readonly int _log2FrameSize;
  private readonly bool _lenPrefix;
  private readonly int _samplesPerFrame;
  private readonly int _maxNumSubframes;
  private readonly int _subframeLenBits;
  private readonly int _minSamplesPerSubframe;
  private readonly bool _dynamicRangeCompression;
  private readonly bool _bV3RTM;

  private readonly ChannelCtx[] _channel;

  // ── per-frame decode state ────────────────────────────────────────────────
  private readonly int[][] _channelResidues;
  private readonly bool[] _isChannelCoded = new bool[MaxChannels];
  private readonly bool[] _transient = new bool[MaxChannels];
  private readonly int[] _transientPos = new int[MaxChannels];
  private readonly long[] _aveSum = new long[MaxChannels];
  private readonly int[] _updateSpeed = new int[MaxChannels];
  private readonly int[] _channelIndexesForCurSubframe = new int[MaxChannels];
  private int _channelsForCurSubframe;
  private bool _parsedAllSubframes;
  private bool _seekableTile;
  private bool _doArithCoding;
  private bool _doAcFilter;
  private bool _doInterChDecorr;
  private bool _doMclms;
  private bool _doLpc;
  private int _movaveScaling;
  private int _quantStepsize;
  private int _frameOffset;
  private int _subframeOffset;

  // AC filter
  private int _acfilterOrder;
  private int _acfilterScaling;
  private readonly short[] _acfilterCoeffs = new short[16];
  private readonly int[][] _acfilterPrevValues;

  // MCLMS
  private int _mclmsOrder;
  private int _mclmsScaling;
  private readonly int[] _mclmsCoeffs = new int[MaxChannels * MaxChannels * 32];
  private readonly int[] _mclmsCoeffsCur = new int[MaxChannels * MaxChannels];
  private readonly int[] _mclmsPrevValues = new int[MaxChannels * 2 * 32];
  private readonly int[] _mclmsUpdates = new int[MaxChannels * 2 * 32];
  private int _mclmsRecent;

  // CDLMS
  private readonly Cdlms[][] _cdlms;
  private readonly int[] _cdlmsTtl = new int[MaxChannels];

  // ── bit reservoir machinery (mirrors save_bits / decode_packet) ───────────
  private readonly byte[] _frameData = new byte[MaxFrameSizeBytes];
  private int _frameDataBits;
  private int _numSavedBits;
  private WmallBitReader? _gb;
  private int _bufBitSize;
  private bool _packetDone = true;
  private bool _packetLoss;
  private bool _skipFrame = true;
  private int _packetSequenceNumber;
  private int _packetOffset;

  /// <summary>Decoded channel count taken from the extradata channel mask (falls back to nChannels).</summary>
  public int Channels => this._numChannels;

  /// <summary>Output bit depth declared by the extradata (16 or 24).</summary>
  public int BitsPerSample => this._bitsPerSample;

  /// <summary>Decode flags parsed from extradata bytes 14..15.</summary>
  public uint DecodeFlags => this._decodeFlags;

  /// <summary>Whether frames carry a length prefix (decode_flags &amp; 0x40).</summary>
  public bool UsesLengthPrefix => this._lenPrefix;

  /// <summary>Samples per frame derived from the sample rate and decode flags.</summary>
  public int SamplesPerFrame => this._samplesPerFrame;

  /// <summary>
  /// Exposes the integer scalar-product-and-MADD kernel
  /// (<c>scalarproduct_and_madd_int16/32</c>) for hand-walked verification. Computes the
  /// dot product of <paramref name="coefs"/> with <paramref name="prev"/> while adapting
  /// <paramref name="coefs"/> in place by <c>mul * updates</c>. Returns the dot product
  /// (taken before each coefficient's in-place update, as the reference does).
  /// </summary>
  internal static int TestScalarProductAndMadd(short[] coefs, int[] prev, short[] updates, int order, int mul)
    => ScalarProductAndMadd(coefs, prev, updates, 0, order, mul);

    /// <summary>
  /// Initializes a new instance of <see cref="WmaLosslessCodec"/>.
  /// </summary>
public WmaLosslessCodec(int channels, int sampleRate, int blockAlign, ReadOnlySpan<byte> extradata) {
    if (blockAlign is <= 0 or > (1 << 21))
      throw new ArgumentOutOfRangeException(nameof(blockAlign), "block_align is not set or invalid.");
    if (extradata.Length < 18)
      throw new ArgumentException("WMA Lossless requires >= 18 bytes of extradata.", nameof(extradata));

    this._decodeFlags = (uint)(extradata[14] | (extradata[15] << 8));
    var channelMask = (uint)(extradata[2] | (extradata[3] << 8) | (extradata[4] << 16) | (extradata[5] << 24));
    this._bitsPerSample = extradata[0] | (extradata[1] << 8);
    if (this._bitsPerSample is not (16 or 24))
      throw new ArgumentOutOfRangeException(nameof(extradata), $"WMA Lossless: unknown bit-depth {this._bitsPerSample}.");

    var nbChannels = channelMask != 0 ? PopCount(channelMask) : channels;
    if (nbChannels is < 1 or > MaxChannels)
      throw new ArgumentOutOfRangeException(nameof(channels), $"WMA Lossless supports 1..{MaxChannels} channels.");
    this._numChannels = nbChannels;
    this._blockAlign = blockAlign;

    this._log2FrameSize = Log2(blockAlign) + 4;
    this._lenPrefix = (this._decodeFlags & 0x40) != 0;

    var bits = WmaGetFrameLenBits(sampleRate, this._decodeFlags);
    this._samplesPerFrame = 1 << bits;
    if (this._samplesPerFrame > BlockMaxSize)
      throw new NotSupportedException("WMA Lossless: frame size too large.");

    var log2MaxNumSubframes = (int)((this._decodeFlags & 0x38) >> 3);
    this._maxNumSubframes = 1 << log2MaxNumSubframes;
    this._subframeLenBits = Log2(log2MaxNumSubframes) + 1;
    this._minSamplesPerSubframe = this._samplesPerFrame / this._maxNumSubframes;
    this._dynamicRangeCompression = (this._decodeFlags & 0x80) != 0;
    this._bV3RTM = (this._decodeFlags & 0x100) != 0;

    if (this._maxNumSubframes > MaxSubframes)
      throw new InvalidDataException("WMA Lossless: invalid number of subframes.");

    this._channel = new ChannelCtx[this._numChannels];
    for (var i = 0; i < this._numChannels; ++i)
      this._channel[i] = new ChannelCtx { PrevBlockLen = this._samplesPerFrame };

    this._channelResidues = new int[MaxChannels][];
    for (var i = 0; i < MaxChannels; ++i)
      this._channelResidues[i] = new int[BlockMaxSize];

    this._acfilterPrevValues = new int[MaxChannels][];
    for (var i = 0; i < MaxChannels; ++i)
      this._acfilterPrevValues[i] = new int[16];

    this._cdlms = new Cdlms[MaxChannels][];
    for (var c = 0; c < MaxChannels; ++c) {
      this._cdlms[c] = new Cdlms[9];
      for (var i = 0; i < 9; ++i)
        this._cdlms[c][i] = new Cdlms();
    }
  }

  // ── ff_wma_get_frame_len_bits (version 3 path, as wma_get_frame_len_bits(.., 3, ..)) ──
  private static int WmaGetFrameLenBits(int sampleRate, uint decodeFlags) {
    int bits;
    if (sampleRate <= 16000) bits = 9;
    else if (sampleRate <= 22050) bits = 10;
    else if (sampleRate <= 48000) bits = 11;
    else if (sampleRate <= 96000) bits = 12;
    else bits = 13;
    var tmp = decodeFlags & 0x6;
    if (tmp == 0x2) ++bits;
    else if (tmp == 0x4) --bits;
    else if (tmp == 0x6) bits -= 2;
    return bits;
  }

  // ── public entry: decode one ASF packet (block_align bytes) ───────────────

  /// <summary>
  /// Decodes one WMA Lossless ASF packet and returns interleaved signed-16-bit PCM for
  /// whatever frames completed in that packet. Decoder state (reservoir, filters, block
  /// lengths) carries across calls. Returns an empty array when the packet only fed the
  /// reservoir or only the (skipped) first frame decoded.
  /// </summary>
  public short[] DecodePacket(ReadOnlySpan<byte> packet) {
    var produced = new List<int[][]>();
    this.DecodePacketCore(packet, produced);
    return Interleave(produced, this._numChannels, this._bitsPerSample);
  }

  private void DecodePacketCore(ReadOnlySpan<byte> avpkt, List<int[][]> produced) {
    if (avpkt.Length < this._blockAlign) {
      this._packetLoss = true;
      return;
    }
    var packetBuf = avpkt[..this._blockAlign].ToArray();
    this._bufBitSize = this._blockAlign << 3;
    var pgb = new WmallBitReader(packetBuf, 0, this._bufBitSize);

    var entry = 0;
    while (true) {
      var firstEntry = this._packetDone || this._packetLoss;
      this.DecodePacketBody(pgb, firstEntry, produced);
      if (this._packetDone || this._packetLoss) break;
      if (++entry > MaxSubframes * 4) break;
    }
  }

  private void DecodePacketBody(WmallBitReader pgb, bool firstEntry, List<int[][]> produced) {
    if (firstEntry) {
      this._packetDone = false;

      var packetSequenceNumber = (int)pgb.GetBits(4);
      pgb.SkipBits(1);             // seekable_frame_in_packet
      pgb.SkipBits(1);             // spliced_packet (unsupported → ignored)

      var numBitsPrevFrame = (int)pgb.GetBits(this._log2FrameSize);

      if (!this._packetLoss &&
          ((this._packetSequenceNumber + 1) & 0xF) != packetSequenceNumber)
        this._packetLoss = true;
      this._packetSequenceNumber = packetSequenceNumber;

      if (numBitsPrevFrame > 0) {
        var remainingPacketBits = this._bufBitSize - pgb.BitsCount;
        if (numBitsPrevFrame >= remainingPacketBits) {
          numBitsPrevFrame = remainingPacketBits;
          this._packetDone = true;
        }
        this.SaveBits(pgb, numBitsPrevFrame, append: true);
        if (numBitsPrevFrame < remainingPacketBits && !this._packetLoss)
          this.DecodeFrame(produced);
      }

      if (this._packetLoss) {
        this._numSavedBits = 0;
        this._packetLoss = false;
        this._frameDataBits = 0;
      }
    } else {
      if (this._lenPrefix && RemainingBits(pgb) > this._log2FrameSize) {
        var frameSize = (int)pgb.ShowBits(this._log2FrameSize);
        if (frameSize != 0 && frameSize <= RemainingBits(pgb)) {
          this.SaveBits(pgb, frameSize, append: false);
          if (!this._packetLoss) this._packetDone = !this.DecodeFrame(produced);
        } else this._packetDone = true;
      } else if (!this._lenPrefix && this._numSavedBits > (this._gb?.BitsCount ?? 0)) {
        this._packetDone = !this.DecodeFrame(produced);
      } else {
        this._packetDone = true;
      }
    }

    if (RemainingBits(pgb) < 0) this._packetLoss = true;
    if (this._packetDone && !this._packetLoss && RemainingBits(pgb) > 0)
      this.SaveBits(pgb, RemainingBits(pgb), append: false);
    this._packetOffset = pgb.BitsCount & 7;
  }

  private int RemainingBits(WmallBitReader gb) => this._bufBitSize - gb.BitsCount;

  // ── save_bits + bit writer (ff_copy_bits / put_bits) ──────────────────────
  private void SaveBits(WmallBitReader gb, int len, bool append) {
    int buflen;
    if (!append) {
      this._frameOffset = gb.BitsCount & 7;
      this._numSavedBits = this._frameOffset;
      this._frameDataBits = this._frameOffset;
      buflen = (this._numSavedBits + len + 8) >> 3;
    } else {
      buflen = (this._frameDataBits + len + 8) >> 3;
    }

    if (len <= 0 || buflen > MaxFrameSizeBytes) {
      this._packetLoss = true;
      this._numSavedBits = 0;
      return;
    }

    this._numSavedBits += len;
    if (!append) {
      Array.Clear(this._frameData);
      this.CopyBitsFromReader(gb, this._numSavedBits);
    } else {
      var align = 8 - (gb.BitsCount & 7);
      if (align > len) align = len;
      this.PutBits(align, gb.GetBits(align));
      len -= align;
      this.CopyBitsAppend(gb, len);
    }
    gb.SkipBits(len);

    this._gb = new WmallBitReader(this._frameData, 0, this._numSavedBits);
    this._gb.SkipBits(this._frameOffset);
  }

  private void CopyBitsFromReader(WmallBitReader gb, int nbits) {
    var srcStartBit = gb.BitsCount & ~7;
    var savedCursor = gb.BitsCount;
    gb.BitsCount = srcStartBit;
    this._frameDataBits = 0;
    var full = nbits >> 3;
    for (var i = 0; i < full; ++i) this.PutBits(8, gb.GetBits(8));
    var rem = nbits & 7;
    if (rem != 0) this.PutBits(rem, gb.GetBits(rem));
    gb.BitsCount = savedCursor;
  }

  private void CopyBitsAppend(WmallBitReader gb, int len) {
    var full = len >> 3;
    for (var i = 0; i < full; ++i) this.PutBits(8, gb.GetBits(8));
    var rem = len & 7;
    if (rem != 0) this.PutBits(rem, gb.GetBits(rem));
  }

  private void PutBits(int n, uint value) {
    for (var i = n - 1; i >= 0; --i) {
      var bit = (value >> i) & 1;
      var pos = this._frameDataBits;
      var bytePos = pos >> 3;
      if (bytePos < this._frameData.Length) {
        var shift = 7 - (pos & 7);
        if (bit != 0) this._frameData[bytePos] |= (byte)(1 << shift);
        else this._frameData[bytePos] &= (byte)~(1 << shift);
      }
      ++this._frameDataBits;
    }
  }

  // ── decode_frame ──────────────────────────────────────────────────────────
  private bool DecodeFrame(List<int[][]> produced) {
    var gb = this._gb!;
    var nbSamples = this._samplesPerFrame;
    var len = 0;
    if (this._lenPrefix) len = (int)gb.GetBits(this._log2FrameSize);

    if (!this.DecodeTileHdr()) { this._packetLoss = true; return false; }

    if (this._dynamicRangeCompression) gb.SkipBits(8); // drc_gain

    if (gb.GetBit() != 0) {
      if (gb.GetBit() != 0) gb.SkipBits(Log2(this._samplesPerFrame * 2)); // start skip
      if (gb.GetBit() != 0) {
        var skip = (int)gb.GetBits(Log2(this._samplesPerFrame * 2));      // end skip
        nbSamples -= skip;
        if (nbSamples <= 0) { this._packetLoss = true; return false; }
      }
    }

    // Output buffers for this frame (per-channel int32 intermediates).
    var outBuf = new int[this._numChannels][];
    for (var c = 0; c < this._numChannels; ++c)
      outBuf[c] = new int[this._samplesPerFrame];
    var outPos = new int[this._numChannels];

    this._parsedAllSubframes = false;
    for (var i = 0; i < this._numChannels; ++i) {
      this._channel[i].DecodedSamples = 0;
      this._channel[i].CurSubframe = 0;
    }

    while (!this._parsedAllSubframes) {
      if (!this.DecodeSubframe(outBuf, outPos)) { this._packetLoss = true; return false; }
    }

    this._skipFrame = false;

    if (!this._skipFrame) {
      var frame = new int[this._numChannels][];
      for (var c = 0; c < this._numChannels; ++c) {
        frame[c] = new int[nbSamples];
        Array.Copy(outBuf[c], 0, frame[c], 0, Math.Min(nbSamples, outBuf[c].Length));
      }
      produced.Add(frame);
    }

    if (this._lenPrefix) {
      var consumed = (gb.BitsCount - this._frameOffset) + 2;
      if (len != consumed) { this._packetLoss = true; return false; }
      gb.SkipBits(len - (gb.BitsCount - this._frameOffset) - 1);
    }

    return gb.GetBit() != 0; // trailer bit
  }

  // ── decode_tilehdr ────────────────────────────────────────────────────────
  private bool DecodeTileHdr() {
    var gb = this._gb!;
    var numSamples = new int[MaxChannels];
    var containsSubframe = new bool[MaxChannels];
    var channelsForCur = this._numChannels;
    var fixedLayout = false;
    var minChannelLen = 0;

    for (var c = 0; c < this._numChannels; ++c) this._channel[c].NumSubframes = 0;

    if (this._maxNumSubframes == 1 || gb.GetBit() != 0) fixedLayout = true;

    do {
      var inUse = false;
      for (var c = 0; c < this._numChannels; ++c) {
        if (numSamples[c] == minChannelLen) {
          if (fixedLayout || channelsForCur == 1 ||
              minChannelLen == this._samplesPerFrame - this._minSamplesPerSubframe)
            containsSubframe[c] = true;
          else
            containsSubframe[c] = gb.GetBit() != 0;
          inUse |= containsSubframe[c];
        } else containsSubframe[c] = false;
      }

      if (!inUse) return false;

      var subframeLen = this.DecodeSubframeLength(minChannelLen);
      if (subframeLen <= 0) return false;

      minChannelLen += subframeLen;
      for (var c = 0; c < this._numChannels; ++c) {
        var chan = this._channel[c];
        if (containsSubframe[c]) {
          if (chan.NumSubframes >= MaxSubframes) return false;
          chan.SubframeLen[chan.NumSubframes] = subframeLen;
          numSamples[c] += subframeLen;
          ++chan.NumSubframes;
          if (numSamples[c] > this._samplesPerFrame) return false;
        } else if (numSamples[c] <= minChannelLen) {
          if (numSamples[c] < minChannelLen) {
            channelsForCur = 0;
            minChannelLen = numSamples[c];
          }
          ++channelsForCur;
        }
      }
    } while (minChannelLen < this._samplesPerFrame);

    for (var c = 0; c < this._numChannels; ++c) {
      var offset = 0;
      for (var i = 0; i < this._channel[c].NumSubframes; ++i) {
        this._channel[c].SubframeOffsets[i] = offset;
        offset += this._channel[c].SubframeLen[i];
      }
    }
    return true;
  }

  private int DecodeSubframeLength(int offset) {
    if (offset == this._samplesPerFrame - this._minSamplesPerSubframe)
      return this._minSamplesPerSubframe;
    var len = Log2(this._maxNumSubframes - 1) + 1;
    var frameLenRatio = (int)this._gb!.GetBits(len);
    var subframeLen = this._minSamplesPerSubframe * (frameLenRatio + 1);
    if (subframeLen < this._minSamplesPerSubframe || subframeLen > this._samplesPerFrame)
      return -1;
    return subframeLen;
  }

  // ── decode_subframe ───────────────────────────────────────────────────────
  private bool DecodeSubframe(int[][] outBuf, int[] outPos) {
    var gb = this._gb!;
    var offset = this._samplesPerFrame;
    var subframeLen = this._samplesPerFrame;
    var totalSamples = this._samplesPerFrame * this._numChannels;

    this._subframeOffset = gb.BitsCount;

    for (var i = 0; i < this._numChannels; ++i) {
      if (offset > this._channel[i].DecodedSamples) {
        offset = this._channel[i].DecodedSamples;
        subframeLen = this._channel[i].SubframeLen[this._channel[i].CurSubframe];
      }
    }

    this._channelsForCurSubframe = 0;
    for (var i = 0; i < this._numChannels; ++i) {
      var curSubframe = this._channel[i].CurSubframe;
      totalSamples -= this._channel[i].DecodedSamples;
      if (offset == this._channel[i].DecodedSamples &&
          subframeLen == this._channel[i].SubframeLen[curSubframe]) {
        totalSamples -= this._channel[i].SubframeLen[curSubframe];
        this._channel[i].DecodedSamples += this._channel[i].SubframeLen[curSubframe];
        this._channelIndexesForCurSubframe[this._channelsForCurSubframe] = i;
        ++this._channelsForCurSubframe;
      }
    }

    if (totalSamples == 0)
      this._parsedAllSubframes = true;

    this._seekableTile = gb.GetBit() != 0;
    if (this._seekableTile) {
      this.ClearCodecBuffers();

      this._doArithCoding = gb.GetBit() != 0;
      if (this._doArithCoding)
        throw new NotSupportedException("WMA Lossless: arithmetic coding is not supported.");
      this._doAcFilter = gb.GetBit() != 0;
      this._doInterChDecorr = gb.GetBit() != 0;
      this._doMclms = gb.GetBit() != 0;

      if (this._doAcFilter) this.DecodeAcFilter();
      if (this._doMclms) this.DecodeMclms();
      if (!this.DecodeCdlms()) return false;

      this._movaveScaling = (int)gb.GetBits(3);
      this._quantStepsize = (int)gb.GetBits(8) + 1;

      this.ResetCodec();
    }

    var rawpcmTile = gb.GetBit() != 0;
    if (!rawpcmTile && this._cdlms[0][0].Order == 0)
      return false; // waiting for a seekable tile

    for (var i = 0; i < this._numChannels; ++i) this._isChannelCoded[i] = true;

    if (!rawpcmTile) {
      for (var i = 0; i < this._numChannels; ++i)
        this._isChannelCoded[i] = gb.GetBit() != 0;
      if (this._bV3RTM) {
        this._doLpc = gb.GetBit() != 0;
        if (this._doLpc) {
          this.DecodeLpc();
          // The reference's inverse-LPC filter is incomplete; bail to fallback.
          throw new NotSupportedException("WMA Lossless: inverse LPC filter is not supported.");
        }
      } else this._doLpc = false;
    }

    if (gb.BitsLeft < 1) return false;

    int paddingZeroes;
    if (gb.GetBit() != 0) paddingZeroes = (int)gb.GetBits(5);
    else paddingZeroes = 0;

    if (rawpcmTile) {
      var bits = this._bitsPerSample - paddingZeroes;
      if (bits <= 0) return false;
      for (var i = 0; i < this._numChannels; ++i)
        for (var j = 0; j < subframeLen; ++j)
          this._channelResidues[i][j] = gb.GetSignedBitsLong(bits);
    } else {
      if (this._bitsPerSample < paddingZeroes) return false;
      for (var i = 0; i < this._numChannels; ++i) {
        if (this._isChannelCoded[i]) {
          if (!this.DecodeChannelResidues(i, subframeLen)) return false;
          if (this._seekableTile) this.UseHighUpdateSpeed(i);
          else this.UseNormalUpdateSpeed(i);
          if (this._bitsPerSample > 16) this.RevertCdlms32(i, 0, subframeLen);
          else this.RevertCdlms16(i, 0, subframeLen);
        } else {
          Array.Clear(this._channelResidues[i], 0, subframeLen);
        }
      }

      if (this._doMclms) this.RevertMclms(subframeLen);
      if (this._doInterChDecorr) this.RevertInterChDecorr(subframeLen);
      if (this._doAcFilter) this.RevertAcFilter(subframeLen);

      if (this._quantStepsize != 1)
        for (var i = 0; i < this._numChannels; ++i)
          for (var j = 0; j < subframeLen; ++j)
            this._channelResidues[i][j] = (int)((uint)this._channelResidues[i][j] * (uint)this._quantStepsize);
    }

    // Write to the per-channel output at the channel's running offset.
    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      var slen = this._channel[c].SubframeLen[this._channel[c].CurSubframe];
      for (var j = 0; j < slen; ++j) {
        int sample;
        if (this._bitsPerSample == 16)
          sample = (short)this._channelResidues[c][j] * (1 << paddingZeroes);
        else
          sample = (int)((uint)this._channelResidues[c][j] * (256u << paddingZeroes));
        if (outPos[c] < outBuf[c].Length)
          outBuf[c][outPos[c]] = sample;
        ++outPos[c];
      }
    }

    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      if (this._channel[c].CurSubframe >= this._channel[c].NumSubframes) return false;
      ++this._channel[c].CurSubframe;
    }
    return true;
  }

  // ── channel residues (Golomb-like) ────────────────────────────────────────
  private bool DecodeChannelResidues(int ch, int tileSize) {
    var gb = this._gb!;
    var i = 0;
    this._transient[ch] = gb.GetBit() != 0;
    if (this._transient[ch]) {
      this._transientPos[ch] = (int)gb.GetBits(Log2(tileSize));
      if (this._transientPos[ch] != 0) this._transient[ch] = false;
      this._channel[ch].TransientCounter =
        Math.Max(this._channel[ch].TransientCounter, this._samplesPerFrame / 2);
    } else if (this._channel[ch].TransientCounter != 0) {
      this._transient[ch] = true;
    }

    if (this._seekableTile) {
      var aveMean = (long)gb.GetBits(this._bitsPerSample);
      this._aveSum[ch] = aveMean << (this._movaveScaling + 1);
    }

    if (this._seekableTile) {
      this._channelResidues[ch][0] = this._doInterChDecorr
        ? gb.GetSignedBitsLong(this._bitsPerSample + 1)
        : gb.GetSignedBitsLong(this._bitsPerSample);
      i++;
    }

    for (; i < tileSize; ++i) {
      uint quo = 0;
      while (gb.GetBit() != 0) {
        ++quo;
        if (gb.BitsLeft <= 0) return false;
      }
      if (quo >= 32)
        quo += gb.GetBitsLong((int)gb.GetBits(5) + 1);

      var aveMean = (this._aveSum[ch] + (1L << this._movaveScaling)) >> (this._movaveScaling + 1);
      uint residue;
      if (aveMean <= 1) {
        residue = quo;
      } else {
        var remBits = CeilLog2((int)aveMean);
        var rem = gb.GetBitsLong(remBits);
        residue = (quo << remBits) + rem;
      }

      this._aveSum[ch] = residue + this._aveSum[ch] - (this._aveSum[ch] >> this._movaveScaling);

      var signed = (int)((residue >> 1) ^ (uint)-(int)(residue & 1));
      this._channelResidues[ch][i] = signed;
    }
    return true;
  }

  private void DecodeLpc() {
    var gb = this._gb!;
    var lpcOrder = (int)gb.GetBits(5) + 1;
    var lpcScaling = (int)gb.GetBits(4);
    var lpcIntbits = (int)gb.GetBits(3) + 1;
    var cbits = lpcScaling + lpcIntbits;
    for (var ch = 0; ch < this._numChannels; ++ch)
      for (var i = 0; i < lpcOrder; ++i)
        gb.GetSignedBits(cbits);
  }

  private void DecodeAcFilter() {
    var gb = this._gb!;
    this._acfilterOrder = (int)gb.GetBits(4) + 1;
    this._acfilterScaling = (int)gb.GetBits(4);
    for (var i = 0; i < this._acfilterOrder; ++i)
      this._acfilterCoeffs[i] = (short)(gb.GetBitsZ(this._acfilterScaling) + 1);
  }

  private void DecodeMclms() {
    var gb = this._gb!;
    this._mclmsOrder = ((int)gb.GetBits(4) + 1) * 2;
    this._mclmsScaling = (int)gb.GetBits(4);
    if (gb.GetBit() != 0) {
      var cbits = Log2(this._mclmsScaling + 1);
      if ((1 << cbits) < this._mclmsScaling + 1) ++cbits;
      var sendCoefBits = (int)gb.GetBitsZ(cbits) + 2;

      for (var i = 0; i < this._mclmsOrder * this._numChannels * this._numChannels; ++i)
        this._mclmsCoeffs[i] = (int)gb.GetBits(sendCoefBits);
      for (var i = 0; i < this._numChannels; ++i)
        for (var c = 0; c < i; ++c)
          this._mclmsCoeffsCur[i * this._numChannels + c] = (int)gb.GetBits(sendCoefBits);
    }
  }

  private bool DecodeCdlms() {
    var gb = this._gb!;
    var cdlmsSendCoef = gb.GetBit() != 0;
    for (var c = 0; c < this._numChannels; ++c) {
      this._cdlmsTtl[c] = (int)gb.GetBits(3) + 1;
      for (var i = 0; i < this._cdlmsTtl[c]; ++i) {
        this._cdlms[c][i].Order = ((int)gb.GetBits(7) + 1) * 8;
        if (this._cdlms[c][i].Order > MaxOrder) {
          this._cdlms[0][0].Order = 0;
          return false;
        }
        // (order & 8) with 16-bit samples is only a request-sample warning upstream;
        // decoding proceeds.
      }
      for (var i = 0; i < this._cdlmsTtl[c]; ++i)
        this._cdlms[c][i].Scaling = (int)gb.GetBits(4);

      if (cdlmsSendCoef) {
        for (var i = 0; i < this._cdlmsTtl[c]; ++i) {
          var cbits = Log2(this._cdlms[c][i].Order);
          if ((1 << cbits) < this._cdlms[c][i].Order) ++cbits;
          this._cdlms[c][i].CoefSend = (int)gb.GetBits(cbits) + 1;

          cbits = Log2(this._cdlms[c][i].Scaling + 1);
          if ((1 << cbits) < this._cdlms[c][i].Scaling + 1) ++cbits;
          this._cdlms[c][i].BitSend = (int)gb.GetBitsZ(cbits) + 2;
          var shiftL = 32 - this._cdlms[c][i].BitSend;
          var shiftR = 32 - this._cdlms[c][i].Scaling - 2;
          for (var j = 0; j < this._cdlms[c][i].CoefSend; ++j)
            this._cdlms[c][i].Coefs[j] =
              (short)(((int)gb.GetBits(this._cdlms[c][i].BitSend) << shiftL) >> shiftR);
        }
      }

      for (var i = 0; i < this._cdlmsTtl[c]; ++i)
        Array.Clear(this._cdlms[c][i].Coefs, this._cdlms[c][i].Order,
          this._cdlms[c][i].Coefs.Length - this._cdlms[c][i].Order);
    }
    return true;
  }

  // ── filter state reset ────────────────────────────────────────────────────
  private void ClearCodecBuffers() {
    Array.Clear(this._acfilterCoeffs);
    for (var c = 0; c < MaxChannels; ++c) Array.Clear(this._acfilterPrevValues[c]);
    Array.Clear(this._mclmsCoeffs);
    Array.Clear(this._mclmsCoeffsCur);
    Array.Clear(this._mclmsPrevValues);
    Array.Clear(this._mclmsUpdates);
    for (var ich = 0; ich < this._numChannels; ++ich) {
      for (var ilms = 0; ilms < this._cdlmsTtl[ich]; ++ilms) {
        Array.Clear(this._cdlms[ich][ilms].Coefs);
        Array.Clear(this._cdlms[ich][ilms].LmsPrevValues);
        Array.Clear(this._cdlms[ich][ilms].LmsUpdates);
      }
      this._aveSum[ich] = 0;
    }
  }

  private void ResetCodec() {
    this._mclmsRecent = this._mclmsOrder * this._numChannels;
    for (var ich = 0; ich < this._numChannels; ++ich) {
      for (var ilms = 0; ilms < this._cdlmsTtl[ich]; ++ilms)
        this._cdlms[ich][ilms].Recent = this._cdlms[ich][ilms].Order;
      this._channel[ich].TransientCounter = this._samplesPerFrame;
      this._transient[ich] = true;
      this._transientPos[ich] = 0;
    }
  }

  // ── MCLMS ─────────────────────────────────────────────────────────────────
  private void MclmsPredict(int icoef, int[] pred) {
    var order = this._mclmsOrder;
    var nc = this._numChannels;
    for (var ich = 0; ich < nc; ++ich) {
      pred[ich] = 0;
      if (!this._isChannelCoded[ich]) continue;
      for (var i = 0; i < order * nc; ++i)
        pred[ich] += (int)((uint)this._mclmsPrevValues[i + this._mclmsRecent] *
                            (uint)this._mclmsCoeffs[i + order * nc * ich]);
      for (var i = 0; i < ich; ++i)
        pred[ich] += (int)((uint)this._channelResidues[i][icoef] *
                            (uint)this._mclmsCoeffsCur[i + nc * ich]);
      pred[ich] += (int)((1u << this._mclmsScaling) >> 1);
      pred[ich] >>= this._mclmsScaling;
      this._channelResidues[ich][icoef] += pred[ich];
    }
  }

  private void MclmsUpdate(int icoef, int[] pred) {
    var order = this._mclmsOrder;
    var nc = this._numChannels;
    var range = 1 << (this._bitsPerSample - 1);

    for (var ich = 0; ich < nc; ++ich) {
      var predError = this._channelResidues[ich][icoef] - pred[ich];
      if (predError > 0) {
        for (var i = 0; i < order * nc; ++i)
          this._mclmsCoeffs[i + ich * order * nc] += this._mclmsUpdates[this._mclmsRecent + i];
        for (var j = 0; j < ich; ++j)
          this._mclmsCoeffsCur[ich * nc + j] += Sign(this._channelResidues[j][icoef]);
      } else if (predError < 0) {
        for (var i = 0; i < order * nc; ++i)
          this._mclmsCoeffs[i + ich * order * nc] -= this._mclmsUpdates[this._mclmsRecent + i];
        for (var j = 0; j < ich; ++j)
          this._mclmsCoeffsCur[ich * nc + j] -= Sign(this._channelResidues[j][icoef]);
      }
    }

    for (var ich = nc - 1; ich >= 0; --ich) {
      --this._mclmsRecent;
      this._mclmsPrevValues[this._mclmsRecent] = Clip(this._channelResidues[ich][icoef], -range, range - 1);
      this._mclmsUpdates[this._mclmsRecent] = Sign(this._channelResidues[ich][icoef]);
    }

    if (this._mclmsRecent == 0) {
      Array.Copy(this._mclmsPrevValues, 0, this._mclmsPrevValues, order * nc, order * nc);
      Array.Copy(this._mclmsUpdates, 0, this._mclmsUpdates, order * nc, order * nc);
      this._mclmsRecent = nc * order;
    }
  }

  private void RevertMclms(int tileSize) {
    var pred = new int[MaxChannels];
    for (var icoef = 0; icoef < tileSize; ++icoef) {
      this.MclmsPredict(icoef, pred);
      this.MclmsUpdate(icoef, pred);
    }
  }

  // ── CDLMS ─────────────────────────────────────────────────────────────────
  private void UseHighUpdateSpeed(int ich) {
    for (var ilms = this._cdlmsTtl[ich] - 1; ilms >= 0; --ilms) {
      var recent = this._cdlms[ich][ilms].Recent;
      if (this._updateSpeed[ich] == 16) continue;
      if (this._bV3RTM)
        for (var icoef = 0; icoef < this._cdlms[ich][ilms].Order; ++icoef)
          this._cdlms[ich][ilms].LmsUpdates[icoef + recent] *= 2;
      else
        for (var icoef = 0; icoef < this._cdlms[ich][ilms].Order; ++icoef)
          this._cdlms[ich][ilms].LmsUpdates[icoef] *= 2;
    }
    this._updateSpeed[ich] = 16;
  }

  private void UseNormalUpdateSpeed(int ich) {
    for (var ilms = this._cdlmsTtl[ich] - 1; ilms >= 0; --ilms) {
      var recent = this._cdlms[ich][ilms].Recent;
      if (this._updateSpeed[ich] == 8) continue;
      if (this._bV3RTM)
        for (var icoef = 0; icoef < this._cdlms[ich][ilms].Order; ++icoef)
          this._cdlms[ich][ilms].LmsUpdates[icoef + recent] /= 2;
      else
        for (var icoef = 0; icoef < this._cdlms[ich][ilms].Order; ++icoef)
          this._cdlms[ich][ilms].LmsUpdates[icoef] /= 2;
    }
    this._updateSpeed[ich] = 8;
  }

  // lms_update + revert_cdlms, both bit-depth variants. The 16/32 distinction only
  // affects the prevvalues element width (which here is always int) and the order
  // padding used by the scalar-product kernel; the arithmetic is identical.
  private void LmsUpdate(int ich, int ilms, int input) {
    var lms = this._cdlms[ich][ilms];
    var range = 1 << (this._bitsPerSample - 1);
    var order = lms.Order;
    var recent = lms.Recent;

    if (recent != 0) {
      --recent;
    } else {
      Array.Copy(lms.LmsPrevValues, 0, lms.LmsPrevValues, order, order);
      Array.Copy(lms.LmsUpdates, 0, lms.LmsUpdates, order, order);
      recent = order - 1;
    }

    lms.LmsPrevValues[recent] = Clip(input, -range, range - 1);
    lms.LmsUpdates[recent] = (short)(Sign(input) * this._updateSpeed[ich]);

    lms.LmsUpdates[recent + (order >> 4)] >>= 2;
    lms.LmsUpdates[recent + (order >> 3)] >>= 1;
    lms.Recent = recent;
    Array.Clear(lms.LmsUpdates, recent + order, lms.LmsUpdates.Length - (recent + order));
  }

  private void RevertCdlms(int ch, int coefBegin, int coefEnd, int round) {
    var numLms = this._cdlmsTtl[ch];
    for (var ilms = numLms - 1; ilms >= 0; --ilms) {
      var lms = this._cdlms[ch][ilms];
      for (var icoef = coefBegin; icoef < coefEnd; ++icoef) {
        var residue = this._channelResidues[ch][icoef];
        long pred = (1 << lms.Scaling) >> 1;
        pred += ScalarProductAndMadd(lms.Coefs, lms.LmsPrevValues, lms.LmsUpdates,
          lms.Recent, FfAlign(lms.Order, round), Sign(residue));
        var input = residue + (int)((int)pred >> lms.Scaling);
        this.LmsUpdate(ch, ilms, input);
        this._channelResidues[ch][icoef] = input;
      }
    }
  }

  private void RevertCdlms16(int ch, int begin, int end) => this.RevertCdlms(ch, begin, end, 16);
  private void RevertCdlms32(int ch, int begin, int end) => this.RevertCdlms(ch, begin, end, 8);

  // scalarproduct_and_madd: res += coefs[i]*prev[recent+i]; coefs[i] += mul*upd[recent+i].
  private static int ScalarProductAndMadd(short[] v1, int[] v2, short[] v3, int v2v3Offset, int order, int mul) {
    var res = 0;
    for (var i = 0; i < order; ++i) {
      res += v1[i] * v2[v2v3Offset + i];
      v1[i] = (short)(v1[i] + mul * v3[v2v3Offset + i]);
    }
    return res;
  }

  private void RevertInterChDecorr(int tileSize) {
    if (this._numChannels != 2) return;
    if (!this._isChannelCoded[0] && !this._isChannelCoded[1]) return;
    for (var icoef = 0; icoef < tileSize; ++icoef) {
      this._channelResidues[0][icoef] -= this._channelResidues[1][icoef] >> 1;
      this._channelResidues[1][icoef] += this._channelResidues[0][icoef];
    }
  }

  private void RevertAcFilter(int tileSize) {
    var order = this._acfilterOrder;
    var scaling = this._acfilterScaling;
    for (var ich = 0; ich < this._numChannels; ++ich) {
      var prev = this._acfilterPrevValues[ich];
      for (var i = 0; i < order; ++i) {
        var pred = 0;
        for (var j = 0; j < order; ++j) {
          if (i <= j)
            pred += (int)((uint)this._acfilterCoeffs[j] * (uint)prev[j - i]);
          else
            pred += (int)((uint)this._channelResidues[ich][i - j - 1] * (uint)this._acfilterCoeffs[j]);
        }
        pred >>= scaling;
        this._channelResidues[ich][i] += pred;
      }
      for (var i = order; i < tileSize; ++i) {
        var pred = 0;
        for (var j = 0; j < order; ++j)
          pred += (int)((uint)this._channelResidues[ich][i - j - 1] * (uint)this._acfilterCoeffs[j]);
        pred >>= scaling;
        this._channelResidues[ich][i] += pred;
      }
      for (var j = order - 1; j >= 0; --j)
        prev[j] = tileSize <= j ? prev[j - tileSize] : this._channelResidues[ich][tileSize - j - 1];
    }
  }

  // ── interleave to int16 LE PCM ────────────────────────────────────────────
  private static short[] Interleave(List<int[][]> frames, int channels, int bitsPerSample) {
    var total = 0;
    foreach (var f in frames) total += f[0].Length;
    var pcm = new short[total * channels];
    var idx = 0;
    foreach (var f in frames) {
      var n = f[0].Length;
      for (var s = 0; s < n; ++s)
        for (var c = 0; c < channels; ++c) {
          var v = f[c][s];
          if (bitsPerSample > 16) v >>= 8; // narrow 24-bit (stored << 8) down to 16-bit
          pcm[idx++] = ClipShort(v);
        }
    }
    return pcm;
  }

  // ── small helpers ─────────────────────────────────────────────────────────
  private static int Log2(int v) => v <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);
  private static int CeilLog2(int v) => v <= 1 ? 0 : Log2(v - 1) + 1;
  private static int FfAlign(int x, int a) => (x + a - 1) & ~(a - 1);
  private static int Sign(int x) => (x > 0 ? 1 : 0) - (x < 0 ? 1 : 0);
  private static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
  private static short ClipShort(int v) => v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;
  private static int PopCount(uint v) => System.Numerics.BitOperations.PopCount(v);
}
