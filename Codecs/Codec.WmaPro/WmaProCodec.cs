#pragma warning disable CS1591

namespace Codec.WmaPro;

/// <summary>
/// Microsoft Windows Media Audio 9 Professional decoder (WAVEFORMATEX tag <c>0x0162</c>).
/// A faithful port of FFmpeg's <c>libavcodec/wmaprodec.c</c> (the WMAPRO code paths only;
/// the XMA1/XMA2 variants are not handled) plus the shared helpers from
/// <c>libavcodec/wma.c</c> / <c>wma_common.c</c>; the large Huffman / decorrelation
/// tables live in <see cref="WmaProTables"/> and were transcribed from
/// <c>libavcodec/wmaprodata.h</c>.
/// <para>
/// WMA Pro is an MDCT codec comparable to AAC: the compressed bitstream is split into
/// packets that each carry one or more variable-length frames (frames may cross packet
/// boundaries via a bit reservoir). Every frame holds a fixed number of samples per
/// channel and is split into a variable number of subframes (2^N time-domain samples,
/// N in 6..13). Subframes of different channels with matching offset/length can be
/// grouped and share channel transforms (M/S for two channels; generalized rotation /
/// default decorrelation matrices for more). Coefficients are vector-Huffman coded
/// (4/2/1 elements per symbol with escapes) with a run-level fallback, then rescaled by
/// per-band scale factors and a per-channel quantization step, decorrelated, IMDCT'd and
/// overlap-added with a sine window.
/// </para>
/// <para>
/// Features covered: 1–8 channels (the generic decorrelation path; LFE handled as a
/// plain channel with its subwoofer cutoff applied), 16/24-bit source depth (the IMDCT
/// scale tracks <c>bits_per_sample</c>; output is signed 16-bit PCM), variable block
/// lengths / tile configuration, the bit reservoir and cross-packet frame fetch, and
/// VBR streams (no constant-bitrate assumption is made — framing is driven entirely by
/// the length-prefix / reservoir fields). Decode-only.
/// </para>
/// </summary>
public sealed class WmaProCodec {

  private const int MaxChannels = 8;
  private const int MaxSubframes = 32;
  private const int MaxBands = 29;
  private const int MaxFrameSize = 32768;
  private const int BlockMinBits = 6;
  private const int BlockMaxBits = 13;
  private const int BlockMinSize = 1 << BlockMinBits;
  private const int BlockMaxSize = 1 << BlockMaxBits;
  private const int BlockSizes = BlockMaxBits - BlockMinBits + 1;

  // ── immutable config (decode_init) ────────────────────────────────────────────
  private readonly int _channels;
  private readonly int _sampleRate;
  private readonly int _blockAlign;
  private readonly int _bitsPerSample;
  private readonly uint _decodeFlags;
  private readonly bool _lenPrefix;
  private readonly bool _dynamicRangeCompression;
  private readonly int _samplesPerFrame;
  private readonly int _log2FrameSize;
  private readonly int _maxNumSubframes;
  private readonly bool _maxSubframeLenBit;
  private readonly int _subframeLenBits;
  private readonly int _minSamplesPerSubframe;
  private readonly int _lfeChannel;

  private readonly int[] _numSfb = new int[BlockSizes];
  private readonly int[][] _sfbOffsets = NewJaggedInt(BlockSizes, MaxBands);
  private readonly int[][][] _sfOffsets = NewJaggedInt3(BlockSizes, BlockSizes, MaxBands);
  private readonly int[] _subwooferCutoffs = new int[BlockSizes];

  private readonly float[] _sin64 = new float[33];

  // VLC tables (built once)
  private readonly WmaProVlc _sfVlc;
  private readonly WmaProVlc _sfRlVlc;
  private readonly WmaProVlc[] _coefVlc = new WmaProVlc[2];
  private readonly WmaProVlc _vec4Vlc;
  private readonly WmaProVlc _vec2Vlc;
  private readonly WmaProVlc _vec1Vlc;

  // MDCT per block size
  private readonly WmaProMdct[] _mdct = new WmaProMdct[BlockSizes];
  private readonly float[][] _windows = new float[BlockSizes][];

  // ── per-channel context (WMAProChannelCtx) ────────────────────────────────────
  private sealed class ChannelCtx {
    public int PrevBlockLen;
    public bool TransmitCoefs;
    public int NumSubframes;
    public readonly int[] SubframeLen = new int[MaxSubframes];
    public readonly int[] SubframeOffset = new int[MaxSubframes];
    public int CurSubframe;
    public int DecodedSamples;
    public bool Grouped;
    public int QuantStep;
    public bool ReuseSf;
    public int ScaleFactorStep;
    public int MaxScaleFactor;
    public readonly int[][] SavedScaleFactors = [new int[MaxBands], new int[MaxBands]];
    public int ScaleFactorIdx;
    public int[] ScaleFactors = null!; // points at one of SavedScaleFactors
    public int TableIdx;
    public int CoeffsOffset; // offset of the current subframe in Out
    public int NumVecCoeffs;
    public readonly float[] Out = new float[BlockMaxSize + BlockMaxSize / 2];
  }

  // ── channel group (WMAProChannelGrp) ──────────────────────────────────────────
  private sealed class ChannelGrp {
    public int NumChannels;
    public bool Transform;
    public readonly bool[] TransformBand = new bool[MaxBands];
    public readonly float[] DecorrelationMatrix = new float[MaxChannels * MaxChannels];
    public readonly int[] ChannelData = new int[MaxChannels]; // channel indexes in the group
  }

  private readonly ChannelCtx[] _channel;
  private readonly ChannelGrp[] _chgroup = new ChannelGrp[MaxChannels];

  // ── bit-reservoir / packet decode state (mirrors save_bits + decode_packet) ───
  private readonly byte[] _frameData = new byte[MaxFrameSize + 8];
  private int _frameDataBits;   // number of valid bits in _frameData (put_bits_count)
  private int _numSavedBits;     // s->num_saved_bits
  private int _frameOffset;      // s->frame_offset (bit alignment within _frameData)
  private int _packetOffset;     // s->packet_offset
  private bool _packetDone = true;
  private bool _packetLoss = true;
  private int _packetSequenceNumber;
  private bool _skipFrame = true; // skip first decoded frame, as the reference does

  // ── frame / subframe decode state ─────────────────────────────────────────────
  private WmaProBitReader _gb = null!;       // s->gb (over _frameData)
  private int _bufBitSize;
  private bool _parsedAllSubframes;
  private int _subframeLen;
  private int _channelsForCurSubframe;
  private readonly int[] _channelIndexesForCurSubframe = new int[MaxChannels];
  private int _numBands;
  private bool _transmitNumVecCoeffs;
  private int[] _curSfbOffsets = null!;
  private int _tableIdx;
  private int _escLen;
  private int _numChgroups;
  private readonly float[] _tmp = new float[BlockMaxSize]; // IMDCT input scratch

  /// <summary>Number of output samples per decoded frame.</summary>
  public int SamplesPerFrame => this._samplesPerFrame;

  /// <summary>Channel count carried by the stream.</summary>
  public int Channels => this._channels;

  /// <summary>Sample rate in Hz.</summary>
  public int SampleRate => this._sampleRate;

  /// <summary>Integer sample bit depth declared in the extradata (1..32).</summary>
  public int BitsPerSample => this._bitsPerSample;

  /// <summary>log2 of the compressed frame-size field width.</summary>
  public int Log2FrameSize => this._log2FrameSize;

  /// <summary>Maximum number of subframes a channel may be split into for this stream.</summary>
  public int MaxNumSubframes => this._maxNumSubframes;

  /// <summary>True when each frame is prefixed with its compressed length (decode_flags bit 0x40).</summary>
  public bool UsesLengthPrefix => this._lenPrefix;

  /// <summary>True when frames carry dynamic range compression data (decode_flags bit 0x80).</summary>
  public bool UsesDynamicRangeCompression => this._dynamicRangeCompression;

  /// <summary>Number of scale factor bands for the given table index (0 = full-length subframe).</summary>
  public int NumScaleFactorBands(int tableIdx) => this._numSfb[tableIdx];

  /// <summary>The LFE channel index, or -1 when the stream has no LFE.</summary>
  public int LfeChannel => this._lfeChannel;

  /// <summary>The raw 32-bit decode-flags word read from the extradata.</summary>
  public uint DecodeFlags => this._decodeFlags;

  /// <summary>Scale factor band offsets for the given table index (test/diagnostic access).</summary>
  internal int[] SfbOffsets(int tableIdx) => this._sfbOffsets[tableIdx];

  /// <summary>Subwoofer cutoff for the given table index (test/diagnostic access).</summary>
  internal int SubwooferCutoff(int tableIdx) => this._subwooferCutoffs[tableIdx];

  /// <summary>Builds the test reservoir reader over <paramref name="data"/> (MSB-first).</summary>
  internal void TestInitReader(byte[] data) {
    this._numSavedBits = data.Length * 8;
    this._frameOffset = 0;
    this._gb = new WmaProBitReader(data, 0, this._numSavedBits);
  }

  /// <summary>Runs <c>decode_tilehdr</c> against the test reader; returns each channel's subframe lengths.</summary>
  internal int[][] TestDecodeTileHdr() {
    if (!this.DecodeTileHdr()) throw new InvalidDataException("tile header decode failed");
    var result = new int[this._channels][];
    for (var c = 0; c < this._channels; ++c) {
      result[c] = new int[this._channel[c].NumSubframes];
      Array.Copy(this._channel[c].SubframeLen, result[c], this._channel[c].NumSubframes);
    }
    return result;
  }

  /// <summary>
  /// Runs <c>decode_channel_transform</c> against the test reader after seeding the
  /// "current subframe" channel set with all channels at the full subframe length. Returns
  /// the decoded channel groups as (channelIndexes, transform-enabled, 2x2 matrix or null).
  /// </summary>
  internal (int[] Channels, bool Transform)[] TestDecodeChannelTransform() {
    this._tableIdx = 0;
    this._numBands = this._numSfb[0];
    this._subframeLen = this._samplesPerFrame;
    this._channelsForCurSubframe = this._channels;
    for (var c = 0; c < this._channels; ++c) {
      this._channelIndexesForCurSubframe[c] = c;
      this._channel[c].Grouped = false;
    }
    if (!this.DecodeChannelTransform()) throw new InvalidDataException("channel transform decode failed");
    var groups = new (int[], bool)[this._numChgroups];
    for (var g = 0; g < this._numChgroups; ++g) {
      var grp = this._chgroup[g];
      var chs = new int[grp.NumChannels];
      Array.Copy(grp.ChannelData, chs, grp.NumChannels);
      groups[g] = (chs, grp.Transform);
    }
    return groups;
  }

  /// <summary>
  /// Hand-walks <c>decode_scale_factors</c> for a single channel: seeds the channel as the
  /// current subframe at table index 0, runs the (non-reuse) DPCM path, and returns the
  /// decoded scale factor values.
  /// </summary>
  internal int[] TestDecodeScaleFactorsSingle() {
    this._tableIdx = 0;
    this._numBands = this._numSfb[0];
    this._subframeLen = this._samplesPerFrame;
    this._channelsForCurSubframe = 1;
    this._channelIndexesForCurSubframe[0] = 0;
    this._channel[0].CurSubframe = 0;
    this._channel[0].ReuseSf = false;
    if (!this.DecodeScaleFactors()) throw new InvalidDataException("scale factor decode failed");
    var sf = new int[this._numBands];
    Array.Copy(this._channel[0].ScaleFactors, sf, this._numBands);
    return sf;
  }

  /// <summary>Decodes one symbol from the scale-factor DPCM VLC against the test reader.</summary>
  internal int TestDecodeScaleFactorVlc(out bool ok) => this._sfVlc.Decode(this._gb, out ok);

  /// <summary>
  /// Hand-walks <c>decode_coeffs</c> for channel 0 at a given subframe length / vec-coeff
  /// count and returns the produced coefficient buffer (subframe_len floats).
  /// </summary>
  internal float[] TestDecodeCoeffs(int subframeLen, int numVecCoeffs, bool transmitNumVec) {
    this._subframeLen = subframeLen;
    this._escLen = Log2(subframeLen - 1) + 1;
    this._transmitNumVecCoeffs = transmitNumVec;
    this._channel[0].CoeffsOffset = 0;
    this._channel[0].NumVecCoeffs = numVecCoeffs;
    if (!this.DecodeCoeffs(0)) throw new InvalidDataException("coeff decode failed");
    var outc = new float[subframeLen];
    Array.Copy(this._channel[0].Out, 0, outc, 0, subframeLen);
    return outc;
  }

  /// <summary>
  /// Constructs a decoder from the stream parameters. <paramref name="extradata"/> is the
  /// WAVEFORMATEX codec-private tail; WMA Pro requires at least 18 bytes (bits-per-sample
  /// at +0, channel mask at +2, decode flags at +14, all little-endian).
  /// </summary>
  public WmaProCodec(int channels, int sampleRate, int bitsPerSample, int blockAlign,
                     long avgBytesPerSec, ReadOnlySpan<byte> extradata) {
    _ = avgBytesPerSec; // WMA Pro framing is length-prefix / reservoir driven, not CBR.
    if (blockAlign <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockAlign), "block_align must be set.");
    if (extradata.Length < 18)
      throw new ArgumentException("WMA Pro requires >= 18 bytes of extradata.", nameof(extradata));

    this._decodeFlags = (uint)(extradata[14] | (extradata[15] << 8));
    var channelMask = (uint)(extradata[2] | (extradata[3] << 8) | (extradata[4] << 16) | (extradata[5] << 24));
    this._bitsPerSample = extradata[0] | (extradata[1] << 8);
    if (this._bitsPerSample is < 1 or > 32)
      throw new ArgumentOutOfRangeException(nameof(extradata), "WMA Pro bits-per-sample must be in 1..32.");

    var nbChannels = channelMask != 0 ? PopCount(channelMask) : channels;
    if (nbChannels is < 1 or > MaxChannels)
      throw new ArgumentOutOfRangeException(nameof(channels), $"WMA Pro supports 1..{MaxChannels} channels.");
    this._channels = nbChannels;
    this._sampleRate = sampleRate;
    this._blockAlign = blockAlign;

    // generic init
    this._log2FrameSize = Log2(blockAlign) + 4;
    if (this._log2FrameSize > 25)
      throw new ArgumentOutOfRangeException(nameof(blockAlign), "WMA Pro large block_align unsupported.");

    this._lenPrefix = (this._decodeFlags & 0x40) != 0;

    var bits = WmaGetFrameLenBits(sampleRate, this._decodeFlags);
    if (bits > BlockMaxBits)
      throw new NotSupportedException("WMA Pro 14-bit block sizes unsupported.");
    this._samplesPerFrame = 1 << bits;

    var log2MaxNumSubframes = (int)((this._decodeFlags & 0x38) >> 3);
    this._maxNumSubframes = 1 << log2MaxNumSubframes;
    if (this._maxNumSubframes is 16 or 4) this._maxSubframeLenBit = true;
    this._subframeLenBits = Log2(log2MaxNumSubframes) + 1;

    var numPossibleBlockSizes = log2MaxNumSubframes + 1;
    this._minSamplesPerSubframe = this._samplesPerFrame / this._maxNumSubframes;
    this._dynamicRangeCompression = (this._decodeFlags & 0x80) != 0;

    if (this._maxNumSubframes > MaxSubframes)
      throw new InvalidDataException("WMA Pro invalid number of subframes.");
    if (this._minSamplesPerSubframe < BlockMinSize)
      throw new InvalidDataException("WMA Pro min_samples_per_subframe too small.");

    this._channel = new ChannelCtx[this._channels];
    for (var i = 0; i < this._channels; ++i) {
      this._channel[i] = new ChannelCtx { PrevBlockLen = this._samplesPerFrame };
    }
    for (var i = 0; i < MaxChannels; ++i) this._chgroup[i] = new ChannelGrp();

    // LFE channel position from the channel mask (bit 3 == low frequency).
    this._lfeChannel = -1;
    if ((channelMask & 8) != 0) {
      for (uint mask = 1; mask < 16; mask <<= 1)
        if ((channelMask & mask) != 0) ++this._lfeChannel;
    }

    // scale factor band counts and offsets per possible block size
    for (var i = 0; i < numPossibleBlockSizes; ++i) {
      var subframeLen = this._samplesPerFrame >> i;
      var band = 1;
      var rate = sampleRate;
      this._sfbOffsets[i][0] = 0;
      for (var x = 0; x < MaxBands - 1 && this._sfbOffsets[i][band - 1] < subframeLen; ++x) {
        var offset = (subframeLen * 2 * WmaProTables.CriticalFreqs[x]) / rate + 2;
        offset &= ~3;
        if (offset > this._sfbOffsets[i][band - 1]) this._sfbOffsets[i][band++] = offset;
        if (offset >= subframeLen) break;
      }
      this._sfbOffsets[i][band - 1] = subframeLen;
      this._numSfb[i] = band - 1;
      if (this._numSfb[i] <= 0) throw new InvalidDataException("WMA Pro num_sfb invalid.");
    }

    // scale factor resample matrix
    for (var i = 0; i < numPossibleBlockSizes; ++i) {
      for (var b = 0; b < this._numSfb[i]; ++b) {
        var offset = ((this._sfbOffsets[i][b] + this._sfbOffsets[i][b + 1] - 1) << i) >> 1;
        for (var x = 0; x < numPossibleBlockSizes; ++x) {
          var v = 0;
          while ((this._sfbOffsets[x][v + 1] << x) < offset) {
            ++v;
            if (v >= MaxBands) throw new InvalidDataException("WMA Pro sf_offsets overflow.");
          }
          this._sfOffsets[i][x][b] = v;
        }
      }
    }

    // MDCT + sine windows per block size
    for (var i = 0; i < BlockSizes; ++i) {
      var n = 1 << (BlockMinBits + i);
      var scale = (float)(1.0 / (1 << (BlockMinBits + i - 1)) / (1L << (this._bitsPerSample - 1)));
      this._mdct[i] = new WmaProMdct(n, scale);
    }
    // windows[BlockSizes-1-i] = sine window of size 2^(BlockMaxBits-i); i.e. windows[k]
    // is the sine window matching block size 2^(BlockMinBits+k).
    for (var i = 0; i < BlockSizes; ++i)
      this._windows[i] = SineWindow(1 << (BlockMinBits + i));

    // subwoofer cutoffs
    for (var i = 0; i < numPossibleBlockSizes; ++i) {
      var blockSize = this._samplesPerFrame >> i;
      var cutoff = (int)((440L * blockSize + 3L * (sampleRate >> 1) - 1) / sampleRate);
      this._subwooferCutoffs[i] = Math.Clamp(cutoff, 4, blockSize);
    }

    // decorrelation sine table
    for (var i = 0; i < 33; ++i) this._sin64[i] = (float)Math.Sin(i * Math.PI / 64.0);

    // VLC tables (decode_init_static)
    this._sfVlc = WmaProVlc.FromSymbolLengths(WmaProTables.ScaleTable, -60);
    this._sfRlVlc = WmaProVlc.FromSymbolLengths(WmaProTables.ScaleRlTable, 0);
    this._coefVlc[0] = WmaProVlc.FromLengthsAndSymbols(WmaProTables.Coef0Lens, WmaProTables.Coef0Syms, 0);
    this._coefVlc[1] = WmaProVlc.FromSymbolLengths(WmaProTables.Coef1Table, 0);
    this._vec4Vlc = WmaProVlc.FromLengthsAndSymbols(WmaProTables.Vec4Lens, WmaProTables.Vec4Syms, -1);
    this._vec2Vlc = WmaProVlc.FromSymbolLengths(WmaProTables.Vec2Table, -1);
    this._vec1Vlc = WmaProVlc.FromSymbolLengths(WmaProTables.Vec1Table, 0);
  }

  // ── ff_wma_get_frame_len_bits (version 3 path) ───────────────────────────────
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

  /// <summary>
  /// Decodes one WMA Pro packet (one reassembled ASF media object of <c>block_align</c>
  /// bytes) and returns the interleaved signed-16-bit PCM for whatever frames completed
  /// in that packet (frames spanning packet boundaries are emitted once their trailing
  /// half arrives). Returns an empty array when the packet only fed the bit reservoir.
  /// Decoder state (the reservoir, MDCT overlap, block lengths) is carried across calls.
  /// </summary>
  public short[] DecodePacket(ReadOnlySpan<byte> packet) {
    var produced = new List<float[][]>();
    this.DecodePacketCore(packet, produced);
    return Interleave(produced, this._channels);
  }

  /// <summary>Alias for <see cref="DecodePacket"/>; WMA Pro packets carry one or more frames (no separate superframe layer).</summary>
  public short[] DecodeSuperframe(ReadOnlySpan<byte> packet) => this.DecodePacket(packet);

  private void DecodePacketCore(ReadOnlySpan<byte> avpkt, List<float[][]> produced) {
    if (avpkt.Length < this._blockAlign) {
      // The reference treats a short packet as packet loss; tolerate by ignoring it.
      this._packetLoss = true;
      return;
    }

    // FFmpeg's demuxer re-invokes decode_packet on the same buffer until the packet is
    // fully consumed (decode_packet returns the byte offset to resume from). For WMAPRO
    // next_packet_start == buf_size - block_align == 0 once we clamp to block_align, so
    // the working buffer is the same block_align bytes each entry; we keep a single packet
    // reader and re-run the decode_packet body until packet_done.
    var packetBuf = avpkt[..this._blockAlign].ToArray();
    this._bufBitSize = this._blockAlign << 3;
    var pgb = new WmaProBitReader(packetBuf, 0, this._bufBitSize);

    var entry = 0;
    while (true) {
      var firstEntry = this._packetDone || this._packetLoss;
      this.DecodePacketBody(pgb, firstEntry, produced);
      if (this._packetDone || this._packetLoss) break;
      if (++entry > MaxSubframes * 4) break; // safety net against a stuck reservoir
    }
  }

  // One invocation of FFmpeg's decode_packet over the (single, persistent) packet reader.
  private void DecodePacketBody(WmaProBitReader pgb, bool firstEntry, List<float[][]> produced) {
    if (firstEntry) {
      this._packetDone = false;

      var packetSequenceNumber = (int)pgb.GetBits(4);
      pgb.SkipBits(2); // drc bit + reserved bit

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
        if (!this._packetLoss) this.DecodeFrame(produced);
      }

      if (this._packetLoss) {
        this._numSavedBits = 0;
        this._packetLoss = false;
      }
    } else {
      // Single persistent reader: the cursor already sits at packet_offset where the
      // previous entry's save_bits left it, so no re-seek is needed (the reference
      // re-creates the reader and skips packet_offset bits to reach the same position).
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

  private int RemainingBits(WmaProBitReader gb) => this._bufBitSize - gb.BitsCount;

  // ── save_bits: copy bits into the frame_data reservoir and re-init s->gb ───────
  private void SaveBits(WmaProBitReader gb, int len, bool append) {
    int buflen;
    if (!append) {
      this._frameOffset = gb.BitsCount & 7;
      this._numSavedBits = this._frameOffset;
      this._frameDataBits = 0;
      Array.Clear(this._frameData);
      // re-establish frameOffset alignment as leading zero bits
      this._frameDataBits = this._frameOffset;
      buflen = (this._numSavedBits + len + 7) >> 3;
    } else {
      buflen = (this._frameDataBits + len + 7) >> 3;
    }

    if (len <= 0 || buflen > MaxFrameSize) {
      this._packetLoss = true;
      return;
    }

    this._numSavedBits += len;
    if (!append) {
      // ff_copy_bits(pb, gb->buffer + (count>>3), num_saved_bits): copy num_saved_bits
      // bits starting at the byte boundary preceding the reader, preserving the low
      // frame_offset bits of alignment.
      this.CopyBitsFromReader(gb, this._numSavedBits);
    } else {
      var align = 8 - (gb.BitsCount & 7);
      if (align > len) align = len;
      this.PutBits(align, gb.GetBits(align));
      len -= align;
      this.CopyBitsAppend(gb, len);
    }
    gb.SkipBits(len);

    this._gb = new WmaProBitReader(this._frameData, 0, this._numSavedBits);
    this._gb.SkipBits(this._frameOffset);
  }

  // Copy `nbits` bits starting at the byte boundary that contains the reader cursor,
  // writing them at the start of frame_data (mirrors ff_copy_bits over the whole buffer).
  private void CopyBitsFromReader(WmaProBitReader gb, int nbits) {
    var srcStartBit = gb.BitsCount & ~7; // byte boundary
    var savedCursor = gb.BitsCount;
    gb.BitsCount = srcStartBit;
    this._frameDataBits = 0;
    var full = nbits >> 3;
    for (var i = 0; i < full; ++i) this.PutBits(8, gb.GetBits(8));
    var rem = nbits & 7;
    if (rem != 0) this.PutBits(rem, gb.GetBits(rem));
    gb.BitsCount = savedCursor;
  }

  private void CopyBitsAppend(WmaProBitReader gb, int len) {
    var full = len >> 3;
    for (var i = 0; i < full; ++i) this.PutBits(8, gb.GetBits(8));
    var rem = len & 7;
    if (rem != 0) this.PutBits(rem, gb.GetBits(rem));
  }

  // MSB-first bit writer into _frameData at _frameDataBits.
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

  // ── decode_frame: returns true when more frames follow in this packet ─────────
  private bool DecodeFrame(List<float[][]> produced) {
    var gb = this._gb;
    var len = 0;
    if (this._lenPrefix) len = (int)gb.GetBits(this._log2FrameSize);

    if (!this.DecodeTileHdr()) { this._packetLoss = true; return false; }

    // postproc transform
    if (this._channels > 1 && gb.GetBit() != 0) {
      if (gb.GetBit() != 0)
        for (var i = 0; i < this._channels * this._channels; ++i) gb.SkipBits(4);
    }

    if (this._dynamicRangeCompression) gb.SkipBits(8); // drc_gain

    if (gb.GetBit() != 0) {
      if (gb.GetBit() != 0) gb.SkipBits(Log2(this._samplesPerFrame * 2)); // trim_start
      if (gb.GetBit() != 0) gb.SkipBits(Log2(this._samplesPerFrame * 2)); // trim_end
    }

    this._parsedAllSubframes = false;
    for (var i = 0; i < this._channels; ++i) {
      this._channel[i].DecodedSamples = 0;
      this._channel[i].CurSubframe = 0;
      this._channel[i].ReuseSf = false;
    }

    while (!this._parsedAllSubframes) {
      if (!this.DecodeSubframe()) { this._packetLoss = true; return false; }
    }

    // emit (or skip) this frame's samples_per_frame
    if (this._skipFrame) {
      this._skipFrame = false;
    } else {
      var frame = new float[this._channels][];
      for (var c = 0; c < this._channels; ++c) {
        frame[c] = new float[this._samplesPerFrame];
        Array.Copy(this._channel[c].Out, 0, frame[c], 0, this._samplesPerFrame);
      }
      produced.Add(frame);
    }

    // shift the second half of out down for the next frame's overlap
    for (var c = 0; c < this._channels; ++c)
      Array.Copy(this._channel[c].Out, this._samplesPerFrame, this._channel[c].Out, 0, this._samplesPerFrame / 2);

    if (this._lenPrefix) {
      var consumed = (gb.BitsCount - this._frameOffset) + 2;
      if (len != consumed) { this._packetLoss = true; return false; }
      gb.SkipBits(len - (gb.BitsCount - this._frameOffset) - 1);
    } else {
      while (gb.BitsCount < this._numSavedBits && gb.GetBit() == 0) { }
    }

    return gb.GetBit() != 0; // trailer bit: more frames?
  }

  // ── decode_tilehdr ─────────────────────────────────────────────────────────────
  private bool DecodeTileHdr() {
    var gb = this._gb;
    var numSamples = new int[MaxChannels];
    var containsSubframe = new bool[MaxChannels];
    var channelsForCur = this._channels;
    var fixedLayout = false;
    var minChannelLen = 0;

    for (var c = 0; c < this._channels; ++c) this._channel[c].NumSubframes = 0;

    if (this._maxNumSubframes == 1 || gb.GetBit() != 0) fixedLayout = true;

    do {
      for (var c = 0; c < this._channels; ++c) {
        if (numSamples[c] == minChannelLen) {
          if (fixedLayout || channelsForCur == 1 ||
              minChannelLen == this._samplesPerFrame - this._minSamplesPerSubframe)
            containsSubframe[c] = true;
          else
            containsSubframe[c] = gb.GetBit() != 0;
        } else containsSubframe[c] = false;
      }

      var subframeLen = this.DecodeSubframeLength(minChannelLen);
      if (subframeLen <= 0) return false;

      minChannelLen += subframeLen;
      for (var c = 0; c < this._channels; ++c) {
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

    for (var c = 0; c < this._channels; ++c) {
      var offset = 0;
      for (var i = 0; i < this._channel[c].NumSubframes; ++i) {
        this._channel[c].SubframeOffset[i] = offset;
        offset += this._channel[c].SubframeLen[i];
      }
    }
    return true;
  }

  private int DecodeSubframeLength(int offset) {
    var gb = this._gb;
    if (offset == this._samplesPerFrame - this._minSamplesPerSubframe)
      return this._minSamplesPerSubframe;
    if (gb.BitsLeft < 1) return -1;

    var frameLenShift = 0;
    if (this._maxSubframeLenBit) {
      if (gb.GetBit() != 0) frameLenShift = 1 + (int)gb.GetBits(this._subframeLenBits - 1);
    } else {
      frameLenShift = (int)gb.GetBits(this._subframeLenBits);
    }

    var subframeLen = this._samplesPerFrame >> frameLenShift;
    if (subframeLen < this._minSamplesPerSubframe || subframeLen > this._samplesPerFrame)
      return -1;
    return subframeLen;
  }

  // ── decode_subframe ───────────────────────────────────────────────────────────
  private bool DecodeSubframe() {
    var gb = this._gb;
    var offset = this._samplesPerFrame;
    var subframeLen = this._samplesPerFrame;
    var totalSamples = this._samplesPerFrame * this._channels;
    var transmitCoeffs = false;

    for (var i = 0; i < this._channels; ++i) {
      this._channel[i].Grouped = false;
      if (offset > this._channel[i].DecodedSamples) {
        offset = this._channel[i].DecodedSamples;
        subframeLen = this._channel[i].SubframeLen[this._channel[i].CurSubframe];
      }
    }

    this._channelsForCurSubframe = 0;
    for (var i = 0; i < this._channels; ++i) {
      var curSub = this._channel[i].CurSubframe;
      totalSamples -= this._channel[i].DecodedSamples;
      if (offset == this._channel[i].DecodedSamples &&
          subframeLen == this._channel[i].SubframeLen[curSub]) {
        totalSamples -= this._channel[i].SubframeLen[curSub];
        this._channel[i].DecodedSamples += this._channel[i].SubframeLen[curSub];
        this._channelIndexesForCurSubframe[this._channelsForCurSubframe++] = i;
      }
    }
    if (totalSamples == 0) this._parsedAllSubframes = true;

    this._tableIdx = Log2(this._samplesPerFrame / subframeLen);
    this._numBands = this._numSfb[this._tableIdx];
    this._curSfbOffsets = this._sfbOffsets[this._tableIdx];
    var curSubwooferCutoff = this._subwooferCutoffs[this._tableIdx];

    offset += this._samplesPerFrame >> 1;
    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      this._channel[c].CoeffsOffset = offset;
    }

    this._subframeLen = subframeLen;
    this._escLen = Log2(this._subframeLen - 1) + 1;

    // skip extended header
    if (gb.GetBit() != 0) {
      var numFillBits = (int)gb.GetBits(2);
      if (numFillBits == 0) {
        var l = (int)gb.GetBits(4);
        numFillBits = (int)gb.GetBitsLong(l) + 1;
      }
      if (gb.BitsCount + numFillBits > this._numSavedBits) return false;
      gb.SkipBits(numFillBits);
    }

    if (gb.GetBit() != 0) return false; // reserved bit must be 0

    if (!this.DecodeChannelTransform()) return false;

    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      this._channel[c].TransmitCoefs = gb.GetBit() != 0;
      if (this._channel[c].TransmitCoefs) transmitCoeffs = true;
    }

    if (this._subframeLen > BlockMaxSize) return false;

    if (transmitCoeffs) {
      var quantStep = 90 * this._bitsPerSample >> 4;

      this._transmitNumVecCoeffs = gb.GetBit() != 0;
      if (this._transmitNumVecCoeffs) {
        var numBits = Log2((this._subframeLen + 3) / 4) + 1;
        for (var i = 0; i < this._channelsForCurSubframe; ++i) {
          var c = this._channelIndexesForCurSubframe[i];
          var numVecCoeffs = (int)gb.GetBits(numBits) << 2;
          if (numVecCoeffs > this._subframeLen) return false;
          this._channel[c].NumVecCoeffs = numVecCoeffs;
        }
      } else {
        for (var i = 0; i < this._channelsForCurSubframe; ++i)
          this._channel[this._channelIndexesForCurSubframe[i]].NumVecCoeffs = this._subframeLen;
      }

      var step = gb.GetSignedBits(6);
      quantStep += step;
      if (step is -32 or 31) {
        var sign = (step == 31 ? 1 : 0) - 1;
        var quant = 0;
        while (gb.BitsCount + 5 < this._numSavedBits && (step = (int)gb.GetBits(5)) == 31)
          quant += 31;
        quantStep += ((quant + step) ^ sign) - sign;
      }

      if (this._channelsForCurSubframe == 1) {
        this._channel[this._channelIndexesForCurSubframe[0]].QuantStep = quantStep;
      } else {
        var modifierLen = (int)gb.GetBits(3);
        for (var i = 0; i < this._channelsForCurSubframe; ++i) {
          var c = this._channelIndexesForCurSubframe[i];
          this._channel[c].QuantStep = quantStep;
          if (gb.GetBit() != 0) {
            if (modifierLen != 0) this._channel[c].QuantStep += (int)gb.GetBits(modifierLen) + 1;
            else ++this._channel[c].QuantStep;
          }
        }
      }

      if (!this.DecodeScaleFactors()) return false;
    }

    // parse coefficients
    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      var ci = this._channel[c];
      if (ci.TransmitCoefs && gb.BitsCount < this._numSavedBits) {
        if (!this.DecodeCoeffs(c)) return false;
      } else {
        Array.Clear(ci.Out, ci.CoeffsOffset, subframeLen);
      }
    }

    if (transmitCoeffs) {
      var mdct = this._mdct[Log2(subframeLen) - BlockMinBits];
      this.InverseChannelTransform();
      for (var i = 0; i < this._channelsForCurSubframe; ++i) {
        var c = this._channelIndexesForCurSubframe[i];
        var ci = this._channel[c];
        var sfIdx = 0;

        if (c == this._lfeChannel && subframeLen > curSubwooferCutoff)
          Array.Clear(this._tmp, curSubwooferCutoff, subframeLen - curSubwooferCutoff);

        for (var b = 0; b < this._numBands; ++b) {
          var end = Math.Min(this._curSfbOffsets[b + 1], this._subframeLen);
          var exp = ci.QuantStep - (ci.MaxScaleFactor - ci.ScaleFactors[sfIdx++]) * ci.ScaleFactorStep;
          var quant = (float)Math.Pow(10.0, exp / 20.0);
          var start = this._curSfbOffsets[b];
          for (var x = start; x < end; ++x)
            this._tmp[x] = ci.Out[ci.CoeffsOffset + x] * quant;
        }

        // IMDCT half: tmp (subframeLen coeffs) -> coeffs (subframeLen samples)
        var outSpan = new float[subframeLen];
        mdct.Inverse(this._tmpSlice(subframeLen), outSpan);
        Array.Copy(outSpan, 0, ci.Out, ci.CoeffsOffset, subframeLen);
      }
    }

    this.WmaProWindow();

    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      if (this._channel[c].CurSubframe >= this._channel[c].NumSubframes) return false;
      ++this._channel[c].CurSubframe;
    }
    return true;
  }

  private float[] _tmpSlice(int len) {
    // The MDCT reads exactly `len` inputs from the front of _tmp.
    if (len == this._tmp.Length) return this._tmp;
    var s = new float[len];
    Array.Copy(this._tmp, 0, s, 0, len);
    return s;
  }

  // ── decode_channel_transform ─────────────────────────────────────────────────
  private bool DecodeChannelTransform() {
    var gb = this._gb;
    this._numChgroups = 0;
    if (this._channels <= 1) return true;

    var remainingChannels = this._channelsForCurSubframe;
    if (gb.GetBit() != 0) return false; // "Channel transform bit" unsupported in reference

    for (this._numChgroups = 0;
         remainingChannels != 0 && this._numChgroups < this._channelsForCurSubframe;
         ++this._numChgroups) {
      var chgroup = this._chgroup[this._numChgroups];
      var dataCount = 0;
      chgroup.NumChannels = 0;
      chgroup.Transform = false;

      if (remainingChannels > 2) {
        for (var i = 0; i < this._channelsForCurSubframe; ++i) {
          var ci = this._channelIndexesForCurSubframe[i];
          if (!this._channel[ci].Grouped && gb.GetBit() != 0) {
            ++chgroup.NumChannels;
            this._channel[ci].Grouped = true;
            chgroup.ChannelData[dataCount++] = ci;
          }
        }
      } else {
        chgroup.NumChannels = remainingChannels;
        for (var i = 0; i < this._channelsForCurSubframe; ++i) {
          var ci = this._channelIndexesForCurSubframe[i];
          if (!this._channel[ci].Grouped) chgroup.ChannelData[dataCount++] = ci;
          this._channel[ci].Grouped = true;
        }
      }

      if (chgroup.NumChannels == 2) {
        if (gb.GetBit() != 0) {
          if (gb.GetBit() != 0) return false; // unknown transform type
        } else {
          chgroup.Transform = true;
          if (this._channels == 2) {
            chgroup.DecorrelationMatrix[0] = 1.0f;
            chgroup.DecorrelationMatrix[1] = -1.0f;
            chgroup.DecorrelationMatrix[2] = 1.0f;
            chgroup.DecorrelationMatrix[3] = 1.0f;
          } else {
            chgroup.DecorrelationMatrix[0] = 0.70703125f;
            chgroup.DecorrelationMatrix[1] = -0.70703125f;
            chgroup.DecorrelationMatrix[2] = 0.70703125f;
            chgroup.DecorrelationMatrix[3] = 0.70703125f;
          }
        }
      } else if (chgroup.NumChannels > 2) {
        if (gb.GetBit() != 0) {
          chgroup.Transform = true;
          if (gb.GetBit() != 0) {
            this.DecodeDecorrelationMatrix(chgroup);
          } else {
            if (chgroup.NumChannels <= 6) {
              var n = chgroup.NumChannels;
              var off = WmaProTables.DefaultDecorrelationOffsets[n];
              Array.Copy(WmaProTables.DefaultDecorrelationMatrices, off, chgroup.DecorrelationMatrix, 0, n * n);
            }
            // num_channels > 6 is a "request sample" no-op in the reference (matrix left zero).
          }
        }
      }

      if (chgroup.Transform) {
        if (gb.GetBit() == 0) {
          for (var i = 0; i < this._numBands; ++i) chgroup.TransformBand[i] = gb.GetBit() != 0;
        } else {
          for (var i = 0; i < this._numBands; ++i) chgroup.TransformBand[i] = true;
        }
      }
      remainingChannels -= chgroup.NumChannels;
    }
    return true;
  }

  private void DecodeDecorrelationMatrix(ChannelGrp chgroup) {
    var gb = this._gb;
    var n = chgroup.NumChannels;
    var rotationOffset = new int[MaxChannels * MaxChannels];
    Array.Clear(chgroup.DecorrelationMatrix, 0, this._channels * this._channels);

    for (var i = 0; i < (n * (n - 1) >> 1); ++i) rotationOffset[i] = (int)gb.GetBits(6);

    for (var i = 0; i < n; ++i)
      chgroup.DecorrelationMatrix[n * i + i] = gb.GetBit() != 0 ? 1.0f : -1.0f;

    var offset = 0;
    for (var i = 1; i < n; ++i) {
      for (var x = 0; x < i; ++x) {
        for (var y = 0; y < i + 1; ++y) {
          var v1 = chgroup.DecorrelationMatrix[x * n + y];
          var v2 = chgroup.DecorrelationMatrix[i * n + y];
          var rn = rotationOffset[offset + x];
          float sinv, cosv;
          if (rn < 32) { sinv = this._sin64[rn]; cosv = this._sin64[32 - rn]; }
          else { sinv = this._sin64[64 - rn]; cosv = -this._sin64[rn - 32]; }
          chgroup.DecorrelationMatrix[y + x * n] = v1 * sinv - v2 * cosv;
          chgroup.DecorrelationMatrix[y + i * n] = v1 * cosv + v2 * sinv;
        }
      }
      offset += i;
    }
  }

  // ── decode_scale_factors ─────────────────────────────────────────────────────
  private bool DecodeScaleFactors() {
    var gb = this._gb;
    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      var ci = this._channel[c];
      ci.ScaleFactors = ci.SavedScaleFactors[ci.ScaleFactorIdx == 0 ? 1 : 0];

      if (ci.ReuseSf) {
        var sfOffsets = this._sfOffsets[this._tableIdx][ci.TableIdx];
        for (var b = 0; b < this._numBands; ++b)
          ci.ScaleFactors[b] = ci.SavedScaleFactors[ci.ScaleFactorIdx][sfOffsets[b]];
      }

      if (ci.CurSubframe == 0 || gb.GetBit() != 0) {
        if (!ci.ReuseSf) {
          ci.ScaleFactorStep = (int)gb.GetBits(2) + 1;
          var val = 45 / ci.ScaleFactorStep;
          for (var b = 0; b < this._numBands; ++b) {
            val += this._sfVlc.Decode(gb, out var ok);
            if (!ok) return false;
            ci.ScaleFactors[b] = val;
          }
        } else {
          for (var b = 0; b < this._numBands; ++b) {
            var idx = this._sfRlVlc.Decode(gb, out var ok);
            if (!ok) return false;
            int skip, val, sign;
            if (idx == 0) {
              var code = (int)gb.GetBits(14);
              val = code >> 6;
              sign = (code & 1) - 1;
              skip = (code & 0x3f) >> 1;
            } else if (idx == 1) {
              break;
            } else {
              skip = WmaProTables.ScaleRlRun[idx];
              val = WmaProTables.ScaleRlLevel[idx];
              sign = (int)gb.GetBit() - 1;
            }
            b += skip;
            if (b >= this._numBands) return false;
            ci.ScaleFactors[b] += (val ^ sign) - sign;
          }
        }
        ci.ScaleFactorIdx = ci.ScaleFactorIdx == 0 ? 1 : 0;
        ci.TableIdx = this._tableIdx;
        ci.ReuseSf = true;
      }

      ci.MaxScaleFactor = ci.ScaleFactors[0];
      for (var b = 1; b < this._numBands; ++b)
        if (ci.ScaleFactors[b] > ci.MaxScaleFactor) ci.MaxScaleFactor = ci.ScaleFactors[b];
    }
    return true;
  }

  // ── decode_coeffs ─────────────────────────────────────────────────────────────
  private static readonly float[] FvalTab = BuildFvalTab();
  private static float[] BuildFvalTab() {
    var t = new float[16];
    for (var i = 0; i < 16; ++i) t[i] = i;
    return t;
  }

  private bool DecodeCoeffs(int c) {
    var gb = this._gb;
    var ci = this._channel[c];
    var rlMode = false;
    var curCoeff = 0;
    var numZeros = 0;

    var vlctable = (int)gb.GetBit();
    var vlc = this._coefVlc[vlctable];
    var run = vlctable == 1 ? WmaProTables.Coef1Run : WmaProTables.Coef0Run;
    var level = vlctable == 1 ? WmaProTables.Coef1Level : WmaProTables.Coef0Level;

    while ((this._transmitNumVecCoeffs || !rlMode) && curCoeff + 3 < ci.NumVecCoeffs) {
      var vals = new float[4];
      var idx = this._vec4Vlc.Decode(gb, out var ok);
      if (!ok) return false;
      if (idx < 0) {
        for (var i = 0; i < 4; i += 2) {
          var idx2 = this._vec2Vlc.Decode(gb, out var ok2);
          if (!ok2) return false;
          if (idx2 < 0) {
            var v0 = this._vec1Vlc.Decode(gb, out var ok0);
            if (!ok0) return false;
            if (v0 == WmaProTables.Vec1Size - 1) v0 += (int)WmaGetLargeVal(gb);
            var v1 = this._vec1Vlc.Decode(gb, out var ok1);
            if (!ok1) return false;
            if (v1 == WmaProTables.Vec1Size - 1) v1 += (int)WmaGetLargeVal(gb);
            vals[i] = v0;
            vals[i + 1] = v1;
          } else {
            vals[i] = FvalTab[idx2 >> 4];
            vals[i + 1] = FvalTab[idx2 & 0xF];
          }
        }
      } else {
        vals[0] = FvalTab[idx >> 12];
        vals[1] = FvalTab[(idx >> 8) & 0xF];
        vals[2] = FvalTab[(idx >> 4) & 0xF];
        vals[3] = FvalTab[idx & 0xF];
      }

      for (var i = 0; i < 4; ++i) {
        if (vals[i] != 0) {
          var sign = gb.GetBit() != 0 ? 1.0f : -1.0f;
          ci.Out[ci.CoeffsOffset + curCoeff] = vals[i] * sign;
          numZeros = 0;
        } else {
          ci.Out[ci.CoeffsOffset + curCoeff] = 0;
          if (++numZeros > (this._subframeLen >> 8)) rlMode = true;
        }
        ++curCoeff;
      }
    }

    if (curCoeff < this._subframeLen) {
      Array.Clear(ci.Out, ci.CoeffsOffset + curCoeff, this._subframeLen - curCoeff);
      if (!this.RunLevelDecode(vlc, level, run, ci.Out, ci.CoeffsOffset, curCoeff, this._subframeLen, this._subframeLen, this._escLen, 0))
        return false;
    }
    return true;
  }

  // ff_wma_run_level_decode, version == 1 (WMA Pro escape path).
  private bool RunLevelDecode(WmaProVlc vlc, float[] levelTable, ushort[] runTable, float[] ptr, int ptrBase,
                              int offset, int numCoefs, int blockLen, int frameLenBits, int coefNbBits) {
    _ = coefNbBits; // unused on the version-1 path
    var coefMask = blockLen - 1;
    for (; offset < numCoefs; ++offset) {
      var code = vlc.Decode(this._gb, out var ok);
      if (!ok) return false;
      if (code > 1) {
        offset += runTable[code];
        // ilvl ^ (sign & 0x80000000): bit 1 -> positive level, bit 0 -> negated.
        var negate = this._gb.GetBit() == 0;
        var lvl = levelTable[code];
        ptr[ptrBase + (offset & coefMask)] = negate ? -lvl : lvl;
      } else if (code == 1) {
        break;
      } else {
        var levelVal = (int)WmaGetLargeVal(this._gb);
        if (this._gb.GetBit() != 0) {
          if (this._gb.GetBit() != 0) {
            if (this._gb.GetBit() != 0) return false; // broken escape
            offset += (int)this._gb.GetBits(frameLenBits) + 4;
          } else {
            offset += (int)this._gb.GetBits(2) + 1;
          }
        }
        var sign = (int)this._gb.GetBit() - 1;
        ptr[ptrBase + (offset & coefMask)] = (levelVal ^ sign) - sign;
      }
    }
    return offset <= numCoefs;
  }

  private static uint WmaGetLargeVal(WmaProBitReader gb) {
    var nBits = 8;
    if (gb.GetBit() != 0) {
      nBits += 8;
      if (gb.GetBit() != 0) {
        nBits += 8;
        if (gb.GetBit() != 0) nBits += 7;
      }
    }
    return gb.GetBitsLong(nBits);
  }

  // ── inverse_channel_transform ─────────────────────────────────────────────────
  private void InverseChannelTransform() {
    for (var i = 0; i < this._numChgroups; ++i) {
      var g = this._chgroup[i];
      if (!g.Transform) continue;
      var num = g.NumChannels;
      var tb = g.TransformBand;

      for (var band = 0; band < this._numBands; ++band) {
        var start = this._curSfbOffsets[band];
        var end = Math.Min(this._curSfbOffsets[band + 1], this._subframeLen);
        if (tb[band]) {
          for (var y = start; y < end; ++y) {
            var data = new float[MaxChannels];
            for (var ch = 0; ch < num; ++ch) {
              var cidx = g.ChannelData[ch];
              data[ch] = this._channel[cidx].Out[this._channel[cidx].CoeffsOffset + y];
            }
            var matPos = 0;
            for (var ch = 0; ch < num; ++ch) {
              float sum = 0;
              for (var k = 0; k < num; ++k) sum += data[k] * g.DecorrelationMatrix[matPos++];
              var cidx = g.ChannelData[ch];
              this._channel[cidx].Out[this._channel[cidx].CoeffsOffset + y] = sum;
            }
          }
        } else if (this._channels == 2) {
          var c0 = g.ChannelData[0];
          var c1 = g.ChannelData[1];
          for (var y = start; y < end; ++y) {
            this._channel[c0].Out[this._channel[c0].CoeffsOffset + y] *= 181.0f / 128;
            this._channel[c1].Out[this._channel[c1].CoeffsOffset + y] *= 181.0f / 128;
          }
        }
      }
    }
  }

  // ── wmapro_window: sine-window overlap-add (vector_fmul_window) ────────────────
  private void WmaProWindow() {
    for (var i = 0; i < this._channelsForCurSubframe; ++i) {
      var c = this._channelIndexesForCurSubframe[i];
      var ci = this._channel[c];
      var winlen = ci.PrevBlockLen;
      var start = ci.CoeffsOffset - (winlen >> 1);

      if (this._subframeLen < winlen) {
        start += (winlen - this._subframeLen) >> 1;
        winlen = this._subframeLen;
      }

      var window = this._windows[Log2(winlen) - BlockMinBits];
      var half = winlen >> 1;

      // vector_fmul_window(out+start, out+start, out+start+half, window, half)
      VectorFmulWindow(ci.Out, start, ci.Out, start, ci.Out, start + half, window, half);

      ci.PrevBlockLen = this._subframeLen;
    }
  }

  // In-place-safe windowed overlap-add (FFmpeg vector_fmul_window_c). dst/src0/src1 may
  // alias; the reference processes paired indices so the in-place case is well defined.
  private static void VectorFmulWindow(float[] dst, int dstBase, float[] src0, int src0Base,
                                       float[] src1, int src1Base, float[] win, int len) {
    // Snapshot src0/src1 windows to be safe under aliasing.
    var s0 = new float[len];
    var s1 = new float[len];
    Array.Copy(src0, src0Base, s0, 0, len);
    Array.Copy(src1, src1Base, s1, 0, len);
    for (var i = 0; i < len; ++i) {
      var j = len - 1 - i;
      var a = s0[i];
      var b = s1[j];
      var wi = win[i];
      var wj = win[j];
      dst[dstBase + i] = a * wj - b * wi;
      dst[dstBase + len + j] = a * wi + b * wj;
    }
  }

  // ── helpers ───────────────────────────────────────────────────────────────────
  private static short[] Interleave(List<float[][]> frames, int channels) {
    var total = 0;
    foreach (var f in frames) total += f[0].Length;
    var pcm = new short[total * channels];
    var pos = 0;
    foreach (var f in frames) {
      var n = f[0].Length;
      for (var i = 0; i < n; ++i)
        for (var ch = 0; ch < channels; ++ch) {
          var s = f[ch][i] * 32768.0f;
          pcm[(pos + i) * channels + ch] = (short)Math.Clamp((int)Math.Round(s), short.MinValue, short.MaxValue);
        }
      pos += n;
    }
    return pcm;
  }

  private static int Log2(int v) {
    var r = 0;
    while ((v >>= 1) != 0) ++r;
    return r;
  }

  private static int PopCount(uint v) {
    var c = 0;
    while (v != 0) { c += (int)(v & 1); v >>= 1; }
    return c;
  }

  private static float[] SineWindow(int n) {
    var w = new float[n];
    for (var i = 0; i < n; ++i) w[i] = (float)Math.Sin((i + 0.5) * (Math.PI / (2.0 * n)));
    return w;
  }

  private static int[][] NewJaggedInt(int a, int b) { var r = new int[a][]; for (var i = 0; i < a; ++i) r[i] = new int[b]; return r; }
  private static int[][][] NewJaggedInt3(int a, int b, int c) {
    var r = new int[a][][];
    for (var i = 0; i < a; ++i) { r[i] = new int[b][]; for (var j = 0; j < b; ++j) r[i][j] = new int[c]; }
    return r;
  }
}
