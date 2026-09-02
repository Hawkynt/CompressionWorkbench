using System.Buffers.Binary;
using Codec.ALaw;
using Codec.ImaAdpcm;
using Codec.MsAdpcm;
using Codec.MuLaw;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Lib;

/// <summary>Canonical PCM and compressed-audio writer adapter for RIFF/WAVE.</summary>
internal sealed class WavAudioAdapter : IAudioPcmSource, IAudioPcmTarget {
  private static readonly string[] Codecs = ["pcm", "float", "alaw", "mulaw", "ima-adpcm", "ms-adpcm"];
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
    if (codec is "alaw" or "mulaw" or "ima-adpcm" or "ms-adpcm") {
      if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
        reason = $"{codecId} WAVE encoding requires signed PCM16 input";
        return false;
      }
      if (codec is "ima-adpcm" or "ms-adpcm" && format.Channels is < 1 or > 2) {
        reason = $"{codecId} WAVE encoding supports mono or stereo";
        return false;
      }
      reason = null;
      return true;
    }
    if (codec == "float") {
      reason = format.Encoding == AudioPcmEncoding.IeeeFloat && format.BitsPerSample is 32 or 64
        ? null : "IEEE-float WAVE requires 32- or 64-bit float PCM";
      return reason is null;
    }
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
