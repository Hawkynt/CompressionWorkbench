#pragma warning disable CS1591
namespace Codec.Sbc;

/// <summary>
/// Bluetooth low-complexity subband codec (SBC), including the mSBC (modified SBC) wide-band-speech
/// variant. A faithful fixed-point port of FFmpeg's <c>libavcodec/sbcdec.c</c> + <c>sbc.c</c>
/// (tables in <see cref="SbcTables"/> from <c>sbcdec_data.h</c>). SBC has no encoder here (FFmpeg's
/// SBC encoder lives elsewhere and is not ported); this type only synthesises 16-bit linear PCM.
/// <para>
/// An SBC stream is a sequence of self-describing frames. Each frame begins with a syncword —
/// <c>0x9C</c> for ordinary A2DP SBC, <c>0xAD</c> for mSBC — followed by a packed header carrying
/// the sampling frequency, block count, channel mode, bit-allocation method, subband count and
/// bitpool, then a CRC-8 over the header and scale factors. mSBC fixes those parameters: 16 kHz,
/// 15 blocks, loudness allocation, mono, 8 subbands, bitpool 26.
/// </para>
/// <para>
/// Decode pipeline (bit-exact with the reference): parse header → validate CRC-8 → derive the
/// per-subband bit allocation (<c>ff_sbc_calculate_bits</c>, loudness or SNR) → read scale factors
/// and quantised samples, reconstructing each subband sample with the reference's
/// <c>(((audio &lt;&lt; 1) | 1) &lt;&lt; shift) / levels - (1 &lt;&lt; shift)</c> formula → undo joint stereo →
/// run the 4- or 8-subband polyphase synthesis filter (prototype filter + synthesis matrix,
/// circular V buffer) → clip to 16 bit. Each frame yields <c>blocks × subbands</c> samples per
/// channel.
/// </para>
/// </summary>
public static class SbcCodec {

  /// <summary>SBC A2DP syncword (first byte of an ordinary SBC frame).</summary>
  public const byte SbcSyncword = 0x9C;

  /// <summary>mSBC syncword (first byte of a modified-SBC wide-band-speech frame).</summary>
  public const byte MsbcSyncword = 0xAD;

  // libavcodec/sbc.h: SBCDEC_FIXED_EXTRA_BITS.
  private const int FixedExtraBits = 2;

  // libavcodec/sbc.h sampling-frequency code → Hz.
  private static readonly int[] FrequencyHz = [16000, 32000, 44100, 48000];

  /// <summary>SBC channel mode (libavcodec/sbc.h).</summary>
  public enum ChannelMode { Mono = 0, DualChannel = 1, Stereo = 2, JointStereo = 3 }

  /// <summary>Bit-allocation method (libavcodec/sbc.h).</summary>
  public enum Allocation { Loudness = 0, Snr = 1 }

  /// <summary>Parsed parameters of a single SBC frame plus its byte length on the wire.</summary>
  public readonly record struct FrameHeader(
    int FrequencyCode, int SampleRate, int Blocks, ChannelMode Mode, int Channels,
    Allocation AllocationMethod, int Subbands, int Bitpool, int FrameLengthBytes, bool IsMsbc);

  /// <summary>Decoder error matching the reference's negative return codes.</summary>
  private enum FrameError { TooShort = -1, BadSync = -2, BadCrc = -3, BadBitpool = -4 }

  /// <summary>
  /// CRC-8 of the first <paramref name="bitLength"/> bits of <paramref name="data"/>, matching
  /// FFmpeg's <c>ff_sbc_crc8</c>: whole bytes through the AV_CRC_8_EBU table (start value 0x0F),
  /// then any trailing partial byte bit-by-bit with polynomial 0x1D.
  /// </summary>
  public static byte Crc8(ReadOnlySpan<byte> data, int bitLength) {
    var byteLength = bitLength >> 3;
    var bitRemainder = bitLength & 7;

    var crc = (byte)0x0F;
    for (var i = 0; i < byteLength; ++i)
      crc = SbcTables.Crc8Table[crc ^ data[i]];

    if (bitRemainder != 0) {
      var bits = data[byteLength];
      while (bitRemainder-- > 0) {
        var mask = (sbyte)(bits ^ crc);
        crc = (byte)((crc << 1) ^ ((mask >> 7) & 0x1D));
        bits <<= 1;
      }
    }

    return crc;
  }

  /// <summary>
  /// Parses the frame header at the start of <paramref name="data"/> without decoding samples,
  /// returning <c>null</c> when the syncword, bitpool or available length is invalid. Used both by
  /// the decoder and by structural stream validation.
  /// </summary>
  public static FrameHeader? ReadHeader(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      return null;

    int freqCode, blocks, subbands, bitpool, channels;
    ChannelMode mode;
    Allocation allocation;
    bool isMsbc;

    if (data[0] == MsbcSyncword) {
      if (data[1] != 0 || data[2] != 0)
        return null;
      freqCode = 0; // SBC_FREQ_16000
      blocks = 15;
      allocation = Allocation.Loudness;
      mode = ChannelMode.Mono;
      channels = 1;
      subbands = 8;
      bitpool = 26;
      isMsbc = true;
    } else if (data[0] == SbcSyncword) {
      freqCode = (data[1] >> 6) & 0x03;
      blocks = 4 * ((data[1] >> 4) & 0x03) + 4;
      mode = (ChannelMode)((data[1] >> 2) & 0x03);
      channels = mode == ChannelMode.Mono ? 1 : 2;
      allocation = (Allocation)((data[1] >> 1) & 0x01);
      subbands = (data[1] & 0x01) != 0 ? 8 : 4;
      bitpool = data[2];
      isMsbc = false;

      if (mode is ChannelMode.Mono or ChannelMode.DualChannel && bitpool > 16 * subbands)
        return null;
      if (mode is ChannelMode.Stereo or ChannelMode.JointStereo && bitpool > 32 * subbands)
        return null;
    } else
      return null;

    var length = FrameLength(channels, subbands, blocks, bitpool, mode);
    if (length > data.Length)
      return null;

    return new FrameHeader(freqCode, FrequencyHz[freqCode], blocks, mode, channels, allocation,
      subbands, bitpool, length, isMsbc);
  }

  /// <summary>
  /// Total on-the-wire byte length of an SBC frame with the given parameters (the standard frame
  /// length formula: 4-byte header + joint flags + scale factors + quantised samples, byte-padded).
  /// </summary>
  private static int FrameLength(int channels, int subbands, int blocks, int bitpool, ChannelMode mode) {
    var bits = 4 * subbands * channels; // scale factors (4 bits each)
    bits += mode switch {
      ChannelMode.Mono or ChannelMode.DualChannel => blocks * channels * bitpool,
      ChannelMode.Stereo => blocks * bitpool,
      _ => blocks * bitpool + subbands, // joint stereo carries one join flag per subband
    };
    return 4 + (bits + 7) / 8;
  }

  /// <summary>
  /// Decodes a single frame's worth of samples from <paramref name="data"/> into
  /// <paramref name="state"/>, advancing the synthesis history. Returns the parsed header (so the
  /// caller can slice off <see cref="FrameHeader.FrameLengthBytes"/>) and writes
  /// <c>blocks × subbands</c> samples per channel into the per-channel output lists. Returns
  /// <c>null</c> on any sync/CRC/length error.
  /// </summary>
  private static FrameHeader? DecodeFrame(ReadOnlySpan<byte> data, SbcDecoderState state,
                                          List<short>[] outputs) {
    var header = ReadHeader(data);
    if (header is not { } frame)
      return null;

    var scaleFactor = new int[2][];
    for (var ch = 0; ch < 2; ++ch)
      scaleFactor[ch] = new int[8];

    // Header bytes 1..2 feed the CRC; joint flags and scale factors are appended bit-packed.
    var crcHeader = new byte[11];
    crcHeader[0] = data[1];
    crcHeader[1] = data[2];
    var crcPos = 16;

    var consumed = 32;
    var joint = 0;
    if (frame.Mode == ChannelMode.JointStereo) {
      for (var sb = 0; sb < frame.Subbands - 1; ++sb)
        joint |= ((data[4] >> (7 - sb)) & 0x01) << sb;
      crcHeader[crcPos / 8] = frame.Subbands == 4 ? (byte)(data[4] & 0xF0) : data[4];
      consumed += frame.Subbands;
      crcPos += frame.Subbands;
    }

    for (var ch = 0; ch < frame.Channels; ++ch)
      for (var sb = 0; sb < frame.Subbands; ++sb) {
        scaleFactor[ch][sb] = (data[consumed >> 3] >> (4 - (consumed & 0x7))) & 0x0F;
        crcHeader[crcPos >> 3] |= (byte)(scaleFactor[ch][sb] << (4 - (crcPos & 0x7)));
        consumed += 4;
        crcPos += 4;
      }

    if (data[3] != Crc8(crcHeader, crcPos))
      return null;

    var bits = CalculateBits(frame, scaleFactor);
    var levels = new uint[2][];
    for (var ch = 0; ch < 2; ++ch)
      levels[ch] = new uint[8];
    for (var ch = 0; ch < frame.Channels; ++ch)
      for (var sb = 0; sb < frame.Subbands; ++sb)
        levels[ch][sb] = (uint)((1 << bits[ch][sb]) - 1);

    var sbSample = new int[16][][];
    for (var blk = 0; blk < frame.Blocks; ++blk) {
      sbSample[blk] = new int[2][];
      for (var ch = 0; ch < 2; ++ch)
        sbSample[blk][ch] = new int[8];
    }

    for (var blk = 0; blk < frame.Blocks; ++blk)
      for (var ch = 0; ch < frame.Channels; ++ch)
        for (var sb = 0; sb < frame.Subbands; ++sb) {
          if (levels[ch][sb] == 0) {
            sbSample[blk][ch][sb] = 0;
            continue;
          }

          var shift = scaleFactor[ch][sb] + 1 + FixedExtraBits;
          uint audioSample = 0;
          for (var bit = 0; bit < bits[ch][sb]; ++bit) {
            if (consumed > data.Length * 8)
              return null;
            if (((data[consumed >> 3] >> (7 - (consumed & 0x7))) & 0x01) != 0)
              audioSample |= (uint)(1 << (bits[ch][sb] - bit - 1));
            ++consumed;
          }

          sbSample[blk][ch][sb] = (int)(
            ((((ulong)audioSample << 1) | 1) << shift) / levels[ch][sb]) - (1 << shift);
        }

    if (frame.Mode == ChannelMode.JointStereo)
      for (var blk = 0; blk < frame.Blocks; ++blk)
        for (var sb = 0; sb < frame.Subbands; ++sb)
          if ((joint & (0x01 << sb)) != 0) {
            var temp = sbSample[blk][0][sb] + sbSample[blk][1][sb];
            sbSample[blk][1][sb] = sbSample[blk][0][sb] - sbSample[blk][1][sb];
            sbSample[blk][0][sb] = temp;
          }

    Synthesize(state, frame, sbSample, outputs);
    return frame;
  }

  // libavcodec/sbc.c: ff_sbc_calculate_bits. Returns bits[2][8].
  private static int[][] CalculateBits(FrameHeader frame, int[][] scaleFactor) {
    var bits = new int[2][];
    for (var i = 0; i < 2; ++i)
      bits[i] = new int[8];

    var subbands = frame.Subbands;
    var sf = frame.FrequencyCode;

    if (frame.Mode is ChannelMode.Mono or ChannelMode.DualChannel) {
      var bitneed = new int[2][];
      for (var i = 0; i < 2; ++i)
        bitneed[i] = new int[8];

      for (var ch = 0; ch < frame.Channels; ++ch) {
        var maxBitneed = 0;
        if (frame.AllocationMethod == Allocation.Snr) {
          for (var sb = 0; sb < subbands; ++sb) {
            bitneed[ch][sb] = scaleFactor[ch][sb];
            if (bitneed[ch][sb] > maxBitneed)
              maxBitneed = bitneed[ch][sb];
          }
        } else {
          for (var sb = 0; sb < subbands; ++sb) {
            if (scaleFactor[ch][sb] == 0)
              bitneed[ch][sb] = -5;
            else {
              var loudness = subbands == 4
                ? scaleFactor[ch][sb] - SbcTables.Offset4[sf][sb]
                : scaleFactor[ch][sb] - SbcTables.Offset8[sf][sb];
              bitneed[ch][sb] = loudness > 0 ? loudness / 2 : loudness;
            }
            if (bitneed[ch][sb] > maxBitneed)
              maxBitneed = bitneed[ch][sb];
          }
        }

        var bitcount = 0;
        var slicecount = 0;
        var bitslice = maxBitneed + 1;
        do {
          --bitslice;
          bitcount += slicecount;
          slicecount = 0;
          for (var sb = 0; sb < subbands; ++sb) {
            if (bitneed[ch][sb] > bitslice + 1 && bitneed[ch][sb] < bitslice + 16)
              ++slicecount;
            else if (bitneed[ch][sb] == bitslice + 1)
              slicecount += 2;
          }
        } while (bitcount + slicecount < frame.Bitpool);

        if (bitcount + slicecount == frame.Bitpool) {
          bitcount += slicecount;
          --bitslice;
        }

        for (var sb = 0; sb < subbands; ++sb) {
          if (bitneed[ch][sb] < bitslice + 2)
            bits[ch][sb] = 0;
          else {
            bits[ch][sb] = bitneed[ch][sb] - bitslice;
            if (bits[ch][sb] > 16)
              bits[ch][sb] = 16;
          }
        }

        for (var sb = 0; bitcount < frame.Bitpool && sb < subbands; ++sb) {
          if (bits[ch][sb] is >= 2 and < 16) {
            ++bits[ch][sb];
            ++bitcount;
          } else if (bitneed[ch][sb] == bitslice + 1 && frame.Bitpool > bitcount + 1) {
            bits[ch][sb] = 2;
            bitcount += 2;
          }
        }

        for (var sb = 0; bitcount < frame.Bitpool && sb < subbands; ++sb) {
          if (bits[ch][sb] < 16) {
            ++bits[ch][sb];
            ++bitcount;
          }
        }
      }
    } else {
      var bitneed = new int[2][];
      for (var i = 0; i < 2; ++i)
        bitneed[i] = new int[8];

      var maxBitneed = 0;
      if (frame.AllocationMethod == Allocation.Snr) {
        for (var ch = 0; ch < 2; ++ch)
          for (var sb = 0; sb < subbands; ++sb) {
            bitneed[ch][sb] = scaleFactor[ch][sb];
            if (bitneed[ch][sb] > maxBitneed)
              maxBitneed = bitneed[ch][sb];
          }
      } else {
        for (var ch = 0; ch < 2; ++ch)
          for (var sb = 0; sb < subbands; ++sb) {
            if (scaleFactor[ch][sb] == 0)
              bitneed[ch][sb] = -5;
            else {
              var loudness = subbands == 4
                ? scaleFactor[ch][sb] - SbcTables.Offset4[sf][sb]
                : scaleFactor[ch][sb] - SbcTables.Offset8[sf][sb];
              bitneed[ch][sb] = loudness > 0 ? loudness / 2 : loudness;
            }
            if (bitneed[ch][sb] > maxBitneed)
              maxBitneed = bitneed[ch][sb];
          }
      }

      var bitcount = 0;
      var slicecount = 0;
      var bitslice = maxBitneed + 1;
      do {
        --bitslice;
        bitcount += slicecount;
        slicecount = 0;
        for (var ch = 0; ch < 2; ++ch)
          for (var sb = 0; sb < subbands; ++sb) {
            if (bitneed[ch][sb] > bitslice + 1 && bitneed[ch][sb] < bitslice + 16)
              ++slicecount;
            else if (bitneed[ch][sb] == bitslice + 1)
              slicecount += 2;
          }
      } while (bitcount + slicecount < frame.Bitpool);

      if (bitcount + slicecount == frame.Bitpool) {
        bitcount += slicecount;
        --bitslice;
      }

      for (var ch = 0; ch < 2; ++ch)
        for (var sb = 0; sb < subbands; ++sb) {
          if (bitneed[ch][sb] < bitslice + 2)
            bits[ch][sb] = 0;
          else {
            bits[ch][sb] = bitneed[ch][sb] - bitslice;
            if (bits[ch][sb] > 16)
              bits[ch][sb] = 16;
          }
        }

      {
        var ch = 0;
        var sb = 0;
        while (bitcount < frame.Bitpool) {
          if (bits[ch][sb] is >= 2 and < 16) {
            ++bits[ch][sb];
            ++bitcount;
          } else if (bitneed[ch][sb] == bitslice + 1 && frame.Bitpool > bitcount + 1) {
            bits[ch][sb] = 2;
            bitcount += 2;
          }
          if (ch == 1) {
            ch = 0;
            if (++sb >= subbands)
              break;
          } else
            ch = 1;
        }
      }

      {
        var ch = 0;
        var sb = 0;
        while (bitcount < frame.Bitpool) {
          if (bits[ch][sb] < 16) {
            ++bits[ch][sb];
            ++bitcount;
          }
          if (ch == 1) {
            ch = 0;
            if (++sb >= subbands)
              break;
          } else
            ch = 1;
        }
      }
    }

    return bits;
  }

  // libavcodec/sbcdec.c: sbc_synthesize_four / sbc_synthesize_eight, per block per channel.
  private static void Synthesize(SbcDecoderState state, FrameHeader frame, int[][][] sbSample,
                                 List<short>[] outputs) {
    Span<short> block = stackalloc short[8];
    for (var ch = 0; ch < frame.Channels; ++ch)
      for (var blk = 0; blk < frame.Blocks; ++blk) {
        if (frame.Subbands == 4)
          SynthesizeFour(state, sbSample[blk][ch], ch, block);
        else
          SynthesizeEight(state, sbSample[blk][ch], ch, block);
        for (var sb = 0; sb < frame.Subbands; ++sb)
          outputs[ch].Add(block[sb]);
      }
  }

  private static void SynthesizeFour(SbcDecoderState state, int[] sbSample, int ch, Span<short> output) {
    var v = state.V[ch];
    var offset = state.Offset[ch];

    for (var i = 0; i < 8; ++i) {
      if (--offset[i] < 0) {
        offset[i] = 79;
        Array.Copy(v, 0, v, 80, 9);
      }
      var m = SbcTables.SynMatrix4[i];
      v[offset[i]] = (int)(((uint)m[0] * (uint)sbSample[0] +
                            (uint)m[1] * (uint)sbSample[1] +
                            (uint)m[2] * (uint)sbSample[2] +
                            (uint)m[3] * (uint)sbSample[3]) >> 15);
    }

    for (int idx = 0, i = 0; i < 4; ++i, idx += 5) {
      var k = (i + 4) & 0xF;
      var acc = (uint)v[offset[i] + 0] * (uint)SbcTables.Proto4M0[idx + 0] +
                (uint)v[offset[k] + 1] * (uint)SbcTables.Proto4M1[idx + 0] +
                (uint)v[offset[i] + 2] * (uint)SbcTables.Proto4M0[idx + 1] +
                (uint)v[offset[k] + 3] * (uint)SbcTables.Proto4M1[idx + 1] +
                (uint)v[offset[i] + 4] * (uint)SbcTables.Proto4M0[idx + 2] +
                (uint)v[offset[k] + 5] * (uint)SbcTables.Proto4M1[idx + 2] +
                (uint)v[offset[i] + 6] * (uint)SbcTables.Proto4M0[idx + 3] +
                (uint)v[offset[k] + 7] * (uint)SbcTables.Proto4M1[idx + 3] +
                (uint)v[offset[i] + 8] * (uint)SbcTables.Proto4M0[idx + 4] +
                (uint)v[offset[k] + 9] * (uint)SbcTables.Proto4M1[idx + 4];
      output[i] = ClipInt16((int)acc >> 15);
    }
  }

  private static void SynthesizeEight(SbcDecoderState state, int[] sbSample, int ch, Span<short> output) {
    var v = state.V[ch];
    var offset = state.Offset[ch];

    for (var i = 0; i < 16; ++i) {
      if (--offset[i] < 0) {
        offset[i] = 159;
        Array.Copy(v, 0, v, 160, 9);
      }
      var m = SbcTables.SynMatrix8[i];
      v[offset[i]] = (int)(((uint)m[0] * (uint)sbSample[0] +
                            (uint)m[1] * (uint)sbSample[1] +
                            (uint)m[2] * (uint)sbSample[2] +
                            (uint)m[3] * (uint)sbSample[3] +
                            (uint)m[4] * (uint)sbSample[4] +
                            (uint)m[5] * (uint)sbSample[5] +
                            (uint)m[6] * (uint)sbSample[6] +
                            (uint)m[7] * (uint)sbSample[7]) >> 15);
    }

    for (int idx = 0, i = 0; i < 8; ++i, idx += 5) {
      var k = (i + 8) & 0xF;
      var acc = (uint)v[offset[i] + 0] * (uint)SbcTables.Proto8M0[idx + 0] +
                (uint)v[offset[k] + 1] * (uint)SbcTables.Proto8M1[idx + 0] +
                (uint)v[offset[i] + 2] * (uint)SbcTables.Proto8M0[idx + 1] +
                (uint)v[offset[k] + 3] * (uint)SbcTables.Proto8M1[idx + 1] +
                (uint)v[offset[i] + 4] * (uint)SbcTables.Proto8M0[idx + 2] +
                (uint)v[offset[k] + 5] * (uint)SbcTables.Proto8M1[idx + 2] +
                (uint)v[offset[i] + 6] * (uint)SbcTables.Proto8M0[idx + 3] +
                (uint)v[offset[k] + 7] * (uint)SbcTables.Proto8M1[idx + 3] +
                (uint)v[offset[i] + 8] * (uint)SbcTables.Proto8M0[idx + 4] +
                (uint)v[offset[k] + 9] * (uint)SbcTables.Proto8M1[idx + 4];
      output[i] = ClipInt16((int)acc >> 15);
    }
  }

  private static short ClipInt16(int v) =>
    v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;

  /// <summary>
  /// Walks <paramref name="data"/> as a sequence of self-describing SBC frames, returning one
  /// <see cref="FrameHeader"/> per fully-present frame. The header is structurally validated
  /// (syncword, bitpool bounds, declared length present); the CRC is not checked here. Walking
  /// stops at the first byte that is not a valid frame header or at a trailing truncated frame.
  /// </summary>
  public static IReadOnlyList<FrameHeader> ReadFrames(ReadOnlySpan<byte> data) {
    var list = new List<FrameHeader>();
    var pos = 0;
    while (pos < data.Length) {
      var header = ReadHeader(data[pos..]);
      if (header is not { } frame)
        break;
      list.Add(frame);
      pos += frame.FrameLengthBytes;
    }
    return list;
  }

  /// <summary>
  /// Returns the parameters of the first valid frame in <paramref name="data"/>, or <c>null</c>
  /// when the stream does not begin with a valid SBC/mSBC frame.
  /// </summary>
  public static FrameHeader? Probe(ReadOnlySpan<byte> data) => ReadHeader(data);

  /// <summary>
  /// Decodes an SBC/mSBC stream to one 16-bit linear-PCM short array per channel. The number of
  /// channels and the sample rate are taken from the first frame; a stream that mixes channel
  /// counts is decoded up to the first frame whose channel count differs. Returns an empty array
  /// when nothing decodes.
  /// </summary>
  public static short[][] DecodeToChannels(ReadOnlySpan<byte> data, out int sampleRate, out int channels) {
    sampleRate = 0;
    channels = 0;

    var first = ReadHeader(data);
    if (first is not { } firstFrame)
      return [];

    sampleRate = firstFrame.SampleRate;
    channels = firstFrame.Channels;

    var outputs = new List<short>[2];
    for (var i = 0; i < 2; ++i)
      outputs[i] = [];

    var state = new SbcDecoderState();
    var pos = 0;
    while (pos < data.Length) {
      var slice = data[pos..];
      var header = ReadHeader(slice);
      if (header is not { } frame || frame.Channels != channels)
        break;
      var decoded = DecodeFrame(slice, state, outputs);
      if (decoded is null)
        break; // CRC or truncation failure: stop cleanly at the first bad frame
      pos += decoded.Value.FrameLengthBytes;
    }

    var result = new short[channels][];
    for (var ch = 0; ch < channels; ++ch)
      result[ch] = outputs[ch].ToArray();
    return result;
  }

  /// <summary>
  /// Decodes an SBC/mSBC stream to interleaved 16-bit linear PCM (channels woven sample-by-sample).
  /// Convenience wrapper over <see cref="DecodeToChannels"/>.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, out int sampleRate, out int channels) {
    var perChannel = DecodeToChannels(data, out sampleRate, out channels);
    if (perChannel.Length == 0)
      return [];
    if (perChannel.Length == 1)
      return perChannel[0];

    var frames = perChannel[0].Length;
    var interleaved = new short[frames * channels];
    for (var f = 0; f < frames; ++f)
      for (var ch = 0; ch < channels; ++ch)
        interleaved[f * channels + ch] = perChannel[ch][f];
    return interleaved;
  }

  /// <summary>Per-channel synthesis history: the circular V buffer and its 16 offsets.</summary>
  private sealed class SbcDecoderState {
    // libavcodec/sbcdec.c: V[2][170], offset[2][16] initialised to 10*i + 10.
    public readonly int[][] V = [new int[170], new int[170]];
    public readonly int[][] Offset = [new int[16], new int[16]];

    public SbcDecoderState() {
      for (var ch = 0; ch < 2; ++ch)
        for (var i = 0; i < 16; ++i)
          this.Offset[ch][i] = 10 * i + 10;
    }
  }
}
