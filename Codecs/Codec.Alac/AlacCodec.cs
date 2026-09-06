#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Alac;

/// <summary>
/// ALAC (Apple Lossless) codec — encoder and decoder implemented from Apple's
/// open-sourced reference (<c>ALACDecoder.cpp</c>/<c>ALACEncoder.cpp</c> with the
/// <c>dp_*</c> dynamic predictor, <c>ag_*</c> adaptive Golomb/Rice coder and
/// <c>matrix_*</c> inter-channel decorrelation). A coded ALAC stream is a sequence of
/// self-delimiting frames; each frame is a chain of audio elements (a single channel
/// element <c>SCE</c>, a channel-pair element <c>CPE</c>, fill/data elements that are
/// skipped, terminated by an <c>END</c> tag) packed MSB-first big-endian and then
/// byte-aligned.
/// <para>
/// Element layout, in the order the bits arrive: a 4-bit instance tag, 12 zero bits, a
/// 1-bit partial-frame flag, a 2-bit "bytes shifted" count, a 1-bit escape flag, and —
/// only when the frame is partial — an explicit 32-bit sample count. A compressed
/// element then carries <c>mixBits</c>/<c>mixRes</c> (present for mono too, where they
/// are zero), followed by <em>all</em> of the per-channel prediction headers, then the
/// shifted-off low bytes interleaved across channels, and only then the residual
/// blocks. Getting that order wrong desynchronises the bit cursor and the frame decodes
/// as noise or not at all.
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

  // Encoder settings; these mirror the reference's defaults.
  private const int DenShiftDefault = 9;  // DENSHIFT_DEFAULT
  private const int PredictorOrder = 4;   // kMinUV
  private const int PbFactor = 4;         // pb scale numerator, denominator 4 → pb unchanged
  private const int MixBitsDefault = 2;   // kDefaultMixBits
  private const byte Pb0 = 40;
  private const byte Mb0 = 10;
  private const byte Kb0 = 14;
  private const ushort MaxRunDefault = 255;

  // Real streams use 4096; this is only here so a corrupt cookie cannot ask for a
  // multi-gigabyte frame buffer.
  private const uint MaxFrameLength = 1 << 20;

  /// <summary>
  /// Where each coded channel belongs in the interleaved output. The reference decoder
  /// emits channels in bitstream order and leaves the mapping to the caller; the orders
  /// are spelled out beside the layout tags in <c>ALACAudioTypes.h</c> — "C L R" for
  /// three, "C L R Ls Rs LFE" for six, and so on — while every common container expects
  /// "L R C LFE Ls Rs". Indexed by channel count, then by coded channel.
  /// </summary>
  private static readonly byte[][] ChannelPositions = [
    [0],
    [0, 1],
    [2, 0, 1],
    [2, 0, 1, 3],
    [2, 0, 1, 3, 4],
    [2, 0, 1, 4, 5, 3],
    [2, 0, 1, 4, 5, 6, 3],
    [2, 6, 7, 0, 1, 4, 5, 3],
  ];

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
    var bytesPerSample = OutputBytesPerSample(cookie.BitDepth);
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
  /// Returns the byte length of the ALAC frame starting at <paramref name="frame"/>. Frames are
  /// self-delimiting but only bit-wise, so a container that has to store one packet per frame
  /// (MP4 <c>stsz</c>, CAF <c>pakt</c>) needs this to split an encoded stream.
  /// </summary>
  public static int FrameByteLength(ReadOnlySpan<byte> frame, AlacCookie cookie) {
    ArgumentNullException.ThrowIfNull(cookie);
    var data = frame.ToArray();
    var reader = new AlacBitReader(data, 0, data.Length);
    DecodeFrame(reader, cookie, out var consumedBits, out _);
    return (consumedBits + 7) / 8;
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
    if (frameLength <= 0 || frameLength > MaxFrameLength)
      throw new ArgumentOutOfRangeException(nameof(frameLength));

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    var pcm = pcmInterleaved.ToArray();
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (sample × channels) size.");

    var totalSamples = pcm.Length / frameBytes;
    using var allFrames = new MemoryStream();
    var maxFrameBytes = 0;

    // The reference carries the predictor coefficients across frames; each frame's header
    // records the state at its start, so the decoder tracks it without extra signalling.
    var coefs = new short[channels][];
    for (var ch = 0; ch < channels; ++ch)
      coefs[ch] = AlacPredictor.InitialCoefficients(PredictorOrder, DenShiftDefault);

    for (var start = 0; start < totalSamples; start += frameLength) {
      var count = Math.Min(frameLength, totalSamples - start);
      var frame = EncodeFrame(pcm, start, count, channels, bitsPerSample, frameLength, coefs);
      allFrames.Write(frame, 0, frame.Length);
      if (frame.Length > maxFrameBytes)
        maxFrameBytes = frame.Length;
    }

    var cookie = new AlacCookie(
      FrameLength: (uint)frameLength,
      CompatibleVersion: 0,
      BitDepth: (byte)bitsPerSample,
      Pb: Pb0, Mb: Mb0, Kb: Kb0,
      NumChannels: (byte)channels,
      MaxRun: MaxRunDefault,
      MaxFrameBytes: (uint)maxFrameBytes,
      AvgBitRate: 0,
      SampleRate: (uint)sampleRate);

    return (allFrames.ToArray(), cookie);
  }

  // ── Frame decode ─────────────────────────────────────────────────────────────

  private static byte[] DecodeFrame(AlacBitReader bits, AlacCookie cookie, out int consumedBits, out int numSamples) {
    var channelCount = cookie.NumChannels;
    if (channelCount < 1)
      throw new InvalidDataException("ALAC cookie declares no channels.");

    if (cookie.FrameLength is 0 or > MaxFrameLength)
      throw new InvalidDataException($"ALAC cookie declares an unusable frame length of {cookie.FrameLength}.");

    var channels = new int[channelCount][];
    var channelIndex = 0;
    numSamples = 0;

    for (var done = false; !done;) {
      if (bits.Exhausted)
        break;

      var tag = (int)bits.Read(3);
      switch (tag) {
        case IdEnd:
          done = true;
          break;

        case IdFil:
          SkipFillElement(bits);
          break;

        case IdDse:
          SkipDataStreamElement(bits);
          break;

        case IdSce:
        case IdLfe:
        case IdCpe: {
          var elementChannels = tag == IdCpe ? 2 : 1;
          if (channelIndex + elementChannels > channelCount)
            throw new InvalidDataException("ALAC frame carries more channels than the cookie declares.");
          numSamples = DecodeChannelElement(bits, cookie, elementChannels, channels, channelIndex);
          channelIndex += elementChannels;
          break;
        }

        case IdCce:
        case IdPce:
        default:
          throw new InvalidDataException($"ALAC element type {tag} is not part of the format.");
      }
    }

    bits.ByteAlign();
    consumedBits = bits.Position;

    if (numSamples == 0)
      return [];

    // Channels the frame never supplied stay silent, as the reference does.
    for (var ch = 0; ch < channelCount; ++ch)
      channels[ch] ??= new int[numSamples];

    var bytesPerSample = OutputBytesPerSample(cookie.BitDepth);
    var justify = cookie.BitDepth == 20 ? 4 : 0; // 20-bit output is left-justified in 3 bytes
    var positions = channelCount <= ChannelPositions.Length ? ChannelPositions[channelCount - 1] : null;
    var outBytes = new byte[numSamples * channelCount * bytesPerSample];
    for (var ch = 0; ch < channelCount; ++ch) {
      var target = positions?[ch] ?? ch;
      var source = channels[ch];
      for (var s = 0; s < numSamples; ++s)
        WriteSampleLe(outBytes, (s * channelCount + target) * bytesPerSample, source[s] << justify, bytesPerSample);
    }
    return outBytes;
  }

  // Decodes one SCE/LFE (1 channel) or CPE (2 channels) element into channels[firstChannel..].
  private static int DecodeChannelElement(
      AlacBitReader bits, AlacCookie cookie, int elementChannels, int[][] channels, int firstChannel) {
    bits.Read(4);                        // element instance tag
    if (bits.Read(12) != 0)
      throw new InvalidDataException("ALAC element header reserved bits are not zero.");

    var header = (int)bits.Read(4);
    var partialFrame = header >> 3;
    var bytesShifted = (header >> 1) & 3;
    var escape = header & 1;
    if (bytesShifted == 3)
      throw new InvalidDataException("ALAC element declares an invalid shift width.");

    var chanBits = cookie.BitDepth - bytesShifted * 8 + (elementChannels - 1);
    var numSamples = partialFrame != 0 ? (int)bits.Read(32) : (int)cookie.FrameLength;

    // A partial frame is the tail of a stream, so it can never hold more than a whole one.
    // Bounding it here keeps a corrupt count from turning into a huge allocation.
    if (numSamples <= 0 || numSamples > cookie.FrameLength)
      throw new InvalidDataException($"ALAC element declares {numSamples} samples in a frame of {cookie.FrameLength}.");

    var mixed = new int[elementChannels][];
    for (var ch = 0; ch < elementChannels; ++ch)
      mixed[ch] = new int[numSamples];

    int mixBits = 0, mixRes = 0;
    AlacBitReader? shiftBits = null;

    if (escape == 0) {
      mixBits = (int)bits.Read(8);
      mixRes = (sbyte)bits.Read(8);

      // Every channel's prediction header comes before any channel's residuals.
      var modes = new int[elementChannels];
      var denShifts = new int[elementChannels];
      var pbFactors = new int[elementChannels];
      var coefs = new short[elementChannels][];
      for (var ch = 0; ch < elementChannels; ++ch) {
        var modeByte = (int)bits.Read(8);
        modes[ch] = modeByte >> 4;
        denShifts[ch] = modeByte & 0xF;

        var filterByte = (int)bits.Read(8);
        pbFactors[ch] = filterByte >> 5;
        var order = filterByte & 0x1F;

        coefs[ch] = new short[order];
        for (var i = 0; i < order; ++i)
          coefs[ch][i] = (short)bits.Read(16);
      }

      // The shifted-off low bytes sit between the headers and the residuals.
      if (bytesShifted != 0) {
        shiftBits = bits.Clone();
        bits.Advance(bytesShifted * 8 * elementChannels * numSamples);
      }

      var residuals = new int[numSamples];
      for (var ch = 0; ch < elementChannels; ++ch) {
        var pb = cookie.Pb * pbFactors[ch] / 4;
        AlacRice.Decode(bits, residuals, numSamples, pb, cookie.Mb, cookie.Kb, chanBits);

        if (modes[ch] != 0)
          // The "numActive == 31" pre-pass runs in place, then the real filter.
          AlacPredictor.Decompress(residuals, residuals, numSamples, [], 31, chanBits, 0);

        AlacPredictor.Decompress(
          residuals, mixed[ch], numSamples, coefs[ch], coefs[ch].Length, chanBits, denShifts[ch]);
      }
    } else {
      // Uncompressed: raw samples, channel-interleaved. A pair stores the full bit depth.
      if (elementChannels == 2)
        chanBits = cookie.BitDepth;
      var extend = 32 - chanBits;
      for (var s = 0; s < numSamples; ++s)
        for (var ch = 0; ch < elementChannels; ++ch)
          mixed[ch][s] = (int)bits.Read(chanBits) << extend >> extend;

      mixBits = mixRes = 0;
      bytesShifted = 0;
    }

    var shifted = new int[elementChannels][];
    if (bytesShifted != 0) {
      var shift = bytesShifted * 8;
      for (var ch = 0; ch < elementChannels; ++ch)
        shifted[ch] = new int[numSamples];
      for (var s = 0; s < numSamples; ++s)
        for (var ch = 0; ch < elementChannels; ++ch)
          shifted[ch][s] = (int)shiftBits!.Read(shift);
    }

    var outputs = new int[elementChannels][];
    if (elementChannels == 2) {
      outputs[0] = new int[numSamples];
      outputs[1] = new int[numSamples];
      AlacMatrix.Unmix(mixed[0], mixed[1], outputs[0], outputs[1], numSamples, mixBits, mixRes);
    } else {
      outputs[0] = mixed[0];
    }

    if (bytesShifted != 0) {
      var shift = bytesShifted * 8;
      for (var ch = 0; ch < elementChannels; ++ch)
        for (var s = 0; s < numSamples; ++s)
          outputs[ch][s] = (outputs[ch][s] << shift) | shifted[ch][s];
    }

    for (var ch = 0; ch < elementChannels; ++ch)
      channels[firstChannel + ch] = outputs[ch];

    return numSamples;
  }

  // 4-bit count, extended by an 8-bit count (minus one) when it saturates.
  private static void SkipFillElement(AlacBitReader bits) {
    var count = (int)bits.Read(4);
    if (count == 15)
      count += (int)bits.Read(8) - 1;
    bits.Advance(count * 8);
  }

  private static void SkipDataStreamElement(AlacBitReader bits) {
    bits.Read(4);                          // element instance tag
    var alignFlag = bits.ReadOne();
    var count = (int)bits.Read(8);
    if (count == 255)
      count += (int)bits.Read(8);
    if (alignFlag != 0)
      bits.ByteAlign();
    bits.Advance(count * 8);
  }

  // ── Frame encode ─────────────────────────────────────────────────────────────

  private static byte[] EncodeFrame(
      byte[] pcm, int startSample, int count, int channels, int bitsPerSample, int frameLength, short[][] coefState) {
    var partialFrame = count != frameLength ? 1 : 0;
    var bytesShifted = bitsPerSample >= 24 ? 1 : 0;
    var shift = bytesShifted * 8;
    var mask = (1 << shift) - 1;
    var chanBits = bitsPerSample - shift + (channels - 1);

    // Pull the frame's samples out of the interleaved buffer, splitting off the low
    // byte(s) that will be stored raw.
    var source = new int[channels][];
    var shifted = new int[channels][];
    for (var ch = 0; ch < channels; ++ch) {
      source[ch] = new int[count];
      shifted[ch] = new int[count];
    }

    var sampleBytes = bitsPerSample / 8;
    for (var s = 0; s < count; ++s)
      for (var ch = 0; ch < channels; ++ch) {
        var offset = (startSample + s) * channels * sampleBytes + ch * sampleBytes;
        var value = ReadSampleLe(pcm, offset, sampleBytes);
        if (bytesShifted == 0) {
          source[ch][s] = value;
          continue;
        }
        shifted[ch][s] = value & mask;
        source[ch][s] = value >> shift;
      }

    var mixed = new int[channels][];
    for (var ch = 0; ch < channels; ++ch)
      mixed[ch] = new int[count];

    var mixBits = channels == 2 ? MixBitsDefault : 0;
    var mixRes = channels == 2 ? ChooseMixRes(source[0], source[1], count, mixBits) : 0;
    if (channels == 2)
      AlacMatrix.Mix(source[0], source[1], mixed[0], mixed[1], count, mixBits, mixRes);
    else
      Array.Copy(source[0], mixed[0], count);

    var writer = new AlacBitWriter();
    writer.Write((uint)(channels == 2 ? IdCpe : IdSce), 3);
    writer.Write(0, 4);                                                // element instance tag
    writer.Write(0, 12);                                               // reserved
    writer.Write((uint)((partialFrame << 3) | (bytesShifted << 1)), 4); // escape flag = 0
    if (partialFrame != 0)
      writer.Write((uint)count, 32);
    writer.Write((uint)mixBits, 8);
    writer.Write((uint)(byte)(sbyte)mixRes, 8);

    for (var ch = 0; ch < channels; ++ch) {
      writer.Write((0u << 4) | DenShiftDefault, 8);                    // mode 0
      writer.Write((PbFactor << 5) | (uint)PredictorOrder, 8);
      for (var i = 0; i < PredictorOrder; ++i)
        writer.Write((uint)(ushort)coefState[ch][i], 16);
    }

    if (bytesShifted != 0)
      for (var s = 0; s < count; ++s)
        for (var ch = 0; ch < channels; ++ch)
          writer.Write((uint)shifted[ch][s], shift);

    var residuals = new int[count];
    for (var ch = 0; ch < channels; ++ch) {
      AlacPredictor.Compress(
        mixed[ch], residuals, count, coefState[ch], PredictorOrder, chanBits, DenShiftDefault);
      AlacRice.Encode(writer, residuals, count, Pb0 * PbFactor / 4, Mb0, Kb0, chanBits);
    }

    writer.Write(IdEnd, 3);
    var compressed = writer.ToArray();

    // The reference falls back to an uncompressed frame whenever compression made it
    // bigger; without that, a pathological frame could exceed maxFrameBytes.
    var escape = EncodeEscapeFrame(source, shifted, count, channels, bitsPerSample, bytesShifted, partialFrame);
    return escape.Length < compressed.Length ? escape : compressed;
  }

  // Picks the decorrelation weight that minimises the coded magnitude, the cheap
  // stand-in for the reference's full trial encode of every candidate.
  private static int ChooseMixRes(int[] left, int[] right, int count, int mixBits) {
    var best = 0;
    var bestCost = long.MaxValue;
    var u = new int[count];
    var v = new int[count];

    for (var candidate = 0; candidate <= 1 << mixBits; ++candidate) {
      AlacMatrix.Mix(left, right, u, v, count, mixBits, candidate);
      var cost = 0L;
      for (var i = 0; i < count; ++i)
        cost += Math.Abs((long)u[i]) + Math.Abs((long)v[i]);
      if (cost >= bestCost)
        continue;
      bestCost = cost;
      best = candidate;
    }

    return best;
  }

  private static byte[] EncodeEscapeFrame(
      int[][] source, int[][] shifted, int count, int channels, int bitsPerSample, int bytesShifted, int partialFrame) {
    var writer = new AlacBitWriter();
    writer.Write((uint)(channels == 2 ? IdCpe : IdSce), 3);
    writer.Write(0, 4);
    writer.Write(0, 12);
    writer.Write((uint)((partialFrame << 3) | 1), 4);                  // escape flag = 1
    if (partialFrame != 0)
      writer.Write((uint)count, 32);

    var shift = bytesShifted * 8;
    for (var s = 0; s < count; ++s)
      for (var ch = 0; ch < channels; ++ch) {
        var value = bytesShifted == 0 ? source[ch][s] : (source[ch][s] << shift) | shifted[ch][s];
        writer.Write((uint)value, bitsPerSample);
      }

    writer.Write(IdEnd, 3);
    return writer.ToArray();
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────

  private static int OutputBytesPerSample(int bitDepth) => bitDepth switch {
    16 => 2,
    20 or 24 => 3,
    32 => 4,
    _ => throw new NotSupportedException($"ALAC bit depth {bitDepth} is not supported."),
  };

  private static int ReadSampleLe(byte[] data, int offset, int bytes) => bytes switch {
    2 => (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)),
    3 => (data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16)) << 8 >> 8,
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
      case 4:
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(bytes));
    }
  }
}
