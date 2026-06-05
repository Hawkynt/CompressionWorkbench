#pragma warning disable CS1591
namespace Codec.BinkAudio;

/// <summary>
/// Decode-only port of FFmpeg's Bink Audio decoder (<c>libavcodec/binkaudio.c</c>),
/// covering both flavours: <c>BINKAUDIO_RDFT</c> ('RDFT', the default) and
/// <c>BINKAUDIO_DCT</c> ('DCT '). A stream is a sequence of packets; each packet begins
/// with a 4-byte reported size that is skipped, after which one or more transform blocks
/// are decoded. Coefficients are read variable-width from an LSB-first bitstream
/// (<see cref="BinkAudioBitReader"/>), dequantized per critical band, run-length zeroed,
/// transformed (inverse RDFT or DCT-III, <see cref="BinkAudioTransforms"/>) and
/// overlap-added with the previous block. The float output is converted to interleaved
/// signed 16-bit PCM.
///
/// <para>For the RDFT flavour the reference treats the (possibly stereo) signal as a
/// single interleaved channel at a multiplied sample rate, so <see cref="DecodeStream"/>
/// returns interleaved samples directly. For the DCT flavour each channel is transformed
/// separately; up to <c>MAX_CHANNELS = 2</c> channels are decoded per block and the
/// per-channel outputs are interleaved here.</para>
/// </summary>
public sealed class BinkAudioCodec {

  private const int MaxChannels = 2;

  private readonly bool _useDct;
  private readonly bool _versionB;
  private readonly int _channels;        // logical channels carried in the transform loop
  private readonly int _frameLen;        // transform size in samples
  private readonly int _overlapLen;      // overlap size in samples
  private readonly int _blockSize;       // samples emitted per packet block (interleaved)
  private readonly int _numBands;
  private readonly int[] _bands = new int[26];
  private readonly double _root;
  private readonly double[] _quant = new double[96];
  private readonly double _transformScale;

  private readonly float[][] _previous;  // trailing overlap coeffs from the previous block
  private bool _first = true;

  /// <summary>Number of output channels (the demuxer's declared channel count).</summary>
  public int OutputChannels { get; }

  /// <summary>Output sample rate (the demuxer's declared rate, unmultiplied).</summary>
  public int SampleRate { get; }

  // Test-only accessors pinning the init-derived framing (InternalsVisibleTo Compression.Tests).
  internal int FrameLenForTest => this._frameLen;
  internal int NumBandsForTest => this._numBands;
  internal int[] BandsForTest => this._bands;

  /// <summary>
  /// Builds a decoder. <paramref name="sampleRate"/> and <paramref name="channels"/> are
  /// the values declared in the container's audio-track header; <paramref name="useDct"/>
  /// selects the DCT flavour (otherwise RDFT). <paramref name="versionB"/> reflects the
  /// Bink revision 'b' (extradata[3] == 'b'), which changes float coefficient framing and
  /// the run parser. Mirrors binkaudio.c <c>decode_init</c>.
  /// </summary>
  public BinkAudioCodec(int sampleRate, int channels, bool useDct, bool versionB) {
    if (channels < 1)
      throw new ArgumentOutOfRangeException(nameof(channels));

    this.OutputChannels = channels;
    this.SampleRate = sampleRate;
    this._useDct = useDct;
    this._versionB = versionB;

    int frameLenBits;
    if (sampleRate < 22050)
      frameLenBits = 9;
    else if (sampleRate < 44100)
      frameLenBits = 10;
    else
      frameLenBits = 11;

    var effectiveRate = sampleRate;
    if (!useDct) {
      // RDFT: audio is already interleaved; treat as one channel at a multiplied rate.
      effectiveRate = sampleRate * channels;
      this._channels = 1;
      if (!versionB)
        frameLenBits += Log2(channels);
    } else {
      this._channels = channels;
    }

    this._frameLen = 1 << frameLenBits;
    this._overlapLen = this._frameLen / 16;
    this._blockSize = (this._frameLen - this._overlapLen) * Math.Min(MaxChannels, this._channels);

    var sampleRateHalf = (effectiveRate + 1) / 2;
    this._root = useDct
      ? this._frameLen / (Math.Sqrt(this._frameLen) * 32768.0)
      : 2.0 / (Math.Sqrt(this._frameLen) * 32768.0);

    for (var i = 0; i < 96; ++i)
      this._quant[i] = BinkAudioTables.QuantBase[i] * this._root;

    // Number of bands.
    var bands = 1;
    while (bands < 25) {
      if (sampleRateHalf <= BinkAudioTables.CriticalFreqs[bands - 1])
        break;
      ++bands;
    }
    this._numBands = bands;

    this._bands[0] = 2;
    for (var i = 1; i < this._numBands; ++i)
      this._bands[i] = (BinkAudioTables.CriticalFreqs[i - 1] * this._frameLen / sampleRateHalf) & ~1;
    this._bands[this._numBands] = this._frameLen;

    // RDFT: inverse real DFT, full size, scale 0.5. DCT: DCT-III, half size, scale 1/(2N).
    this._transformScale = useDct ? 1.0 / (1 << frameLenBits) : 0.5;

    this._previous = new float[Math.Max(this._channels, MaxChannels)][];
    for (var i = 0; i < this._previous.Length; ++i)
      this._previous[i] = new float[this._frameLen];
  }

  private static int Log2(int v) {
    var r = 0;
    while ((v >>= 1) != 0)
      ++r;
    return r;
  }

  /// <summary>
  /// Decodes a whole audio stream (the concatenation of every packet for one track) into
  /// interleaved signed-16-bit PCM. Each packet's leading 4-byte reported size is honoured
  /// per binkaudio.c (<c>skip_bits_long(gb, 32)</c>). Returns an empty array if nothing
  /// could be decoded.
  /// </summary>
  public short[] DecodeStream(IReadOnlyList<byte[]> packets) {
    var pcm = new List<short>();
    foreach (var packet in packets)
      this.DecodePacket(packet, pcm);
    return pcm.ToArray();
  }

  /// <summary>Decodes a single packet, appending interleaved 16-bit samples to <paramref name="pcm"/>.</summary>
  public void DecodePacket(byte[] packet, List<short> pcm) {
    if (packet.Length < 4)
      return;

    var reader = new BinkAudioBitReader(packet, 0, packet.Length * 8);
    reader.SkipBits(32); // reported decoded-byte count

    // One packet carries the full set of channels; the reference loops in groups of
    // MAX_CHANNELS (chan_split). For RDFT _channels == 1, so a single block is decoded.
    var chOffset = 0;
    var blocks = new List<float[]>();
    while (chOffset < this._channels) {
      var blockChannels = Math.Min(MaxChannels, this._channels - chOffset);
      var ok = this.DecodeBlock(reader, chOffset, blockChannels, blocks);
      if (!ok)
        return;
      chOffset += MaxChannels;
      reader.Align32();
    }

    // blocks holds one float[frameLen] per transform channel. Emit (frameLen - overlap)
    // interleaved samples — the reference emits block_size / channels per output channel.
    var emit = this._frameLen - this._overlapLen;
    var outChannels = blocks.Count;
    for (var i = 0; i < emit; ++i)
      for (var ch = 0; ch < outChannels; ++ch)
        pcm.Add(FloatToS16(blocks[ch][i]));
  }

  /// <summary>
  /// Decodes one transform block for <paramref name="blockChannels"/> channels starting at
  /// <paramref name="chOffset"/>, applying dequantization, the inverse transform and
  /// overlap-add. Decoded float frames are appended to <paramref name="output"/>. Returns
  /// <see langword="false"/> on an incomplete block (mirrors the reference's
  /// <c>AVERROR_INVALIDDATA</c> bail-outs).
  /// </summary>
  private bool DecodeBlock(BinkAudioBitReader reader, int chOffset, int blockChannels, List<float[]> output) {
    if (this._useDct)
      reader.SkipBits(2);

    var frameLen = this._frameLen;
    var decoded = new float[blockChannels][];

    for (var ch = 0; ch < blockChannels; ++ch) {
      var coeffs = new float[frameLen + 2];

      if (this._versionB) {
        if (reader.BitsLeft < 64)
          return false;
        coeffs[0] = (float)(BitsToFloat(reader.GetBits(32)) * this._root);
        coeffs[1] = (float)(BitsToFloat(reader.GetBits(32)) * this._root);
      } else {
        if (reader.BitsLeft < 58)
          return false;
        coeffs[0] = (float)(GetFloat(reader) * this._root);
        coeffs[1] = (float)(GetFloat(reader) * this._root);
      }

      if (reader.BitsLeft < this._numBands * 8)
        return false;
      var quant = new double[25];
      for (var i = 0; i < this._numBands; ++i) {
        var value = (int)reader.GetBits(8);
        quant[i] = this._quant[Math.Min(value, 95)];
      }

      var k = 0;
      var q = quant[0];

      // Parse coefficients.
      var idx = 2;
      while (idx < frameLen) {
        int j;
        if (this._versionB) {
          j = idx + 16;
        } else {
          var v = reader.GetBit();
          if (v != 0) {
            v = (int)reader.GetBits(4);
            j = idx + BinkAudioTables.RleLengthTab[v] * 8;
          } else {
            j = idx + 8;
          }
        }

        j = Math.Min(j, frameLen);

        var width = (int)reader.GetBits(4);
        if (width == 0) {
          for (var z = idx; z < j; ++z)
            coeffs[z] = 0.0f;
          idx = j;
          while (this._bands[k] < idx)
            q = quant[k++];
        } else {
          while (idx < j) {
            if (this._bands[k] == idx)
              q = quant[k++];
            var coeff = (int)reader.GetBits(width);
            if (coeff != 0) {
              var sign = reader.GetBit();
              coeffs[idx] = (float)(sign != 0 ? -q * coeff : q * coeff);
            } else {
              coeffs[idx] = 0.0f;
            }
            ++idx;
          }
        }
      }

      var frame = new float[frameLen];
      if (this._useDct) {
        coeffs[0] *= 2.0f; // coeffs[0] /= 0.5
        // DCT-III over all frame_len coefficients. The reference inits the transform with
        // size 1<<(frame_len_bits-1) but av_tx's DCT init doubles that for the inverse
        // (ff_tx_dct_init: "if (inv) len *= 2"), so the effective transform length is
        // frame_len and it reads/writes frame_len samples.
        BinkAudioTransforms.InverseDctIII(coeffs, frame, frameLen, this._transformScale);
      } else {
        // RDFT layout fix-up (binkaudio.c): negate the imaginary halves, move the value
        // in coeffs[1] (Nyquist) to coeffs[frameLen] and clear coeffs[1].
        for (var i = 2; i < frameLen; i += 2)
          coeffs[i + 1] *= -1.0f;
        coeffs[frameLen] = coeffs[1];
        coeffs[frameLen + 1] = 0.0f;
        coeffs[1] = 0.0f;
        BinkAudioTransforms.InverseRdft(coeffs, frame, frameLen, this._transformScale);
      }

      decoded[ch] = frame;
    }

    // Overlap-add with the previous block's trailing coefficients, then stash this block's.
    for (var ch = 0; ch < blockChannels; ++ch) {
      var frame = decoded[ch];
      var prev = this._previous[chOffset + ch];
      var count = this._overlapLen * blockChannels;
      if (!this._first) {
        var jj = ch;
        for (var i = 0; i < this._overlapLen; ++i, jj += blockChannels)
          frame[i] = (prev[i] * (count - jj) + frame[i] * jj) / count;
      }
      Array.Copy(frame, frameLen - this._overlapLen, prev, 0, this._overlapLen);
      output.Add(frame);
    }

    this._first = false;
    return true;
  }

  /// <summary>Reads a Bink custom float (binkaudio.c <c>get_float</c>): 5-bit power, 23-bit mantissa, sign.</summary>
  private static double GetFloat(BinkAudioBitReader reader) {
    var power = (int)reader.GetBits(5);
    var mantissa = (int)reader.GetBits(23);
    var f = mantissa * Math.Pow(2.0, power - 23);
    if (reader.GetBit() != 0)
      f = -f;
    return f;
  }

  /// <summary>Reinterprets 32 bits as an IEEE-754 single (version-b float coefficients, <c>av_int2float</c>).</summary>
  private static float BitsToFloat(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

  /// <summary>
  /// Converts a float sample (FFmpeg <c>AV_SAMPLE_FMT_FLT</c>, normalised to roughly
  /// [-1, 1] because <c>root</c> already folds in the <c>1/32768</c> factor) to signed
  /// 16-bit, matching the standard FLT→S16 resample (multiply by 32768, round, clamp).
  /// </summary>
  private static short FloatToS16(float value) {
    var v = (int)Math.Round(value * 32768.0f);
    if (v > short.MaxValue) v = short.MaxValue;
    else if (v < short.MinValue) v = short.MinValue;
    return (short)v;
  }
}
