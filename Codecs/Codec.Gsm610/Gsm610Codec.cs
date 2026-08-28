namespace Codec.Gsm610;

/// <summary>
/// GSM 06.10 full-rate speech codec (ETSI EN 300 961).
/// <para>
/// Each 33-byte frame decodes to 160 × 16-bit PCM samples at 8 kHz. The decoder
/// unpacks the bitstream into LAR/LTP/RPE parameters, applies RPE grid positioning
/// and the LTP + short-term synthesis filters. The fixed-point synthesis remains a
/// compact approximation of the ETSI reference; the encoder lives in the companion
/// partial declaration and emits the standard 0xD signature + 260 parameter bits.
/// </para>
/// </summary>
public static partial class Gsm610Codec {

  /// <summary>Size of one encoded GSM 06.10 frame in bytes.</summary>
  public const int FrameBytes = 33;

  /// <summary>Number of PCM samples produced per decoded frame.</summary>
  public const int FrameSamples = 160;

  // Short-term predictor log-area-ratio dequantisation tables (ETSI 06.10 table 2.1).
  private static readonly short[] InvA = [13107, 13107, 13107, 13107, 19223, 17476, 31454, 29708];
  private static readonly short[] MicB = [0, 0, 2048, -2560, 94, -1792, -341, -1144];

  // LTP gain quantisation (2-bit).
  private static readonly short[] Qlb = [3277, 11469, 21299, 32767];

  /// <summary>
  /// Decodes a buffer of GSM 06.10 frames to interleaved 16-bit PCM.
  /// </summary>
  /// <param name="gsm">Concatenated 33-byte frames, one per channel per frame-group.</param>
  /// <param name="channels">Number of interleaved channels (1 or 2 typical).</param>
  public static short[] Decode(ReadOnlySpan<byte> gsm, int channels) {
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (gsm.Length % (FrameBytes * channels) != 0)
      throw new ArgumentException("GSM 06.10 input is not a whole number of frame-groups.", nameof(gsm));

    var groupCount = gsm.Length / (FrameBytes * channels);
    var pcm = new short[groupCount * FrameSamples * channels];
    var decoders = new Decoder[channels];
    for (var c = 0; c < channels; ++c) decoders[c] = new Decoder();

    Span<short> frameOut = stackalloc short[FrameSamples];
    for (var g = 0; g < groupCount; ++g) {
      for (var c = 0; c < channels; ++c) {
        var off = (g * channels + c) * FrameBytes;
        decoders[c].DecodeFrame(gsm.Slice(off, FrameBytes), frameOut);
        for (var i = 0; i < FrameSamples; ++i)
          pcm[(g * FrameSamples + i) * channels + c] = frameOut[i];
      }
    }
    return pcm;
  }

  /// <summary>
  /// Decodes a buffer of raw 33-byte GSM 06.10 frames (single mono stream) to 16-bit
  /// PCM. This is the "toast"/<c>.gsm</c> on-disk layout — a bare concatenation of
  /// frames with no container. Each frame's first byte carries the signature nibble
  /// <c>0xD</c> in its high four bits (the magic byte ranges <c>0xD0..0xDF</c>).
  /// </summary>
  /// <param name="gsm">Concatenated 33-byte frames.</param>
  public static short[] DecodeRaw(ReadOnlySpan<byte> gsm) => Decode(gsm, channels: 1);

  /// <summary>
  /// Reports whether <paramref name="gsm"/> is a whole number of 33-byte frames whose
  /// per-frame signature nibbles are all <c>0xD</c> — the cheap structural check a
  /// headerless <c>.gsm</c> reader uses before committing to a decode.
  /// </summary>
  public static bool LooksLikeRawFrames(ReadOnlySpan<byte> gsm) {
    if (gsm.Length == 0 || gsm.Length % FrameBytes != 0)
      return false;
    for (var off = 0; off < gsm.Length; off += FrameBytes)
      if ((gsm[off] & 0xF0) != 0xD0)
        return false;
    return true;
  }

  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _buf;
    private int _bitPos;

    public BitReader(ReadOnlySpan<byte> buf) { _buf = buf; _bitPos = 0; }

    public int Read(int bits) {
      var v = 0;
      for (var i = 0; i < bits; ++i) {
        var byteIdx = _bitPos >> 3;
        if (byteIdx >= _buf.Length) return v << (bits - i);
        var bitIdx = 7 - (_bitPos & 7);
        v = (v << 1) | ((_buf[byteIdx] >> bitIdx) & 1);
        ++_bitPos;
      }
      return v;
    }
  }

  private sealed class Decoder {
    private readonly short[] _drp = new short[160 + 120]; // 120-sample history
    private readonly short[] _v = new short[9];

    public void DecodeFrame(ReadOnlySpan<byte> frame, Span<short> pcm) {
      if (frame.Length != FrameBytes)
        throw new ArgumentException($"A GSM 06.10 frame must contain exactly {FrameBytes} bytes.", nameof(frame));

      var br = new BitReader(frame);
      var signature = br.Read(4);
      if (signature != 0xD)
        throw new InvalidDataException($"Invalid GSM 06.10 frame signature 0x{signature:X1}; expected 0xD.");

      // LAR: 8 values, 6/6/5/5/4/4/3/3 bits.
      Span<short> LARc = stackalloc short[8];
      LARc[0] = (short)br.Read(6);
      LARc[1] = (short)br.Read(6);
      LARc[2] = (short)br.Read(5);
      LARc[3] = (short)br.Read(5);
      LARc[4] = (short)br.Read(4);
      LARc[5] = (short)br.Read(4);
      LARc[6] = (short)br.Read(3);
      LARc[7] = (short)br.Read(3);

      // 4 sub-frames × (7+2+2+6 + 13×3) bits.
      Span<short> Nc = stackalloc short[4];
      Span<short> bc = stackalloc short[4];
      Span<short> Mc = stackalloc short[4];
      Span<short> xmaxc = stackalloc short[4];
      Span<short> xmc = stackalloc short[52];
      for (var k = 0; k < 4; ++k) {
        Nc[k] = (short)br.Read(7);
        bc[k] = (short)br.Read(2);
        Mc[k] = (short)br.Read(2);
        xmaxc[k] = (short)br.Read(6);
        for (var i = 0; i < 13; ++i)
          xmc[k * 13 + i] = (short)br.Read(3);
      }

      // Dequantise LAR → reflection coefficient approximations.
      Span<short> rrp = stackalloc short[8];
      for (var i = 0; i < 8; ++i) {
        var larBits = i < 2 ? 6 : i < 4 ? 5 : i < 6 ? 4 : 3;
        var signMask = 1 << (larBits - 1);
        var signExt = LARc[i] >= signMask ? LARc[i] - (1 << larBits) : (int)LARc[i];
        var val = (signExt * 256 + MicB[i] * 4) / Math.Max(1, InvA[i] >> 8);
        if (val > 32767) val = 32767;
        if (val < -32768) val = -32768;
        rrp[i] = (short)val;
      }

      // Process 4 sub-frames.
      Span<short> erp = stackalloc short[40];
      for (var k = 0; k < 4; ++k) {
        erp.Clear();
        // xmaxc 0..63 → magnitude scale factor.
        var scale = 1 << Math.Clamp((int)xmaxc[k] / 8, 0, 8);
        var grid = Mc[k] & 3;
        for (var i = 0; i < 13; ++i) {
          var x = xmc[k * 13 + i] & 7;
          var signed = x < 4 ? x : x - 8;
          var idx = grid + 3 * i;
          if (idx < 40) erp[idx] = (short)(signed * scale);
        }

        // LTP synthesis.
        var ltpGain = Qlb[bc[k] & 3];
        var drpOff = 120 + k * 40;
        for (var i = 0; i < 40; ++i) {
          var lag = Math.Clamp((int)Nc[k], 40, 120);
          var hist = _drp[drpOff + i - lag];
          var synth = erp[i] + (short)(((int)ltpGain * hist) >> 15);
          _drp[drpOff + i] = (short)Clamp(synth);
        }

        // Short-term synthesis filter (reflection lattice form).
        for (var i = 0; i < 40; ++i) {
          int s = _drp[drpOff + i];
          for (var j = 7; j >= 0; --j) {
            s -= ((int)rrp[j] * _v[j]) >> 15;
            s = Clamp(s);
            _v[j + 1] = (short)Clamp(_v[j] + (((int)rrp[j] * s) >> 15));
          }
          _v[0] = (short)s;
          pcm[k * 40 + i] = (short)s;
        }
      }

      // Slide drp history window by 160 samples.
      for (var i = 0; i < 120; ++i) _drp[i] = _drp[i + 160];
    }

    private static int Clamp(int x) => x > 32767 ? 32767 : x < -32768 ? -32768 : x;
  }
}
