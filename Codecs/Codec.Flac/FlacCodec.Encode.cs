#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Flac;

/// <summary>Subframe strategy used by the FLAC encoder.</summary>
public enum FlacSubframeMode {
  /// <summary>Selects constant, verbatim, or the cheapest fixed predictor (orders 0-4) per channel/block.</summary>
  Auto,
  Verbatim,
  Fixed0,
  Fixed1,
  Fixed2,
  Fixed3,
  Fixed4,
}

/// <summary>Stereo channel-assignment strategy used by the FLAC encoder.</summary>
public enum FlacStereoMode {
  Auto,
  Independent,
  LeftSide,
  RightSide,
  MidSide,
}

/// <summary>
/// Parameters for native FLAC encoding. Samples are integer PCM values in the declared
/// bit depth; the encoder does not silently rescale them.
/// </summary>
public sealed record FlacEncoderOptions(
  int SampleRate,
  int Channels,
  int BitsPerSample = 16,
  int BlockSize = 4096,
  FlacSubframeMode Compression = FlacSubframeMode.Auto,
  FlacStereoMode StereoMode = FlacStereoMode.Auto
);

public static partial class FlacCodec {

  /// <summary>
  /// Encodes interleaved signed integer PCM to a complete native FLAC stream. The generated
  /// stream uses fixed-block framing, supports 1-8 channels, 4-32 bit samples, constant /
  /// verbatim / fixed predictors 0-4 with Rice coding, and all standard stereo decorrelation
  /// assignments understood by the decoder.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<int> interleaved, FlacEncoderOptions options) {
    ValidateEncoderOptions(interleaved.Length, options);
    var frames = interleaved.Length / options.Channels;
    if ((long)frames > 0xFFFFFFFFFL)
      throw new ArgumentOutOfRangeException(nameof(interleaved), "FLAC STREAMINFO stores at most 36 bits of total-sample count.");

    ValidateSampleRange(interleaved, options.BitsPerSample);

    using var output = new MemoryStream();
    output.Write("fLaC"u8);
    WriteStreamInfo(output, options, frames);

    var frameNumber = 0UL;
    for (var baseFrame = 0; baseFrame < frames; baseFrame += options.BlockSize) {
      var blockSize = Math.Min(options.BlockSize, frames - baseFrame);
      var frame = EncodeFrame(interleaved, baseFrame, blockSize, frameNumber++, options);
      output.Write(frame);
    }

    return output.ToArray();
  }

  /// <summary>Convenience overload for the common 16-bit PCM case.</summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int sampleRate, int channels,
    int blockSize = 4096, FlacSubframeMode compression = FlacSubframeMode.Auto,
    FlacStereoMode stereoMode = FlacStereoMode.Auto) {
    var pcm = new int[interleaved.Length];
    for (var i = 0; i < interleaved.Length; ++i)
      pcm[i] = interleaved[i];
    return Encode(pcm, new FlacEncoderOptions(sampleRate, channels, 16, blockSize, compression, stereoMode));
  }

  private static void ValidateEncoderOptions(int sampleCount, FlacEncoderOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.SampleRate is < 1 or > 1_048_575)
      throw new ArgumentOutOfRangeException(nameof(options), "FLAC sample rate must fit the 20-bit STREAMINFO field.");
    if (options.Channels is < 1 or > 8)
      throw new ArgumentOutOfRangeException(nameof(options), "FLAC supports 1-8 channels.");
    if (options.BitsPerSample is < 4 or > 32)
      throw new ArgumentOutOfRangeException(nameof(options), "FLAC supports integer sample widths from 4 through 32 bits.");
    if (options.BlockSize is < 1 or > 65_535)
      throw new ArgumentOutOfRangeException(nameof(options), "This encoder uses the explicit 16-bit FLAC block-size code (1-65535 samples). ");
    if (sampleCount % options.Channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.");
    if (options.Channels != 2 && options.StereoMode is not FlacStereoMode.Auto and not FlacStereoMode.Independent)
      throw new ArgumentException("FLAC stereo decorrelation modes require exactly two channels.", nameof(options));
    if (options.BitsPerSample == 32 && options.StereoMode is not FlacStereoMode.Auto and not FlacStereoMode.Independent)
      throw new ArgumentException("32-bit stereo must use independent coding because a side channel can require 33 bits.", nameof(options));
  }

  private static void ValidateSampleRange(ReadOnlySpan<int> samples, int bits) {
    if (bits == 32)
      return;
    var min = -(1L << (bits - 1));
    var max = (1L << (bits - 1)) - 1;
    foreach (var sample in samples)
      if (sample < min || sample > max)
        throw new ArgumentOutOfRangeException(nameof(samples), $"PCM sample {sample} does not fit signed {bits}-bit PCM.");
  }

  private static void WriteStreamInfo(Stream output, FlacEncoderOptions options, int totalFrames) {
    output.WriteByte(0x80); // last metadata block + STREAMINFO
    output.WriteByte(0);
    output.WriteByte(0);
    output.WriteByte(34);

    Span<byte> info = stackalloc byte[34];
    BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)Math.Min(options.BlockSize, ushort.MaxValue));
    BinaryPrimitives.WriteUInt16BigEndian(info[2..], (ushort)Math.Min(options.BlockSize, ushort.MaxValue));
    // min/max frame byte sizes are optional and may be zero when unknown.

    var packed = ((ulong)options.SampleRate << 44)
                 | ((ulong)(options.Channels - 1) << 41)
                 | ((ulong)(options.BitsPerSample - 1) << 36)
                 | (uint)totalFrames;
    for (var i = 0; i < 8; ++i)
      info[10 + i] = (byte)(packed >> (56 - i * 8));
    // MD5 is deliberately zero: RFC FLAC defines all-zero as "MD5 not computed".
    output.Write(info);
  }

  private static byte[] EncodeFrame(ReadOnlySpan<int> interleaved, int baseFrame, int blockSize,
    ulong frameNumber, FlacEncoderOptions options) {
    var channelMode = SelectChannelMode(interleaved, baseFrame, blockSize, options);
    var channelAssignment = channelMode switch {
      FlacStereoMode.LeftSide => 8,
      FlacStereoMode.RightSide => 9,
      FlacStereoMode.MidSide => 10,
      _ => options.Channels - 1,
    };

    var writer = new EncoderBitWriter();
    writer.WriteBits(0x3FFE, 14);
    writer.WriteBits(0, 1); // reserved
    writer.WriteBits(0, 1); // fixed-block strategy
    writer.WriteBits(7, 4); // 16-bit explicit block size follows
    writer.WriteBits(0, 4); // sample rate from STREAMINFO
    writer.WriteBits((ulong)channelAssignment, 4);
    writer.WriteBits(0, 3); // sample width from STREAMINFO
    writer.WriteBits(0, 1);
    WriteUtf8Number(writer, frameNumber);
    writer.WriteBits((ulong)(blockSize - 1), 16);
    writer.AlignToByte();

    var headerBytes = writer.ToArray();
    writer.WriteBits(Crc8(headerBytes), 8);

    var transformed = BuildChannels(interleaved, baseFrame, blockSize, options.Channels, channelMode);
    for (var channel = 0; channel < transformed.Length; ++channel) {
      var bps = options.BitsPerSample;
      if (channelMode == FlacStereoMode.LeftSide && channel == 1) ++bps;
      else if (channelMode == FlacStereoMode.RightSide && channel == 0) ++bps;
      else if (channelMode == FlacStereoMode.MidSide && channel == 1) ++bps;
      EncodeSubframe(writer, transformed[channel], bps, options.Compression);
    }

    writer.AlignToByte();
    var withoutCrc = writer.ToArray();
    writer.WriteBits(Crc16(withoutCrc), 16);
    return writer.ToArray();
  }

  private static FlacStereoMode SelectChannelMode(ReadOnlySpan<int> interleaved, int baseFrame,
    int blockSize, FlacEncoderOptions options) {
    if (options.Channels != 2 || options.BitsPerSample == 32)
      return FlacStereoMode.Independent;
    if (options.StereoMode != FlacStereoMode.Auto)
      return options.StereoMode;

    var bestMode = FlacStereoMode.Independent;
    var bestCost = long.MaxValue;
    foreach (var mode in new[] { FlacStereoMode.Independent, FlacStereoMode.LeftSide, FlacStereoMode.RightSide, FlacStereoMode.MidSide }) {
      var channels = BuildChannels(interleaved, baseFrame, blockSize, 2, mode);
      long cost = 0;
      for (var c = 0; c < 2; ++c) {
        var bps = options.BitsPerSample + ((mode == FlacStereoMode.LeftSide && c == 1)
          || (mode == FlacStereoMode.RightSide && c == 0)
          || (mode == FlacStereoMode.MidSide && c == 1) ? 1 : 0);
        cost += EstimateSubframeBits(channels[c], bps, options.Compression);
      }
      if (cost >= bestCost) continue;
      bestCost = cost;
      bestMode = mode;
    }
    return bestMode;
  }

  private static int[][] BuildChannels(ReadOnlySpan<int> interleaved, int baseFrame, int blockSize,
    int channels, FlacStereoMode mode) {
    var result = new int[channels][];
    for (var c = 0; c < channels; ++c)
      result[c] = new int[blockSize];

    if (channels != 2 || mode == FlacStereoMode.Independent) {
      for (var i = 0; i < blockSize; ++i)
        for (var c = 0; c < channels; ++c)
          result[c][i] = interleaved[(baseFrame + i) * channels + c];
      return result;
    }

    for (var i = 0; i < blockSize; ++i) {
      var left = interleaved[(baseFrame + i) * 2];
      var right = interleaved[(baseFrame + i) * 2 + 1];
      switch (mode) {
        case FlacStereoMode.LeftSide:
          result[0][i] = left;
          result[1][i] = left - right;
          break;
        case FlacStereoMode.RightSide:
          result[0][i] = left - right;
          result[1][i] = right;
          break;
        case FlacStereoMode.MidSide:
          result[0][i] = (int)(((long)left + right) >> 1);
          result[1][i] = left - right;
          break;
      }
    }
    return result;
  }

  private static void EncodeSubframe(EncoderBitWriter writer, int[] samples, int bps, FlacSubframeMode requestedMode) {
    var mode = ChooseSubframeMode(samples, bps, requestedMode);
    writer.WriteBits(0, 1); // zero padding

    if (mode == -1) {
      writer.WriteBits(0, 6); // CONSTANT
      writer.WriteBits(0, 1); // no wasted bits
      writer.WriteSigned(samples[0], bps);
      return;
    }
    if (mode == -2) {
      writer.WriteBits(1, 6); // VERBATIM
      writer.WriteBits(0, 1);
      foreach (var sample in samples)
        writer.WriteSigned(sample, bps);
      return;
    }

    writer.WriteBits((ulong)(8 + mode), 6); // FIXED order 0..4
    writer.WriteBits(0, 1);
    for (var i = 0; i < mode; ++i)
      writer.WriteSigned(samples[i], bps);

    var residuals = BuildFixedResiduals(samples, mode);
    writer.WriteBits(0, 2); // Rice coding method (4-bit parameter)
    writer.WriteBits(0, 4); // partition order 0
    var rice = SelectRiceEncoding(residuals);
    if (rice.EscapeBits >= 0) {
      writer.WriteBits(15, 4);
      writer.WriteBits((ulong)rice.EscapeBits, 5);
      foreach (var residual in residuals)
        if (rice.EscapeBits != 0)
          writer.WriteSigned(residual, rice.EscapeBits);
      return;
    }

    writer.WriteBits((ulong)rice.Parameter, 4);
    foreach (var residual in residuals) {
      var folded = FoldSigned(residual);
      var quotient = folded >> rice.Parameter;
      writer.WriteUnaryZeros(quotient);
      if (rice.Parameter != 0)
        writer.WriteBits(folded & ((1UL << rice.Parameter) - 1), rice.Parameter);
    }
  }

  // -1 constant, -2 verbatim, otherwise fixed predictor order 0..4.
  private static int ChooseSubframeMode(int[] samples, int bps, FlacSubframeMode requestedMode) {
    if (samples.Length > 0 && samples.All(static (value, state) => value == state, samples[0]))
      return -1;

    if (requestedMode == FlacSubframeMode.Verbatim)
      return -2;
    if (requestedMode != FlacSubframeMode.Auto) {
      var order = (int)requestedMode - (int)FlacSubframeMode.Fixed0;
      return Math.Min(order, Math.Max(0, samples.Length - 1));
    }

    var bestMode = -2;
    var bestBits = (long)samples.Length * bps + 8;
    for (var order = 0; order <= Math.Min(4, samples.Length - 1); ++order) {
      var bits = EstimateFixedBits(samples, bps, order);
      if (bits >= bestBits) continue;
      bestBits = bits;
      bestMode = order;
    }
    return bestMode;
  }

  private static long EstimateSubframeBits(int[] samples, int bps, FlacSubframeMode mode) {
    var selected = ChooseSubframeMode(samples, bps, mode);
    if (selected == -1) return 8L + bps;
    if (selected == -2) return 8L + (long)samples.Length * bps;
    return EstimateFixedBits(samples, bps, selected);
  }

  private static long EstimateFixedBits(int[] samples, int bps, int order) {
    var residuals = BuildFixedResiduals(samples, order);
    var rice = SelectRiceEncoding(residuals);
    long body = rice.EscapeBits >= 0
      ? 9L + (long)rice.EscapeBits * residuals.Length
      : 4L + RiceDataBits(residuals, rice.Parameter);
    return 8L + (long)order * bps + 6L + body;
  }

  private static int[] BuildFixedResiduals(int[] samples, int order) {
    var residuals = new int[samples.Length - order];
    for (var i = order; i < samples.Length; ++i) {
      var prediction = order switch {
        0 => 0,
        1 => samples[i - 1],
        2 => unchecked(2 * samples[i - 1] - samples[i - 2]),
        3 => unchecked(3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3]),
        4 => unchecked(4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4]),
        _ => 0,
      };
      residuals[i - order] = unchecked(samples[i] - prediction);
    }
    return residuals;
  }

  private readonly record struct RiceEncoding(int Parameter, int EscapeBits);

  private static RiceEncoding SelectRiceEncoding(int[] residuals) {
    if (residuals.Length == 0)
      return new RiceEncoding(0, -1);

    var bestParameter = 0;
    var bestBits = long.MaxValue;
    for (var k = 0; k <= 14; ++k) {
      var bits = RiceDataBits(residuals, k);
      if (bits >= bestBits) continue;
      bestBits = bits;
      bestParameter = k;
    }

    var rawBits = SignedBitsRequired(residuals);
    if (rawBits <= 31) {
      var escapeBits = 5L + (long)rawBits * residuals.Length;
      if (escapeBits < bestBits)
        return new RiceEncoding(0, rawBits);
    }
    return new RiceEncoding(bestParameter, -1);
  }

  private static long RiceDataBits(int[] residuals, int parameter) {
    long bits = 0;
    foreach (var residual in residuals) {
      var folded = FoldSigned(residual);
      var quotient = folded >> parameter;
      if (quotient > int.MaxValue)
        return long.MaxValue;
      bits += (long)quotient + 1 + parameter;
      if (bits > int.MaxValue * 8L)
        return long.MaxValue;
    }
    return bits;
  }

  private static int SignedBitsRequired(int[] values) {
    var bits = 1;
    foreach (var value in values) {
      while (bits < 32) {
        var min = -(1L << (bits - 1));
        var max = (1L << (bits - 1)) - 1;
        if (value >= min && value <= max) break;
        ++bits;
      }
    }
    return bits;
  }

  private static ulong FoldSigned(int value)
    => ((ulong)(uint)value << 1) ^ (ulong)(uint)(value >> 31);

  private static void WriteUtf8Number(EncoderBitWriter writer, ulong value) {
    if (value < 0x80) {
      writer.WriteBits(value, 8);
      return;
    }

    var continuation = 1;
    var limit = 0x800UL;
    while (value >= limit && continuation < 6) {
      ++continuation;
      limit <<= 5;
    }
    var leadPayloadBits = 7 - continuation;
    var leadPrefix = (0xFF << (8 - continuation)) & 0xFF;
    var lead = (byte)(leadPrefix | (int)(value >> (6 * continuation) & (ulong)((1 << leadPayloadBits) - 1)));
    writer.WriteBits(lead, 8);
    for (var i = continuation - 1; i >= 0; --i)
      writer.WriteBits((byte)(0x80 | ((value >> (6 * i)) & 0x3F)), 8);
  }

  private static byte Crc8(ReadOnlySpan<byte> data) {
    byte crc = 0;
    foreach (var value in data) {
      crc ^= value;
      for (var bit = 0; bit < 8; ++bit)
        crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
    }
    return crc;
  }

  private static ushort Crc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var value in data) {
      crc ^= (ushort)(value << 8);
      for (var bit = 0; bit < 8; ++bit)
        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
    }
    return crc;
  }

  private sealed class EncoderBitWriter {
    private readonly List<byte> _bytes = [];
    private int _bitCount;
    private byte _current;

    public void WriteBits(ulong value, int count) {
      if (count is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
      for (var bit = count - 1; bit >= 0; --bit) {
        _current = (byte)((_current << 1) | ((value >> bit) & 1));
        if (++_bitCount != 8) continue;
        _bytes.Add(_current);
        _current = 0;
        _bitCount = 0;
      }
    }

    public void WriteSigned(long value, int bits) {
      if (bits is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(bits));
      var encoded = unchecked((ulong)value);
      if (bits < 64) encoded &= (1UL << bits) - 1;
      WriteBits(encoded, bits);
    }

    public void WriteUnaryZeros(ulong quotient) {
      for (ulong i = 0; i < quotient; ++i)
        WriteBits(0, 1);
      WriteBits(1, 1);
    }

    public void AlignToByte() {
      if (_bitCount == 0) return;
      _current <<= 8 - _bitCount;
      _bytes.Add(_current);
      _current = 0;
      _bitCount = 0;
    }

    public byte[] ToArray() {
      if (_bitCount == 0) return [.. _bytes];
      var result = new byte[_bytes.Count + 1];
      _bytes.CopyTo(result, 0);
      result[^1] = (byte)(_current << (8 - _bitCount));
      return result;
    }
  }
}
