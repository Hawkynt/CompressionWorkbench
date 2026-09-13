#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Alac;

/// <summary>
/// Full-profile Apple Lossless encoder. Unlike the historical <see cref="AlacCodec.Encode"/>
/// convenience path this implementation covers every source shape supported by the Apple
/// codec: 16/20/24/32-bit signed integer PCM, one through eight channels, and packet sizes
/// through 16,384 sample frames.
/// </summary>
/// <remarks>
/// The framing follows Apple's published ALAC encoder: multichannel packets are chains of
/// SCE/CPE elements in the codec-defined channel order, 24-bit samples shift one low byte
/// out of the predictor and 32-bit samples shift two, and each element independently falls
/// back to an escape payload when prediction would expand it.
/// </remarks>
public static class AlacEncoder {
  private const int IdSce = 0;
  private const int IdCpe = 1;
  private const int IdEnd = 7;

  private const int DenShiftDefault = 9;
  private const int PredictorOrder = 4;
  private const int PbFactor = 4;
  private const int MixBitsDefault = 2;
  private const byte Pb0 = 40;
  private const byte Mb0 = 10;
  private const byte Kb0 = 14;
  private const ushort MaxRunDefault = 255;
  private const int DefaultFrameLength = 4096;
  private const int MaxFrameLength = 16_384;

  // Apple bitstream order -> conventional interleaved order used elsewhere in this repo.
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

  // Apple's sChannelMaps, expanded into element order. An element consumes one channel
  // for SCE and two for CPE.
  private static readonly byte[][] ChannelElements = [
    [IdSce],
    [IdCpe],
    [IdSce, IdCpe],
    [IdSce, IdCpe, IdSce],
    [IdSce, IdCpe, IdCpe],
    [IdSce, IdCpe, IdCpe, IdSce],
    [IdSce, IdCpe, IdCpe, IdSce, IdSce],
    [IdSce, IdCpe, IdCpe, IdCpe, IdSce],
  ];

  /// <summary>Encodes canonical interleaved little-endian PCM to ALAC packets.</summary>
  public static (byte[] Frames, AlacCookie Cookie) Encode(
      ReadOnlySpan<byte> pcmInterleaved,
      int channels,
      int sampleRate,
      int bitsPerSample,
      int frameLength = DefaultFrameLength) {
    if (channels is < 1 or > 8)
      throw new ArgumentOutOfRangeException(nameof(channels), "ALAC supports one through eight channels.");
    if (sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(sampleRate), "ALAC sample rate must be positive.");
    if (bitsPerSample is not (16 or 20 or 24 or 32))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "ALAC supports 16, 20, 24 or 32-bit PCM.");
    if (frameLength is < 1 or > MaxFrameLength)
      throw new ArgumentOutOfRangeException(nameof(frameLength), "ALAC packet length must be 1..16384 sample frames.");

    var bytesPerSample = BytesPerSample(bitsPerSample);
    var frameBytes = checked(bytesPerSample * channels);
    if (pcmInterleaved.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not aligned to the sample × channel frame size.", nameof(pcmInterleaved));
    ValidateSamplePacking(pcmInterleaved, bitsPerSample);

    var pcm = pcmInterleaved.ToArray();
    var totalSamples = pcm.Length / frameBytes;
    using var allFrames = new MemoryStream();
    var maxFrameBytes = 0;

    var coefs = new short[channels][];
    for (var channel = 0; channel < channels; ++channel)
      coefs[channel] = AlacPredictor.InitialCoefficients(PredictorOrder, DenShiftDefault);

    for (var start = 0; start < totalSamples; start += frameLength) {
      var count = Math.Min(frameLength, totalSamples - start);
      var frame = EncodeFrame(pcm, start, count, channels, bitsPerSample, frameLength, coefs);
      allFrames.Write(frame);
      maxFrameBytes = Math.Max(maxFrameBytes, frame.Length);
    }

    var frames = allFrames.ToArray();
    var averageBitRate = totalSamples == 0
      ? 0u
      : checked((uint)Math.Min(uint.MaxValue,
        (ulong)frames.Length * 8UL * (ulong)sampleRate / (ulong)totalSamples));

    return (frames, new AlacCookie(
      FrameLength: checked((uint)frameLength),
      CompatibleVersion: 0,
      BitDepth: checked((byte)bitsPerSample),
      Pb: Pb0,
      Mb: Mb0,
      Kb: Kb0,
      NumChannels: checked((byte)channels),
      MaxRun: MaxRunDefault,
      MaxFrameBytes: checked((uint)maxFrameBytes),
      AvgBitRate: averageBitRate,
      SampleRate: checked((uint)sampleRate)));
  }

  private static void ValidateSamplePacking(ReadOnlySpan<byte> pcmInterleaved, int bitsPerSample) {
    if (bitsPerSample != 20)
      return;

    // ALAC's 20-bit PCM convention is a signed 20-bit sample left-justified in a
    // three-byte little-endian container. Silently shifting arbitrary 24-bit input by
    // four would discard information, so reject non-canonical packing instead.
    for (var offset = 0; offset < pcmInterleaved.Length; offset += 3)
      if ((pcmInterleaved[offset] & 0x0F) != 0)
        throw new ArgumentException(
          "20-bit ALAC PCM must be left-justified in 24-bit containers; the low four bits of every sample must be zero.",
          nameof(pcmInterleaved));
  }

  private static byte[] EncodeFrame(
      byte[] pcm,
      int startSample,
      int count,
      int channels,
      int bitsPerSample,
      int frameLength,
      short[][] coefficientState) {
    var partialFrame = count != frameLength;
    var bytesShifted = bitsPerSample switch {
      32 => 2,
      >= 24 => 1,
      _ => 0,
    };
    var shift = bytesShifted * 8;
    var mask = shift == 0 ? 0 : (1 << shift) - 1;

    var source = new int[channels][];
    var shifted = new int[channels][];
    for (var codedChannel = 0; codedChannel < channels; ++codedChannel) {
      source[codedChannel] = new int[count];
      shifted[codedChannel] = new int[count];
    }

    var bytesPerSample = BytesPerSample(bitsPerSample);
    var positions = ChannelPositions[channels - 1];
    for (var sample = 0; sample < count; ++sample) {
      for (var codedChannel = 0; codedChannel < channels; ++codedChannel) {
        var inputChannel = positions[codedChannel];
        var offset = checked(((startSample + sample) * channels + inputChannel) * bytesPerSample);
        var value = ReadSampleLe(pcm, offset, bitsPerSample);
        if (shift == 0) {
          source[codedChannel][sample] = value;
          continue;
        }
        shifted[codedChannel][sample] = value & mask;
        source[codedChannel][sample] = value >> shift;
      }
    }

    var packet = new AlacBitWriter();
    var firstChannel = 0;
    foreach (var tag in ChannelElements[channels - 1]) {
      var elementChannels = tag == IdCpe ? 2 : 1;
      var encoded = EncodeElement(
        tag, source, shifted, firstChannel, elementChannels, count, bitsPerSample,
        bytesShifted, partialFrame, coefficientState);
      WriteBits(packet, encoded.Data, encoded.Bits);
      firstChannel += elementChannels;
    }

    packet.Write(IdEnd, 3);
    return packet.ToArray();
  }

  private static EncodedBits EncodeElement(
      int tag,
      int[][] source,
      int[][] shifted,
      int firstChannel,
      int channels,
      int count,
      int bitsPerSample,
      int bytesShifted,
      bool partialFrame,
      short[][] coefficientState) {
    var shift = bytesShifted * 8;
    var chanBits = bitsPerSample - shift + (channels - 1);
    var mixed = new int[channels][];
    for (var channel = 0; channel < channels; ++channel)
      mixed[channel] = new int[count];

    var mixBits = channels == 2 ? MixBitsDefault : 0;
    var mixRes = channels == 2
      ? ChooseMixRes(source[firstChannel], source[firstChannel + 1], count, mixBits)
      : 0;
    if (channels == 2)
      AlacMatrix.Mix(source[firstChannel], source[firstChannel + 1], mixed[0], mixed[1], count, mixBits, mixRes);
    else
      Array.Copy(source[firstChannel], mixed[0], count);

    var compressed = new AlacBitWriter();
    WriteElementHeader(compressed, tag, partialFrame, bytesShifted, escape: false, count);
    compressed.Write(checked((uint)mixBits), 8);
    compressed.Write((uint)(byte)(sbyte)mixRes, 8);

    for (var channel = 0; channel < channels; ++channel) {
      compressed.Write(DenShiftDefault, 8); // mode = 0, low nibble = denShift
      compressed.Write((PbFactor << 5) | PredictorOrder, 8);
      var state = coefficientState[firstChannel + channel];
      for (var coefficient = 0; coefficient < PredictorOrder; ++coefficient)
        compressed.Write((uint)(ushort)state[coefficient], 16);
    }

    if (shift != 0)
      for (var sample = 0; sample < count; ++sample)
        for (var channel = 0; channel < channels; ++channel)
          compressed.Write(checked((uint)shifted[firstChannel + channel][sample]), shift);

    var residuals = new int[count];
    for (var channel = 0; channel < channels; ++channel) {
      AlacPredictor.Compress(
        mixed[channel], residuals, count, coefficientState[firstChannel + channel],
        PredictorOrder, chanBits, DenShiftDefault);
      AlacRice.Encode(compressed, residuals, count, Pb0 * PbFactor / 4, Mb0, Kb0, chanBits);
    }

    var escape = new AlacBitWriter();
    WriteElementHeader(escape, tag, partialFrame, bytesShifted: 0, escape: true, count);
    for (var sample = 0; sample < count; ++sample) {
      for (var channel = 0; channel < channels; ++channel) {
        var coded = firstChannel + channel;
        var value = shift == 0
          ? source[coded][sample]
          : (source[coded][sample] << shift) | shifted[coded][sample];
        escape.Write((uint)value, bitsPerSample);
      }
    }

    return escape.Position < compressed.Position
      ? new EncodedBits(escape.ToArray(), escape.Position)
      : new EncodedBits(compressed.ToArray(), compressed.Position);
  }

  private static void WriteElementHeader(
      AlacBitWriter writer,
      int tag,
      bool partialFrame,
      int bytesShifted,
      bool escape,
      int count) {
    writer.Write(checked((uint)tag), 3);
    writer.Write(0, 4); // element instance tag
    writer.Write(0, 12); // reserved
    var flags = (partialFrame ? 8 : 0) | (bytesShifted << 1) | (escape ? 1 : 0);
    writer.Write(checked((uint)flags), 4);
    if (partialFrame)
      writer.Write(checked((uint)count), 32);
  }

  private static void WriteBits(AlacBitWriter target, ReadOnlySpan<byte> data, int bitCount) {
    if (bitCount < 0 || bitCount > data.Length * 8)
      throw new ArgumentOutOfRangeException(nameof(bitCount));
    var completeBytes = bitCount >> 3;
    for (var i = 0; i < completeBytes; ++i)
      target.Write(data[i], 8);
    var remaining = bitCount & 7;
    if (remaining != 0)
      target.Write((uint)(data[completeBytes] >> (8 - remaining)), remaining);
  }

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

  private static int BytesPerSample(int bitsPerSample) => (bitsPerSample + 7) / 8;

  private static int ReadSampleLe(byte[] data, int offset, int bitsPerSample) => bitsPerSample switch {
    16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
    20 => ReadSigned24(data, offset) >> 4,
    24 => ReadSigned24(data, offset),
    32 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)),
    _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample)),
  };

  private static int ReadSigned24(byte[] data, int offset)
    => (data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8;

  private readonly record struct EncodedBits(byte[] Data, int Bits);
}
