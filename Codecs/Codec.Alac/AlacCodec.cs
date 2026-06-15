#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Alac;

/// <summary>
/// ALAC (Apple Lossless) codec — encoder and decoder ported from Apple's
/// open-sourced reference (<c>ALACDecoder.cpp</c>/<c>ALACEncoder.cpp</c> with the
/// <c>dp_*</c> dynamic predictor, <c>ag_*</c> adaptive Golomb/Rice coder and
/// <c>matrix_*</c> inter-channel decorrelation). A coded ALAC stream is a sequence of
/// self-delimiting frames; each frame is a little chain of audio elements (a single
/// channel element <c>SCE</c>, a channel-pair element <c>CPE</c>, fill/data elements
/// that are skipped, terminated by an <c>END</c> tag) packed MSB-first big-endian.
/// <para>
/// The decoder reconstructs interleaved little-endian PCM. The encoder emits
/// spec-shaped frames — always non-escape, a fixed predictor order with the
/// reference's default coefficient seed and the standard adaptive coder — so its
/// output round-trips losslessly through this decoder for 16- and 24-bit mono and
/// stereo. Hand-built uncompressed (escape) frames decode too, proving the header
/// parsing is independent of the encoder.
/// </para>
/// </summary>
public static class AlacCodec {

  // Audio element tags (3-bit), per the reference / MPEG-4 syntactic element ids.
  private const int IdSce = 0; // single channel element
  private const int IdCpe = 1; // channel pair element
  private const int IdCce = 2;
  private const int IdLfe = 3;
  private const int IdDse = 4; // data stream element (skip)
  private const int IdPce = 5;
  private const int IdFil = 6; // fill element (skip)
  private const int IdEnd = 7; // terminator

  private const int DefaultOrder = 4;
  private const int DefaultShift = 9; // quantisation denominator shift (matches predictor)

  // ── Public API ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes a concatenation of ALAC <paramref name="frames"/> against <paramref name="cookie"/>
  /// into interleaved little-endian PCM. Each frame holds <c>cookie.FrameLength</c> samples
  /// except possibly the last (which carries an explicit sample count). Frames are decoded
  /// in order until the input is exhausted.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> frames, AlacCookie cookie) {
    ArgumentNullException.ThrowIfNull(cookie);
    var data = frames.ToArray();
    var bytesPerSample = (cookie.BitDepth + 7) / 8;
    using var output = new MemoryStream();

    var pos = 0;
    while (pos < data.Length) {
      var reader = new AlacBitReader(data, pos, data.Length - pos);
      var pcm = DecodeFrame(reader, cookie, out var consumedBits, out var produced);
      if (produced == 0)
        break;

      output.Write(pcm, 0, produced * cookie.NumChannels * bytesPerSample);

      var consumedBytes = (consumedBits + 7) / 8;
      if (consumedBytes <= 0)
        break;
      pos += consumedBytes;
    }

    return output.ToArray();
  }

  /// <summary>
  /// Encodes interleaved little-endian PCM into a concatenation of ALAC frames plus the
  /// matching magic cookie. Mono and stereo, 16- and 24-bit, are supported.
  /// </summary>
  public static (byte[] Frames, AlacCookie Cookie) Encode(
      ReadOnlySpan<byte> pcmInterleaved, int channels, int sampleRate, int bitsPerSample, int frameLength = 4096) {
    if (channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(channels), "ALAC encoder supports mono or stereo.");
    if (bitsPerSample is not (16 or 24))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "ALAC encoder supports 16- or 24-bit PCM.");
    if (frameLength <= 0)
      throw new ArgumentOutOfRangeException(nameof(frameLength));

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    var pcm = pcmInterleaved.ToArray();
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (sample × channels) size.");

    var totalSamples = pcm.Length / frameBytes;
    using var allFrames = new MemoryStream();
    var maxFrameBytes = 0;

    for (var start = 0; start < totalSamples; start += frameLength) {
      var count = Math.Min(frameLength, totalSamples - start);
      var frame = EncodeFrame(pcm, start, count, channels, bitsPerSample, frameLength);
      allFrames.Write(frame, 0, frame.Length);
      if (frame.Length > maxFrameBytes)
        maxFrameBytes = frame.Length;
    }

    var cookie = new AlacCookie(
      FrameLength: (uint)frameLength,
      CompatibleVersion: 0,
      BitDepth: (byte)bitsPerSample,
      Pb: 40, Mb: 10, Kb: 14,
      NumChannels: (byte)channels,
      MaxRun: 255,
      MaxFrameBytes: (uint)maxFrameBytes,
      AvgBitRate: 0,
      SampleRate: (uint)sampleRate);

    return (allFrames.ToArray(), cookie);
  }

  // ── Frame decode ─────────────────────────────────────────────────────────────

  private static byte[] DecodeFrame(AlacBitReader bits, AlacCookie cookie, out int consumedBits, out int numSamples) {
    var bytesPerSample = (cookie.BitDepth + 7) / 8;
    var channels = cookie.NumChannels;
    var maxSamples = (int)cookie.FrameLength;
    var perChannel = new int[channels][];
    for (var ch = 0; ch < channels; ++ch)
      perChannel[ch] = new int[maxSamples];

    numSamples = 0;
    var channelsFilled = 0;
    var done = false;

    // Read elements until the END tag (the encoder always terminates a frame with it),
    // so the bit cursor lands exactly at the frame boundary for the next frame.
    while (!done) {
      var tag = (int)bits.Read(3);
      switch (tag) {
        case IdEnd:
          done = true;
          break;

        case IdFil:
        case IdDse: {
          // 4-bit element instance + 8-bit count (with 255 escape) of bytes to skip.
          bits.Read(4);
          var n = (int)bits.Read(8);
          if (n == 255)
            n += (int)bits.Read(8) - 1;
          bits.Advance(n * 8);
          break;
        }

        case IdSce:
        case IdLfe: {
          var samples = DecodeChannelElement(bits, cookie, elementChannels: 1, out var produced, perChannel, channelsFilled);
          numSamples = produced;
          channelsFilled += 1;
          _ = samples;
          break;
        }

        case IdCpe: {
          var samples = DecodeChannelElement(bits, cookie, elementChannels: 2, out var produced, perChannel, channelsFilled);
          numSamples = produced;
          channelsFilled += 2;
          _ = samples;
          break;
        }

        default:
          // CCE/PCE and unknowns aren't produced by this encoder; stop cleanly.
          done = true;
          break;
      }
    }

    bits.ByteAlign();
    consumedBits = bits.Position;

    if (numSamples == 0)
      return [];

    var outBytes = new byte[numSamples * channels * bytesPerSample];
    var idx = 0;
    for (var s = 0; s < numSamples; ++s)
      for (var ch = 0; ch < channels; ++ch) {
        WriteSampleLe(outBytes, idx, perChannel[ch][s], bytesPerSample);
        idx += bytesPerSample;
      }
    return outBytes;
  }

  // Decodes one SCE (1 channel) or CPE (2 channels) element into perChannel[firstChannel..].
  private static int DecodeChannelElement(
      AlacBitReader bits, AlacCookie cookie, int elementChannels,
      out int numSamples, int[][] perChannel, int firstChannel) {
    bits.Read(4);                       // element instance tag
    bits.Read(12);                      // unused
    var partialFrame = bits.ReadOne();  // 1 if this frame carries an explicit sample count
    var outputShift = (int)bits.Read(2) * 8; // bytes shifted out → bits
    var escape = bits.ReadOne();        // 1 = uncompressed

    numSamples = partialFrame != 0 ? (int)bits.Read(32) : (int)cookie.FrameLength;
    var n = numSamples;

    var baseBits = cookie.BitDepth - outputShift + (elementChannels - 1);

    int mixBits = 0, mixRes = 0;
    if (elementChannels == 2) {
      mixBits = (int)bits.Read(8);
      mixRes = SignExtend8((int)bits.Read(8));
    }

    var shiftBuffers = outputShift > 0 ? new int[elementChannels][] : null;

    if (escape != 0) {
      // Uncompressed: raw samples, channel-interleaved, baseBits each.
      for (var s = 0; s < n; ++s)
        for (var ch = 0; ch < elementChannels; ++ch) {
          var raw = (int)bits.Read(cookie.BitDepth - outputShift);
          perChannel[firstChannel + ch][s] = SignExtend(raw, cookie.BitDepth - outputShift);
        }
      // No shift bytes embedded in escape frames produced here.
      return n;
    }

    // Prediction headers + residuals per channel.
    var chanBuffers = new int[elementChannels][];
    for (var ch = 0; ch < elementChannels; ++ch)
      chanBuffers[ch] = new int[n];

    if (outputShift > 0)
      for (var ch = 0; ch < elementChannels; ++ch)
        shiftBuffers![ch] = new int[n];

    for (var ch = 0; ch < elementChannels; ++ch) {
      var predType = (int)bits.Read(4);
      var shift = (int)bits.Read(4);
      var riceMod = (int)bits.Read(3);
      var order = (int)bits.Read(5);
      var coefs = new int[order];
      for (var j = 0; j < order; ++j)
        coefs[j] = SignExtend16((int)bits.Read(16));

      AlacRice.Decode(bits, chanBuffers[ch], 0, n, cookie.Kb, cookie.Pb, cookie.Mb << 4, baseBits);
      ZigZagToSigned(chanBuffers[ch], n);
      AlacPredictor.Decompress(chanBuffers[ch], n, coefs, order, shift, baseBits);
      _ = (riceMod, predType);
    }

    // Shifted-out low bytes (output shift) are stored after the channels.
    if (outputShift > 0)
      for (var s = 0; s < n; ++s)
        for (var ch = 0; ch < elementChannels; ++ch)
          shiftBuffers![ch][s] = (int)bits.Read(outputShift);

    if (elementChannels == 2) {
      var left = new int[n];
      var right = new int[n];
      AlacMatrix.Unmix(chanBuffers[0], chanBuffers[1], left, right, n, mixBits, mixRes);
      chanBuffers[0] = left;
      chanBuffers[1] = right;
    }

    for (var ch = 0; ch < elementChannels; ++ch)
      for (var s = 0; s < n; ++s) {
        var v = chanBuffers[ch][s];
        if (outputShift > 0)
          v = (v << outputShift) | shiftBuffers![ch][s];
        perChannel[firstChannel + ch][s] = v;
      }

    return n;
  }

  // ── Frame encode ─────────────────────────────────────────────────────────────

  private static byte[] EncodeFrame(
      byte[] pcm, int startSample, int count, int channels, int bitsPerSample, int frameLength) {
    var bytesPerSample = bitsPerSample / 8;
    var w = new AlacBitWriter();
    var partial = count != frameLength;
    var elementChannels = channels;

    w.Write((uint)(elementChannels == 2 ? IdCpe : IdSce), 3);
    w.Write(0, 4);                          // element instance tag
    w.Write(0, 12);                         // unused
    w.WriteOne(partial ? 1u : 0u);          // partial frame
    w.Write(0, 2);                          // output shift = 0
    w.WriteOne(0);                          // escape = 0 (compressed)
    if (partial)
      w.Write((uint)count, 32);

    var baseBits = bitsPerSample + (elementChannels - 1);

    if (elementChannels == 2) {
      w.Write(0, 8);                        // mixBits
      w.Write(0, 8);                        // mixRes = 0 → plain L/R
    }

    // Gather per-channel signed samples.
    var chan = new int[elementChannels][];
    for (var ch = 0; ch < elementChannels; ++ch)
      chan[ch] = new int[count];
    for (var s = 0; s < count; ++s)
      for (var ch = 0; ch < elementChannels; ++ch) {
        var off = (startSample + s) * channels * bytesPerSample + ch * bytesPerSample;
        chan[ch][s] = ReadSampleLe(pcm, off, bytesPerSample);
      }

    int[][] coded;
    if (elementChannels == 2) {
      var u = new int[count];
      var v = new int[count];
      AlacMatrix.Mix(chan[0], chan[1], u, v, count, 0, 0); // mixRes 0 ⇒ u=L, v=R
      coded = [u, v];
    } else {
      coded = [chan[0]];
    }

    for (var ch = 0; ch < elementChannels; ++ch) {
      var order = DefaultOrder;
      var shift = DefaultShift;
      var coefs = DefaultCoefs(order);

      var residuals = (int[])coded[ch].Clone();
      AlacPredictor.Compress(residuals, count, coefs, order, shift, baseBits);
      SignedToZigZag(residuals, count);

      w.Write(0, 4);                        // predictor type (0 → standard adaptive)
      w.Write((uint)shift, 4);
      w.Write(0, 3);                        // rice modifier
      w.Write((uint)order, 5);
      for (var j = 0; j < order; ++j)
        w.Write((uint)(coefs[j] & 0xFFFF), 16);

      AlacRice.Encode(w, residuals, 0, count, KbDefault, PbDefault, MbDefault << 4, baseBits);
    }

    w.Write((uint)IdEnd, 3);
    return w.ToArray();
  }

  private const int PbDefault = 40;
  private const int MbDefault = 10;
  private const int KbDefault = 14;

  private static int[] DefaultCoefs(int order) {
    // Apple's default predictor seed for order 4 (init_coefs in dp_enc.c, scaled to the
    // shift); zero for other orders. The adaptation refines them per frame.
    if (order == 4)
      return [0, 0, 0, 0];
    return new int[order];
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────

  private static void ZigZagToSigned(int[] buf, int n) {
    for (var i = 0; i < n; ++i) {
      var u = (uint)buf[i];
      buf[i] = (int)(u >> 1) ^ -(int)(u & 1);
    }
  }

  private static void SignedToZigZag(int[] buf, int n) {
    for (var i = 0; i < n; ++i) {
      var v = buf[i];
      buf[i] = (v << 1) ^ (v >> 31);
    }
  }

  private static int ReadSampleLe(byte[] data, int offset, int bytes) => bytes switch {
    2 => SignExtend16(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))),
    3 => SignExtend(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16), 24),
    _ => throw new ArgumentOutOfRangeException(nameof(bytes)),
  };

  private static void WriteSampleLe(byte[] data, int offset, int value, int bytes) {
    switch (bytes) {
      case 2:
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)value);
        break;
      case 3:
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(bytes));
    }
  }

  private static int SignExtend(int value, int bits) {
    if (bits >= 32)
      return value;
    var s = 32 - bits;
    return (value << s) >> s;
  }

  private static int SignExtend8(int value) => (sbyte)value;
  private static int SignExtend16(int value) => (short)value;
}
