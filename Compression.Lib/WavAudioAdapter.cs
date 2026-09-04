using System.Buffers.Binary;
using Codec.ALaw;
using Codec.G72x;
using Codec.G722;
using Codec.Gsm610;
using Codec.ImaAdpcm;
using Codec.MsAdpcm;
using Codec.MuLaw;
using Codec.OkiAdpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Lib;

/// <summary>Canonical PCM and compressed-audio writer adapter for RIFF/WAVE.</summary>
internal sealed class WavAudioAdapter : IAudioPcmSource, IAudioPcmTarget {
  private static readonly string[] Codecs = [
    "pcm", "float", "alaw", "mulaw", "ima-adpcm", "ms-adpcm",
    "oki-adpcm", "dialogic-oki-adpcm", "gsm610", "g721",
    "g726-16", "g726-24", "g726-32", "g726-40",
    "g726-apicom-16", "g726-apicom-24", "g726-apicom-32", "g726-apicom-40",
    "g722", "g722-apicom",
  ];
  private static readonly short[] MsAdpcmCoefficients = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];

  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    var parsed = new WavReader().ReadCanonicalPcm(memory.ToArray());
    if (parsed.FormatCode is not (1 or 3))
      throw new NotSupportedException($"WAVE format code 0x{parsed.FormatCode:X4} is not decoded to canonical PCM.");
    return new AudioPcmBuffer(
      new AudioPcmFormat(
        parsed.SampleRate,
        parsed.NumChannels,
        parsed.BitsPerSample,
        parsed.FormatCode == 3 ? AudioPcmEncoding.IeeeFloat
          : parsed.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger
          : AudioPcmEncoding.SignedInteger,
        parsed.ChannelMask),
      parsed.InterleavedPcm);
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"WAVE codec '{codecId}' is not supported by this writer";
      return false;
    }
    if (format.Channels < 1 || format.SampleRate < 1) {
      reason = "WAVE requires a positive sample rate and at least one channel";
      return false;
    }

    var codec = codecId.ToLowerInvariant();
    if (codec == "float") {
      reason = format.Encoding == AudioPcmEncoding.IeeeFloat && format.BitsPerSample is 32 or 64
        ? null : "IEEE-float WAVE requires 32- or 64-bit float PCM";
      return reason is null;
    }
    if (codec == "pcm") {
      if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
        reason = "floating-point input must select the 'float' WAVE codec";
        return false;
      }
      if (format.BitsPerSample is not (8 or 16 or 24 or 32)) {
        reason = "PCM WAVE supports 8/16/24/32-bit integer samples";
        return false;
      }
      reason = null;
      return true;
    }

    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = $"{codecId} WAVE encoding requires signed PCM16 input";
      return false;
    }
    if (codec is "ima-adpcm" or "ms-adpcm") {
      reason = format.Channels is 1 or 2 ? null : $"{codecId} WAVE encoding supports mono or stereo";
      return reason is null;
    }
    if (codec is "alaw" or "mulaw") {
      reason = null;
      return true;
    }
    if (format.Channels != 1) {
      reason = $"{codecId} WAVE encoding currently supports mono only";
      return false;
    }
    if (codec == "gsm610" && format.SampleRate != 8_000) {
      reason = "Microsoft GSM 06.10 WAVE requires 8000 Hz PCM";
      return false;
    }
    if ((codec == "g722" || codec == "g722-apicom") && format.SampleRate != 16_000) {
      reason = "G.722 WAVE requires 16000 Hz PCM";
      return false;
    }
    if ((codec == "g721" || codec.StartsWith("g726-", StringComparison.Ordinal)) && format.SampleRate != 8_000) {
      reason = $"{codecId} WAVE encoding requires 8000 Hz PCM";
      return false;
    }

    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    switch (codecId.ToLowerInvariant()) {
      case "pcm": {
        var payload = (byte[])pcm.InterleavedData.Clone();
        if (pcm.Format.BitsPerSample == 8 && pcm.Format.Encoding == AudioPcmEncoding.SignedInteger)
          for (var i = 0; i < payload.Length; ++i) payload[i] ^= 0x80;
        WriteWave(output, 0x0001, pcm.Format.Channels, pcm.Format.SampleRate,
          pcm.Format.BitsPerSample, checked((ushort)pcm.Format.BytesPerFrame),
          checked((uint)(pcm.Format.SampleRate * pcm.Format.BytesPerFrame)), payload, [], null);
        break;
      }
      case "float":
        WriteWave(output, 0x0003, pcm.Format.Channels, pcm.Format.SampleRate,
          pcm.Format.BitsPerSample, checked((ushort)pcm.Format.BytesPerFrame),
          checked((uint)(pcm.Format.SampleRate * pcm.Format.BytesPerFrame)), pcm.InterleavedData, [], checked((uint)pcm.FrameCount));
        break;
      case "alaw":
        WriteG711(output, pcm, aLaw: true);
        break;
      case "mulaw":
        WriteG711(output, pcm, aLaw: false);
        break;
      case "ima-adpcm":
        WriteImaAdpcm(output, pcm, options);
        break;
      case "ms-adpcm":
        WriteMsAdpcm(output, pcm, options);
        break;
      case "oki-adpcm":
        WriteOkiAdpcm(output, pcm, 0x0010);
        break;
      case "dialogic-oki-adpcm":
        WriteOkiAdpcm(output, pcm, 0x0017);
        break;
      case "gsm610":
        WriteGsm610(output, pcm);
        break;
      case "g721":
        WriteG72x(output, pcm, 0x0040, 4, useG721EntryPoint: true);
        break;
      case "g726-16":
        WriteG72x(output, pcm, 0x0045, 2);
        break;
      case "g726-24":
        WriteG72x(output, pcm, 0x0045, 3);
        break;
      case "g726-32":
        WriteG72x(output, pcm, 0x0045, 4);
        break;
      case "g726-40":
        WriteG72x(output, pcm, 0x0045, 5);
        break;
      case "g726-apicom-16":
        WriteG72x(output, pcm, 0x0064, 2);
        break;
      case "g726-apicom-24":
        WriteG72x(output, pcm, 0x0064, 3);
        break;
      case "g726-apicom-32":
        WriteG72x(output, pcm, 0x0064, 4);
        break;
      case "g726-apicom-40":
        WriteG72x(output, pcm, 0x0064, 5);
        break;
      case "g722":
        WriteG722(output, pcm, 0x028F);
        break;
      case "g722-apicom":
        WriteG722(output, pcm, 0x0065);
        break;
      default:
        throw new InvalidOperationException($"Unhandled WAVE codec '{codecId}'.");
    }
  }

  private static void WriteG711(Stream output, AudioPcmBuffer pcm, bool aLaw) {
    var samples = ReadPcm16(pcm.InterleavedData);
    var encoded = aLaw ? ALawCodec.Encode(samples) : MuLawCodec.Encode(samples);
    var blockAlign = checked((ushort)pcm.Format.Channels);
    WriteWave(output, aLaw ? (ushort)0x0006 : (ushort)0x0007, pcm.Format.Channels, pcm.Format.SampleRate,
      8, blockAlign, checked((uint)(pcm.Format.SampleRate * blockAlign)), encoded, [0, 0], checked((uint)pcm.FrameCount));
  }

  private static void WriteImaAdpcm(Stream output, AudioPcmBuffer pcm, FormatCreateOptions options) {
    var blockAlign = options.GetOptionInt("block-align", pcm.Format.Channels == 1 ? 256 : 512);
    if (blockAlign < 4 * pcm.Format.Channels || blockAlign > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options), "IMA ADPCM block-align is invalid.");
    if (pcm.Format.Channels == 2 && (blockAlign - 8) % 8 != 0)
      throw new ArgumentException("Stereo IMA ADPCM block-align must leave a data area divisible by 8 bytes.", nameof(options));
    var samples = ReadPcm16(pcm.InterleavedData);
    var encoded = ImaAdpcmCodec.Encode(samples, pcm.Format.Channels, blockAlign);
    var samplesPerBlock = (blockAlign - 4 * pcm.Format.Channels) * 2 / pcm.Format.Channels + 1;
    Span<byte> extra = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(extra, 2);
    BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], checked((ushort)samplesPerBlock));
    var average = checked((uint)((long)pcm.Format.SampleRate * blockAlign / samplesPerBlock));
    WriteWave(output, 0x0011, pcm.Format.Channels, pcm.Format.SampleRate, 4,
      checked((ushort)blockAlign), average, encoded, extra.ToArray(), checked((uint)pcm.FrameCount));
  }

  private static void WriteMsAdpcm(Stream output, AudioPcmBuffer pcm, FormatCreateOptions options) {
    var blockAlign = options.GetOptionInt("block-align", pcm.Format.Channels == 1 ? 256 : 512);
    var headerBytes = 7 * pcm.Format.Channels;
    if (blockAlign < headerBytes || blockAlign > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options), "MS ADPCM block-align is invalid.");
    var samples = ReadPcm16(pcm.InterleavedData);
    var encoded = MsAdpcmCodec.Encode(samples, pcm.Format.Channels, blockAlign);
    var samplesPerBlock = 2 + (blockAlign - headerBytes) * 2 / pcm.Format.Channels;

    var extra = new byte[6 + MsAdpcmCoefficients.Length * 2];
    BinaryPrimitives.WriteUInt16LittleEndian(extra, checked((ushort)(extra.Length - 2)));
    BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), checked((ushort)samplesPerBlock));
    BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(4), 7);
    for (var i = 0; i < MsAdpcmCoefficients.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(extra.AsSpan(6 + i * 2), MsAdpcmCoefficients[i]);

    var average = checked((uint)((long)pcm.Format.SampleRate * blockAlign / samplesPerBlock));
    WriteWave(output, 0x0002, pcm.Format.Channels, pcm.Format.SampleRate, 4,
      checked((ushort)blockAlign), average, encoded, extra, checked((uint)pcm.FrameCount));
  }

  private static void WriteOkiAdpcm(Stream output, AudioPcmBuffer pcm, ushort formatTag) {
    var encoded = OkiAdpcmCodec.Encode(ReadPcm16(pcm.InterleavedData));
    var average = checked((uint)((pcm.Format.SampleRate + 1) / 2));
    WriteWave(output, formatTag, 1, pcm.Format.SampleRate, 4, 1, average,
      encoded, [0, 0], checked((uint)pcm.FrameCount));
  }

  private static void WriteGsm610(Stream output, AudioPcmBuffer pcm) {
    var encoded = Gsm610Wav49.Encode(ReadPcm16(pcm.InterleavedData));
    Span<byte> extra = stackalloc byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(extra, 2);
    BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], checked((ushort)Gsm610Wav49.SamplesPerBlock));
    const uint averageBytesPerSecond = 1_625;
    WriteWave(output, 0x0031, 1, 8_000, 0, checked((ushort)Gsm610Wav49.BlockBytes), averageBytesPerSecond,
      encoded, extra.ToArray(), checked((uint)pcm.FrameCount));
  }

  private static void WriteG72x(Stream output, AudioPcmBuffer pcm, ushort formatTag, int bitsPerSample, bool useG721EntryPoint = false) {
    var samples = ReadPcm16(pcm.InterleavedData);
    var encoded = useG721EntryPoint ? G72xCodec.EncodeG721(samples) : G72xCodec.EncodeG726(samples, bitsPerSample);
    var blockAlign = bitsPerSample is 3 or 5 ? bitsPerSample : 1;
    var average = checked((uint)((long)pcm.Format.SampleRate * bitsPerSample / 8));
    WriteWave(output, formatTag, 1, pcm.Format.SampleRate, bitsPerSample, checked((ushort)blockAlign), average,
      encoded, [0, 0], checked((uint)pcm.FrameCount));
  }

  private static void WriteG722(Stream output, AudioPcmBuffer pcm, ushort formatTag) {
    var samples = ReadPcm16(pcm.InterleavedData);
    if ((samples.Length & 1) != 0) {
      var padded = new short[samples.Length + 1];
      samples.AsSpan().CopyTo(padded);
      padded[^1] = samples[^1];
      samples = padded;
    }
    var encoded = G722Codec.Encode(samples);
    const int codedBitsPerPcmSample = 4;
    const ushort blockAlign = 1;
    const uint averageBytesPerSecond = 8_000;
    WriteWave(output, formatTag, 1, 16_000, codedBitsPerPcmSample, blockAlign, averageBytesPerSecond,
      encoded, [0, 0], checked((uint)pcm.FrameCount));
  }

  private static void WriteWave(Stream output, ushort formatTag, int channels, int sampleRate,
    int bitsPerSample, ushort blockAlign, uint averageBytesPerSecond, byte[] data, byte[] extraFmt, uint? factFrames) {
    var fmtBodyLength = 16 + extraFmt.Length;
    var fmtPadded = fmtBodyLength + (fmtBodyLength & 1);
    var factChunkLength = factFrames.HasValue ? 12 : 0;
    var dataPadded = data.Length + (data.Length & 1);
    var riffPayloadLength = checked(4 + 8 + fmtPadded + factChunkLength + 8 + dataPadded);

    Span<byte> riff = stackalloc byte[12];
    "RIFF"u8.CopyTo(riff);
    BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], checked((uint)riffPayloadLength));
    "WAVE"u8.CopyTo(riff[8..]);
    output.Write(riff);

    Span<byte> fmtHeader = stackalloc byte[8];
    "fmt "u8.CopyTo(fmtHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(fmtHeader[4..], checked((uint)fmtBodyLength));
    output.Write(fmtHeader);
    Span<byte> fmt = stackalloc byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(fmt, formatTag);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[2..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[8..], averageBytesPerSecond);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[12..], blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[14..], checked((ushort)bitsPerSample));
    output.Write(fmt);
    output.Write(extraFmt);
    if ((fmtBodyLength & 1) != 0) output.WriteByte(0);

    if (factFrames is { } frames) {
      Span<byte> fact = stackalloc byte[12];
      "fact"u8.CopyTo(fact);
      BinaryPrimitives.WriteUInt32LittleEndian(fact[4..], 4);
      BinaryPrimitives.WriteUInt32LittleEndian(fact[8..], frames);
      output.Write(fact);
    }

    Span<byte> dataHeader = stackalloc byte[8];
    "data"u8.CopyTo(dataHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(dataHeader[4..], checked((uint)data.Length));
    output.Write(dataHeader);
    output.Write(data);
    if ((data.Length & 1) != 0) output.WriteByte(0);
  }

  private static short[] ReadPcm16(ReadOnlySpan<byte> bytes) {
    if ((bytes.Length & 1) != 0) throw new InvalidDataException("PCM16 payload has odd length.");
    var samples = new short[bytes.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
    return samples;
  }
}
