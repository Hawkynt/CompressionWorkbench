#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Codec.CriAdx;

public enum AdxEncodingMode : byte {
  Fixed = 0x02,
  Standard = 0x03,
  Exponential = 0x04,
}

public enum AdxHeaderVersion : byte {
  Version3 = 3,
  Version4 = 4,
  Version5 = 5,
}

public readonly record struct AdxEncryptionKey(ushort Start, ushort Multiplier, ushort Addend, byte Revision = 8) {
  public ushort Next(ushort value) => (ushort)((value * this.Multiplier + this.Addend) & 0x7FFF);
}

public sealed record AdxEncodeOptions {
  public AdxEncodingMode Encoding { get; init; } = AdxEncodingMode.Standard;
  public AdxHeaderVersion Version { get; init; } = AdxHeaderVersion.Version3;
  public int HighpassFrequency { get; init; } = 500;
  public int? LoopStartSample { get; init; }
  public int? LoopEndSample { get; init; }
  public AdxEncryptionKey? Encryption { get; init; }
  public int HeaderAlignment { get; init; } = 1;
  public bool WriteEndMarker { get; init; }
}

public static class AdxCodec {
  public const ushort Magic = 0x8000;
  public const byte EncodingTypeFixed = 0x02;
  public const byte EncodingTypeStandard = 0x03;
  public const byte EncodingTypeExponential = 0x04;
  public const byte EncodingTypeAhx = 0x10;
  public const byte EncodingTypeAhx11 = 0x11;
  public const int FrameSize = 18;
  public const int SamplesPerFrame = 32;
  public const byte BitDepth = 4;
  public const ushort EndMarkerScale = 0x8001;
  public const int MaxChannels = 8;

  private const double Sqrt2 = 1.4142135623730951;
  private static readonly (int Coef1, int Coef2)[] FixedCoefficients = [
    (0x0000, 0x0000),
    (0x0F00, 0x0000),
    (0x1CC0, unchecked((short)0xF300)),
    (0x1880, unchecked((short)0xF240)),
  ];

  public readonly record struct AdxInfo(
    byte EncodingType, int BlockSize, int BitDepth, int Channels,
    int SampleRate, int TotalSamples, int HighpassFrequency, int Version,
    byte Flags, int DataOffset,
    int? LoopStartSample = null, int? LoopEndSample = null) {

    public byte Revision => this.Flags;
    public ushort VersionSignature => (ushort)((this.Version << 8) | this.Revision);
    public bool IsEncrypted => this.Version == 4 && this.Revision is 8 or 9;
    public bool IsStandard => this.EncodingType == EncodingTypeStandard;
    public bool IsAdxAdpcm => this.EncodingType is EncodingTypeFixed or EncodingTypeStandard or EncodingTypeExponential;
    public bool IsAhx => this.EncodingType is EncodingTypeAhx or EncodingTypeAhx11;
    public bool HasLoop => this.LoopStartSample.HasValue && this.LoopEndSample.HasValue;
  }

  public static (int Coef1, int Coef2) DeriveCoefficients(int highpassFrequency, int sampleRate) {
    if (sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (highpassFrequency < 0 || highpassFrequency >= sampleRate / 2)
      throw new ArgumentOutOfRangeException(nameof(highpassFrequency), "High-pass cutoff must be non-negative and below Nyquist.");

    var z = Math.Cos(2.0 * Math.PI * highpassFrequency / sampleRate);
    var a = Sqrt2 - z;
    var b = Sqrt2 - 1.0;
    var c = (a - Math.Sqrt((a + b) * (a - b))) / b;
    var coef1 = (int)Math.Round(c * 8192.0, MidpointRounding.AwayFromZero);
    var coef2 = (int)Math.Round(-(c * c) * 4096.0, MidpointRounding.AwayFromZero);
    return (coef1, coef2);
  }

  public static AdxInfo ReadInfo(ReadOnlySpan<byte> file) {
    if (file.Length < 20)
      throw new InvalidDataException("ADX file too short for a header.");
    var magic = BinaryPrimitives.ReadUInt16BigEndian(file);
    if (magic != Magic)
      throw new InvalidDataException($"Missing ADX magic 0x8000 (found 0x{magic:X4}).");

    var copyrightOffset = BinaryPrimitives.ReadUInt16BigEndian(file[2..]);
    var dataOffset = copyrightOffset + 4;
    if (dataOffset < 20 || dataOffset > file.Length)
      throw new InvalidDataException("ADX data offset is outside the input.");
    if (dataOffset < 6 || !file.Slice(dataOffset - 6, 6).SequenceEqual("(c)CRI"u8))
      throw new InvalidDataException("ADX copyright marker '(c)CRI' is missing at the declared data offset.");

    var encodingType = file[4];
    var blockSize = file[5];
    var bitDepth = file[6];
    var channels = file[7];
    var sampleRate = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file[8..]));
    var totalSamples = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file[12..]));
    var highpass = BinaryPrimitives.ReadUInt16BigEndian(file[16..]);
    var version = file[18];
    var revision = file[19];

    if (channels is < 1 or > MaxChannels)
      throw new InvalidDataException($"ADX header reports unsupported channel count {channels}.");
    if (sampleRate <= 0)
      throw new InvalidDataException("ADX header reports an invalid sample rate.");

    int? loopStart = null;
    int? loopEnd = null;
    if (encodingType is EncodingTypeFixed or EncodingTypeStandard or EncodingTypeExponential) {
      var loopOffset = version switch {
        3 => 0x14,
        4 => 0x18 + Math.Max(8, channels * 4),
        _ => -1,
      };
      if (loopOffset >= 0 && dataOffset - 6 >= loopOffset + 0x18) {
        var loopFlag = BinaryPrimitives.ReadInt32BigEndian(file[loopOffset + 4..]);
        if (loopFlag != 0) {
          loopStart = BinaryPrimitives.ReadInt32BigEndian(file[loopOffset + 8..]);
          loopEnd = BinaryPrimitives.ReadInt32BigEndian(file[loopOffset + 0x10..]);
          if (loopStart < 0 || loopEnd <= loopStart || loopEnd > totalSamples)
            throw new InvalidDataException("ADX loop metadata is outside the sample range.");
        }
      }
    }

    return new AdxInfo(encodingType, blockSize, bitDepth, channels, sampleRate, totalSamples,
      highpass, version, revision, dataOffset, loopStart, loopEnd);
  }

  public static (short[] InterleavedPcm, int Channels, int SampleRate) Decode(
    ReadOnlySpan<byte> file,
    AdxEncryptionKey? encryptionKey = null) {
    var info = ReadInfo(file);
    ValidateAdpcmHeader(info);
    if (info.IsEncrypted && encryptionKey is null)
      throw new NotSupportedException("Encrypted ADX requires the derived start/multiplier/addend key.");
    if (info.IsEncrypted && encryptionKey!.Value.Revision != info.Revision)
      throw new InvalidDataException($"ADX key revision {encryptionKey.Value.Revision} does not match stream revision {info.Revision}.");

    var (coef1, coef2) = info.EncodingType == EncodingTypeFixed
      ? (0, 0)
      : DeriveCoefficients(info.HighpassFrequency, info.SampleRate);
    var histories = ReadInitialHistories(file, info);
    var hist1 = histories.Hist1;
    var hist2 = histories.Hist2;
    var pcm = new short[checked(info.TotalSamples * info.Channels)];
    var groups = info.TotalSamples == 0 ? 0 : (info.TotalSamples + SamplesPerFrame - 1) / SamplesPerFrame;
    var required = checked(info.DataOffset + groups * FrameSize * info.Channels);
    if (required > file.Length)
      throw new InvalidDataException("ADX sample data is truncated.");

    var xor = encryptionKey?.Start ?? 0;
    for (var group = 0; group < groups; ++group) {
      var samplesDone = group * SamplesPerFrame;
      var samplesThisGroup = Math.Min(SamplesPerFrame, info.TotalSamples - samplesDone);
      for (var ch = 0; ch < info.Channels; ++ch) {
        var frameStart = info.DataOffset + (group * info.Channels + ch) * FrameSize;
        var word = BinaryPrimitives.ReadUInt16BigEndian(file[frameStart..]);
        if (!info.IsEncrypted && word == EndMarkerScale)
          continue;

        var frameCoef1 = coef1;
        var frameCoef2 = coef2;
        int scale;
        switch (info.EncodingType) {
          case EncodingTypeFixed: {
            var predictor = word >> 13;
            if (predictor >= FixedCoefficients.Length)
              throw new InvalidDataException($"ADX fixed-predictor index {predictor} is invalid.");
            (frameCoef1, frameCoef2) = FixedCoefficients[predictor];
            scale = (word & 0x1FFF) + 1;
            break;
          }
          case EncodingTypeStandard:
            scale = info.IsEncrypted ? ((word ^ xor) & 0x1FFF) + 1 : (word & 0x7FFF) + 1;
            break;
          case EncodingTypeExponential:
            if (word > 12)
              throw new InvalidDataException($"ADX exponential scale exponent {word} is invalid.");
            scale = 1 << (12 - word);
            break;
          default:
            throw new NotSupportedException($"Unsupported ADX encoding type {info.EncodingType}.");
        }

        var h1 = hist1[ch];
        var h2 = hist2[ch];
        for (var i = 0; i < samplesThisGroup; ++i) {
          var packed = file[frameStart + 2 + (i >> 1)];
          var nibble = (i & 1) == 0 ? packed >> 4 : packed & 0x0F;
          var delta = SignExtend4(nibble);
          var prediction = info.Version == 3
            ? ((frameCoef1 * h1) >> 12) + ((frameCoef2 * h2) >> 12)
            : (frameCoef1 * h1 + frameCoef2 * h2) >> 12;
          var sample = Clamp16(delta * scale + prediction);
          pcm[(samplesDone + i) * info.Channels + ch] = (short)sample;
          h2 = h1;
          h1 = sample;
        }
        hist1[ch] = h1;
        hist2[ch] = h2;
        if (info.IsEncrypted)
          xor = encryptionKey!.Value.Next(xor);
      }
    }

    return (pcm, info.Channels, info.SampleRate);
  }

  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
    => Encode(interleaved, channels, sampleRate, new AdxEncodeOptions());

  public static byte[] Encode(
    ReadOnlySpan<short> interleaved,
    int channels,
    int sampleRate,
    AdxEncodeOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    ValidateEncodeArguments(interleaved, channels, sampleRate, options);
    var totalSamples = interleaved.Length / channels;
    var histories = BuildInitialHistories(interleaved, channels, options.Version);
    var header = BuildAdpcmHeader(channels, sampleRate, totalSamples, options, histories);
    var data = EncodeFrames(interleaved, channels, sampleRate, options, histories);
    var markerLength = options.WriteEndMarker ? FrameSize : 0;
    var file = new byte[checked(header.Length + data.Length + markerLength)];
    header.CopyTo(file, 0);
    data.CopyTo(file, header.Length);
    if (options.WriteEndMarker)
      BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(header.Length + data.Length), EndMarkerScale);
    return file;
  }

  public static byte[] BuildAhxHeader(
    int sampleRate,
    int totalSamples,
    byte encodingType = EncodingTypeAhx11,
    byte encryptionRevision = 0,
    int headerAlignment = 1) {
    if (encodingType is not (EncodingTypeAhx or EncodingTypeAhx11))
      throw new ArgumentOutOfRangeException(nameof(encodingType));
    if (sampleRate <= 0 || totalSamples < 0)
      throw new ArgumentOutOfRangeException(sampleRate <= 0 ? nameof(sampleRate) : nameof(totalSamples));
    if (encryptionRevision is not (0 or 8))
      throw new ArgumentOutOfRangeException(nameof(encryptionRevision), "AHX only documents encryption revision 8.");
    ValidateAlignment(headerAlignment);

    const int baseSize = 0x14;
    var dataOffset = Align(baseSize + 6, headerAlignment);
    if (dataOffset - 4 > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(headerAlignment));
    var header = new byte[dataOffset];
    WriteCommonHeader(header, encodingType, 0, 0, 1, sampleRate, totalSamples, 0, 6, encryptionRevision);
    "(c)CRI"u8.CopyTo(header.AsSpan(dataOffset - 6));
    return header;
  }

  public static byte[] RebuildHeader(AdxInfo info, int dataOffset) {
    if (dataOffset < 20 || dataOffset - 4 > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(dataOffset));
    var header = new byte[dataOffset];
    WriteCommonHeader(header, info.EncodingType, info.BlockSize, info.BitDepth, info.Channels,
      info.SampleRate, info.TotalSamples, info.HighpassFrequency, info.Version, info.Revision);
    "(c)CRI"u8.CopyTo(header.AsSpan(dataOffset - 6));
    return header;
  }

  private static byte[] BuildAdpcmHeader(
    int channels,
    int sampleRate,
    int totalSamples,
    AdxEncodeOptions options,
    (short[] Hist1, short[] Hist2) histories) {
    var version = (byte)options.Version;
    var histSize = version == 4 ? Math.Max(8, channels * 4) : 0;
    var loop = options.LoopStartSample.HasValue;
    var loopOffset = version switch {
      3 => 0x14,
      4 => 0x18 + histSize,
      _ => -1,
    };
    var structuralEnd = version switch {
      3 => 0x14,
      4 => 0x18 + histSize,
      5 => 0x14,
      _ => throw new ArgumentOutOfRangeException(nameof(options.Version)),
    };
    if (loop)
      structuralEnd = checked(loopOffset + 0x18);
    var dataOffset = Align(checked(structuralEnd + 6), options.HeaderAlignment);
    if (dataOffset - 4 > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options.HeaderAlignment), "ADX header offset is a 16-bit field.");

    var revision = options.Encryption?.Revision ?? 0;
    var header = new byte[dataOffset];
    WriteCommonHeader(header, (byte)options.Encoding, FrameSize, BitDepth, channels,
      sampleRate, totalSamples, options.HighpassFrequency, version, revision);

    if (version == 4) {
      for (var ch = 0; ch < channels; ++ch) {
        var offset = 0x18 + ch * 4;
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(offset), histories.Hist1[ch]);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(offset + 2), histories.Hist2[ch]);
      }
    }

    if (loop) {
      var start = options.LoopStartSample!.Value;
      var end = options.LoopEndSample!.Value;
      var frameGroup = FrameSize * channels;
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(loopOffset + 4), 1);
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(loopOffset + 8), start);
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(loopOffset + 0x0C), dataOffset + (start / SamplesPerFrame) * frameGroup);
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(loopOffset + 0x10), end);
      BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(loopOffset + 0x14), dataOffset + ((end + SamplesPerFrame - 1) / SamplesPerFrame) * frameGroup);
    }

    "(c)CRI"u8.CopyTo(header.AsSpan(dataOffset - 6));
    return header;
  }

  private static void WriteCommonHeader(
    Span<byte> header,
    byte encodingType,
    int blockSize,
    int bitDepth,
    int channels,
    int sampleRate,
    int totalSamples,
    int highpass,
    int version,
    byte revision) {
    BinaryPrimitives.WriteUInt16BigEndian(header, Magic);
    BinaryPrimitives.WriteUInt16BigEndian(header[2..], checked((ushort)(header.Length - 4)));
    header[4] = encodingType;
    header[5] = checked((byte)blockSize);
    header[6] = checked((byte)bitDepth);
    header[7] = checked((byte)channels);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(header[12..], checked((uint)totalSamples));
    BinaryPrimitives.WriteUInt16BigEndian(header[16..], checked((ushort)highpass));
    header[18] = checked((byte)version);
    header[19] = revision;
  }

  private static byte[] EncodeFrames(
    ReadOnlySpan<short> interleaved,
    int channels,
    int sampleRate,
    AdxEncodeOptions options,
    (short[] Hist1, short[] Hist2) histories) {
    var totalSamples = interleaved.Length / channels;
    var groups = totalSamples == 0 ? 0 : (totalSamples + SamplesPerFrame - 1) / SamplesPerFrame;
    var data = new byte[checked(groups * FrameSize * channels)];
    var hist1 = histories.Hist1.Select(static value => (int)value).ToArray();
    var hist2 = histories.Hist2.Select(static value => (int)value).ToArray();
    var derived = options.Encoding == AdxEncodingMode.Fixed
      ? (Coef1: 0, Coef2: 0)
      : DeriveCoefficients(options.HighpassFrequency, sampleRate);
    var xor = options.Encryption?.Start ?? 0;

    for (var group = 0; group < groups; ++group) {
      var samplesDone = group * SamplesPerFrame;
      var count = Math.Min(SamplesPerFrame, totalSamples - samplesDone);
      for (var ch = 0; ch < channels; ++ch) {
        var frameStart = (group * channels + ch) * FrameSize;
        var choice = ChooseFrame(interleaved, channels, ch, samplesDone, count, options, derived, hist1[ch], hist2[ch]);
        var word = choice.Word;
        if (options.Encryption is not null)
          word = (ushort)(word ^ xor);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(frameStart), word);

        var h1 = hist1[ch];
        var h2 = hist2[ch];
        for (var i = 0; i < SamplesPerFrame; ++i) {
          var nibble = 0;
          if (i < count) {
            var target = interleaved[(samplesDone + i) * channels + ch];
            var prediction = options.Version == AdxHeaderVersion.Version3
              ? ((choice.Coef1 * h1) >> 12) + ((choice.Coef2 * h2) >> 12)
              : (choice.Coef1 * h1 + choice.Coef2 * h2) >> 12;
            var quant = Quantize(target - prediction, choice.Scale);
            var sample = Clamp16(prediction + quant * choice.Scale);
            h2 = h1;
            h1 = sample;
            nibble = quant & 0x0F;
          }
          var byteIndex = frameStart + 2 + (i >> 1);
          if ((i & 1) == 0)
            data[byteIndex] = (byte)(nibble << 4);
          else
            data[byteIndex] |= (byte)nibble;
        }
        hist1[ch] = h1;
        hist2[ch] = h2;
        if (options.Encryption is { } encryption)
          xor = encryption.Next(xor);
      }
    }
    return data;
  }

  private readonly record struct FrameChoice(ushort Word, int Scale, int Coef1, int Coef2, long Error);

  private static FrameChoice ChooseFrame(
    ReadOnlySpan<short> samples,
    int channels,
    int channel,
    int firstSample,
    int count,
    AdxEncodeOptions options,
    (int Coef1, int Coef2) derived,
    int initialH1,
    int initialH2) {
    if (options.Encoding == AdxEncodingMode.Fixed) {
      FrameChoice? best = null;
      for (var predictor = 0; predictor < FixedCoefficients.Length; ++predictor) {
        var (c1, c2) = FixedCoefficients[predictor];
        var scale = ChooseLinearScale(samples, channels, channel, firstSample, count, c1, c2, initialH1, initialH2, options.Version);
        scale = Math.Min(scale, 0x2000);
        var word = (ushort)((predictor << 13) | (scale - 1));
        var candidate = EvaluateFrame(samples, channels, channel, firstSample, count, c1, c2, scale, word, initialH1, initialH2, options.Version);
        if (best is null || candidate.Error < best.Value.Error)
          best = candidate;
      }
      return best!.Value;
    }

    if (options.Encoding == AdxEncodingMode.Exponential) {
      var desired = ChooseLinearScale(samples, channels, channel, firstSample, count,
        derived.Coef1, derived.Coef2, initialH1, initialH2, options.Version);
      var scale = 1;
      var exponent = 12;
      while (scale < desired && exponent > 0) {
        scale <<= 1;
        --exponent;
      }
      return EvaluateFrame(samples, channels, channel, firstSample, count,
        derived.Coef1, derived.Coef2, scale, (ushort)exponent, initialH1, initialH2, options.Version);
    }

    var standardLimit = options.Encryption is null ? 0x8000 : 0x2000;
    var standardScale = Math.Min(ChooseLinearScale(samples, channels, channel, firstSample, count,
      derived.Coef1, derived.Coef2, initialH1, initialH2, options.Version), standardLimit);
    return EvaluateFrame(samples, channels, channel, firstSample, count,
      derived.Coef1, derived.Coef2, standardScale, (ushort)(standardScale - 1), initialH1, initialH2, options.Version);
  }

  private static int ChooseLinearScale(
    ReadOnlySpan<short> samples, int channels, int channel, int firstSample, int count,
    int coef1, int coef2, int h1, int h2, AdxHeaderVersion version) {
    var maxPositive = 0;
    var maxNegative = 0;
    for (var i = 0; i < count; ++i) {
      var prediction = version == AdxHeaderVersion.Version3
        ? ((coef1 * h1) >> 12) + ((coef2 * h2) >> 12)
        : (coef1 * h1 + coef2 * h2) >> 12;
      var target = samples[(firstSample + i) * channels + channel];
      var residual = target - prediction;
      if (residual >= 0) maxPositive = Math.Max(maxPositive, residual);
      else maxNegative = Math.Max(maxNegative, -residual);
      h2 = h1;
      h1 = target;
    }
    var positiveScale = (maxPositive + 6) / 7;
    var negativeScale = (maxNegative + 7) / 8;
    return Math.Max(1, Math.Max(positiveScale, negativeScale));
  }

  private static FrameChoice EvaluateFrame(
    ReadOnlySpan<short> samples, int channels, int channel, int firstSample, int count,
    int coef1, int coef2, int scale, ushort word, int h1, int h2, AdxHeaderVersion version) {
    long error = 0;
    for (var i = 0; i < count; ++i) {
      var prediction = version == AdxHeaderVersion.Version3
        ? ((coef1 * h1) >> 12) + ((coef2 * h2) >> 12)
        : (coef1 * h1 + coef2 * h2) >> 12;
      var target = samples[(firstSample + i) * channels + channel];
      var quant = Quantize(target - prediction, scale);
      var reconstructed = Clamp16(prediction + quant * scale);
      var delta = target - reconstructed;
      error += (long)delta * delta;
      h2 = h1;
      h1 = reconstructed;
    }
    return new FrameChoice(word, scale, coef1, coef2, error);
  }

  private static int Quantize(int residual, int scale) {
    var quant = (int)Math.Round((double)residual / scale, MidpointRounding.AwayFromZero);
    return Math.Clamp(quant, -8, 7);
  }

  private static (short[] Hist1, short[] Hist2) BuildInitialHistories(
    ReadOnlySpan<short> interleaved,
    int channels,
    AdxHeaderVersion version) {
    var h1 = new short[channels];
    var h2 = new short[channels];
    if (version == AdxHeaderVersion.Version4 && interleaved.Length >= channels) {
      for (var ch = 0; ch < channels; ++ch)
        h1[ch] = h2[ch] = interleaved[ch];
    }
    return (h1, h2);
  }

  private static (int[] Hist1, int[] Hist2) ReadInitialHistories(ReadOnlySpan<byte> file, AdxInfo info) {
    var h1 = new int[info.Channels];
    var h2 = new int[info.Channels];
    if (info.Version != 4)
      return (h1, h2);
    var historyEnd = 0x18 + Math.Max(8, info.Channels * 4);
    if (historyEnd > info.DataOffset - 6)
      return (h1, h2);
    for (var ch = 0; ch < info.Channels; ++ch) {
      h1[ch] = BinaryPrimitives.ReadInt16BigEndian(file[(0x18 + ch * 4)..]);
      h2[ch] = BinaryPrimitives.ReadInt16BigEndian(file[(0x1A + ch * 4)..]);
    }
    return (h1, h2);
  }

  private static void ValidateAdpcmHeader(AdxInfo info) {
    if (!info.IsAdxAdpcm)
      throw new NotSupportedException($"Unsupported ADX encoding type 0x{info.EncodingType:X2}.");
    if (info.BlockSize != FrameSize)
      throw new NotSupportedException($"ADX block size {info.BlockSize} is not the interoperable 18-byte frame size.");
    if (info.BitDepth != BitDepth)
      throw new NotSupportedException($"ADX sample bit depth {info.BitDepth} is not 4.");
    if (info.Version is not (3 or 4 or 5))
      throw new NotSupportedException($"ADX header version {info.Version} is unsupported.");
    if (info.IsEncrypted && info.EncodingType != EncodingTypeStandard)
      throw new NotSupportedException("ADX encryption revisions 8/9 are defined for standard type-3 ADX.");
  }

  private static void ValidateEncodeArguments(
    ReadOnlySpan<short> interleaved,
    int channels,
    int sampleRate,
    AdxEncodeOptions options) {
    if (channels is < 1 or > MaxChannels)
      throw new ArgumentOutOfRangeException(nameof(channels), $"ADX supports 1 to {MaxChannels} channels.");
    if (sampleRate <= 1)
      throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count is not a multiple of the channel count.", nameof(interleaved));
    if (options.HighpassFrequency < 0 || options.HighpassFrequency >= sampleRate / 2)
      throw new ArgumentOutOfRangeException(nameof(options.HighpassFrequency), "High-pass cutoff must be below Nyquist.");
    ValidateAlignment(options.HeaderAlignment);
    if (options.Version == AdxHeaderVersion.Version5 && options.LoopStartSample.HasValue)
      throw new NotSupportedException("ADX version 5 has no loop metadata layout.");
    if (options.LoopStartSample.HasValue != options.LoopEndSample.HasValue)
      throw new ArgumentException("ADX loop start and end must be supplied together.", nameof(options));
    var totalSamples = interleaved.Length / channels;
    if (options.LoopStartSample is { } start && options.LoopEndSample is { } end &&
        (start < 0 || end <= start || end > totalSamples))
      throw new ArgumentOutOfRangeException(nameof(options), "ADX loop range must satisfy 0 <= start < end <= total samples.");
    if (options.Encryption is { } encryption) {
      if (options.Version != AdxHeaderVersion.Version4)
        throw new NotSupportedException("ADX encryption revisions 8/9 require a version-4 header.");
      if (options.Encoding != AdxEncodingMode.Standard)
        throw new NotSupportedException("ADX encryption applies to standard type-3 ADX.");
      if (encryption.Revision is not (8 or 9))
        throw new ArgumentOutOfRangeException(nameof(options.Encryption), "ADX encryption revision must be 8 or 9.");
      if ((encryption.Start | encryption.Multiplier | encryption.Addend) > 0x7FFF)
        throw new ArgumentOutOfRangeException(nameof(options.Encryption), "ADX encryption LCG components are 15-bit values.");
    }
  }

  private static void ValidateAlignment(int alignment) {
    if (alignment < 1 || (alignment & (alignment - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(alignment), "ADX header alignment must be a positive power of two.");
  }

  private static int Align(int value, int alignment) => checked((value + alignment - 1) & -alignment);
  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;
  private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);
}
