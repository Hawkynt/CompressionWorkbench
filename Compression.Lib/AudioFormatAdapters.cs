using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Codec.ALaw;
using Codec.ImaAdpcm;
using Codec.Mp3;
using Codec.MuLaw;
using Codec.Opus;
using Codec.Qoa;
using Codec.WavPack;
using Compression.Registry;
using FileFormat.Aiff;
using FileFormat.Au;

namespace Compression.Lib;

/// <summary>
/// Non-invasive adapters for established format descriptors that already have
/// codec read/write implementations but do not yet implement the common audio
/// conversion interfaces directly.
/// </summary>
internal static class AudioFormatAdapters {
  private static readonly AiffAdapter Aiff = new();
  private static readonly AuAdapter Au = new();
  private static readonly Mp3Adapter Mp3 = new();
  private static readonly WavPackAdapter WavPack = new();
  private static readonly QoaAdapter Qoa = new();
  private static readonly OpusAdapter Opus = new();

  // TTA, AC-3 and DTS are deliberately absent. Each has a decoder that works on
  // streams our own encoder wrote and fails on streams from anyone else — TTA
  // throws part-way through a frame whose CRC it has already checked, AC-3 and
  // DTS return no samples at all. Routing them would turn an honest "no route"
  // into a silently empty conversion, so they stay out until the decoders are
  // fixed. Measurements are in docs/AUDIO-IDENTIFIER-REGISTRY.md.
  public static IAudioPcmSource? ResolvePcmSource(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmSource ?? descriptor.Id switch {
      "Aiff" => Aiff,
      "Au" => Au,
      "Mp3" => Mp3,
      "WavPack" => WavPack,
      "Qoa" => Qoa,
      "Opus" => Opus,
      _ => null,
    };

  public static IAudioPcmTarget? ResolvePcmTarget(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmTarget ?? descriptor.Id switch {
      "Aiff" => Aiff,
      "Au" => Au,
      "Mp3" => Mp3,
      "WavPack" => WavPack,
      "Qoa" => Qoa,
      _ => null,
    };

  private sealed class AiffAdapter : IAudioPcmSource, IAudioPcmTarget {
    private static readonly string[] Codecs = ["pcm", "sowt", "mulaw", "alaw", "ima4", "fl32", "fl64"];

    public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var parsed = new AiffReader().Read(materialized.ToArray());
      var compression = parsed.CompressionId;
      if (!parsed.IsAifc || compression is "NONE" or "twos")
        return DecodeAiffInteger(parsed, parsed.BitsPerSample, bigEndian: true);
      return compression switch {
        "sowt" => DecodeAiffInteger(parsed, parsed.BitsPerSample, bigEndian: false),
        "ulaw" or "ULAW" => DecodeAiffCompanded(parsed, MuLawCodec.Decode(parsed.SoundData)),
        "alaw" or "ALAW" => DecodeAiffCompanded(parsed, ALawCodec.Decode(parsed.SoundData)),
        "ima4" => DecodeIma4(parsed),
        "fl32" or "FL32" => new AudioPcmBuffer(
          new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 32, AudioPcmEncoding.IeeeFloat),
          SwapSampleEndianness(parsed.SoundData, 4)),
        "fl64" or "FL64" => new AudioPcmBuffer(
          new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 64, AudioPcmEncoding.IeeeFloat),
          SwapSampleEndianness(parsed.SoundData, 8)),
        _ => throw new NotSupportedException($"AIFC compression '{compression}' is not supported by the canonical PCM pipeline."),
      };
    }

    public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
      if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
        reason = $"AIFF/AIFC does not support codec '{codecId}' in this writer";
        return false;
      }
      if (format.Channels < 1 || format.SampleRate < 1) {
        reason = "AIFF/AIFC requires a positive sample rate and at least one channel";
        return false;
      }

      if (codecId.Equals("mulaw", StringComparison.OrdinalIgnoreCase) ||
          codecId.Equals("alaw", StringComparison.OrdinalIgnoreCase) ||
          codecId.Equals("ima4", StringComparison.OrdinalIgnoreCase)) {
        if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
          reason = $"{codecId} AIFC encoding requires signed PCM16 input";
          return false;
        }
        reason = null;
        return true;
      }

      if (codecId.Equals("fl32", StringComparison.OrdinalIgnoreCase)) {
        reason = format.Encoding == AudioPcmEncoding.IeeeFloat && format.BitsPerSample == 32
          ? null : "fl32 requires 32-bit IEEE-float PCM";
        return reason is null;
      }
      if (codecId.Equals("fl64", StringComparison.OrdinalIgnoreCase)) {
        reason = format.Encoding == AudioPcmEncoding.IeeeFloat && format.BitsPerSample == 64
          ? null : "fl64 requires 64-bit IEEE-float PCM";
        return reason is null;
      }

      if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
        reason = "floating-point PCM must select fl32 or fl64";
        return false;
      }
      if (format.BitsPerSample is not (8 or 16 or 24 or 32)) {
        reason = "AIFF integer PCM supports 8/16/24/32 bits";
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

      var normalizedCodec = codecId.ToLowerInvariant();
      var sampleFrames = checked((uint)pcm.FrameCount);
      byte[] payload;
      string compressionId;
      string compressionName;
      var bitsPerSample = pcm.Format.BitsPerSample;
      var aifc = true;

      switch (normalizedCodec) {
        case "pcm":
          compressionId = "NONE";
          compressionName = "not compressed";
          aifc = false;
          payload = PrepareSignedEightOrEndianSwap(pcm, bigEndian: true);
          break;
        case "sowt":
          compressionId = "sowt";
          compressionName = "Little-endian PCM";
          payload = PrepareSignedEightOrEndianSwap(pcm, bigEndian: false);
          break;
        case "mulaw":
          compressionId = "ulaw";
          compressionName = "mu-law 2:1";
          bitsPerSample = 16;
          payload = MuLawCodec.Encode(ReadPcm16(pcm.InterleavedData));
          break;
        case "alaw":
          compressionId = "alaw";
          compressionName = "A-law 2:1";
          bitsPerSample = 16;
          payload = ALawCodec.Encode(ReadPcm16(pcm.InterleavedData));
          break;
        case "ima4":
          compressionId = "ima4";
          compressionName = "IMA 4:1";
          bitsPerSample = 16;
          payload = ImaAdpcmCodec.EncodeQuickTime(ReadPcm16(pcm.InterleavedData), pcm.Format.Channels);
          break;
        case "fl32":
          compressionId = "fl32";
          compressionName = "32-bit floating point";
          bitsPerSample = 32;
          payload = SwapSampleEndianness(pcm.InterleavedData, 4);
          break;
        case "fl64":
          compressionId = "fl64";
          compressionName = "64-bit floating point";
          bitsPerSample = 64;
          payload = SwapSampleEndianness(pcm.InterleavedData, 8);
          break;
        default:
          throw new UnreachableException();
      }

      WriteAiff(
        output,
        aifc,
        pcm.Format.Channels,
        pcm.Format.SampleRate,
        bitsPerSample,
        sampleFrames,
        compressionId,
        compressionName,
        payload);
    }

    private static AudioPcmBuffer DecodeAiffInteger(AiffReader.ParsedAiff parsed, int bitsPerSample, bool bigEndian) {
      var data = bigEndian && bitsPerSample > 8
        ? SwapSampleEndianness(parsed.SoundData, bitsPerSample / 8)
        : (byte[])parsed.SoundData.Clone();
      return new AudioPcmBuffer(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, bitsPerSample, AudioPcmEncoding.SignedInteger),
        data);
    }

    private static AudioPcmBuffer DecodeAiffCompanded(AiffReader.ParsedAiff parsed, short[] samples) {
      var bytes = new byte[samples.Length * 2];
      for (var i = 0; i < samples.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
      return new AudioPcmBuffer(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 16, AudioPcmEncoding.SignedInteger),
        bytes);
    }

    private static AudioPcmBuffer DecodeIma4(AiffReader.ParsedAiff parsed) {
      var channels = ImaAdpcmCodec.DecodeQuickTime(parsed.SoundData, parsed.NumChannels);
      var availableFrames = channels.Length == 0 ? 0 : channels.Min(static channel => channel.Length);
      var frames = parsed.SampleFrames > 0 ? Math.Min(parsed.SampleFrames, availableFrames) : availableFrames;
      var bytes = new byte[checked(frames * parsed.NumChannels * 2)];
      for (var frame = 0; frame < frames; ++frame)
        for (var channel = 0; channel < parsed.NumChannels; ++channel)
          BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan((frame * parsed.NumChannels + channel) * 2, 2),
            channels[channel][frame]);
      return new AudioPcmBuffer(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 16, AudioPcmEncoding.SignedInteger),
        bytes);
    }

    private static byte[] PrepareSignedEightOrEndianSwap(AudioPcmBuffer pcm, bool bigEndian) {
      var payload = (byte[])pcm.InterleavedData.Clone();
      if (pcm.Format.BitsPerSample == 8) {
        if (pcm.Format.Encoding == AudioPcmEncoding.UnsignedInteger)
          for (var i = 0; i < payload.Length; ++i) payload[i] ^= 0x80;
        return payload;
      }
      return bigEndian ? SwapSampleEndianness(payload, pcm.Format.BytesPerSample) : payload;
    }

    private static void WriteAiff(
      Stream output,
      bool aifc,
      int channels,
      int sampleRate,
      int bitsPerSample,
      uint sampleFrames,
      string compressionId,
      string compressionName,
      byte[] payload
    ) {
      using var commBody = new MemoryStream();
      Span<byte> fixedComm = stackalloc byte[18];
      BinaryPrimitives.WriteInt16BigEndian(fixedComm, checked((short)channels));
      BinaryPrimitives.WriteUInt32BigEndian(fixedComm[2..], sampleFrames);
      BinaryPrimitives.WriteInt16BigEndian(fixedComm[6..], checked((short)bitsPerSample));
      AiffWriter.Encode80BitFloat(sampleRate).CopyTo(fixedComm[8..]);
      commBody.Write(fixedComm);

      if (aifc) {
        if (compressionId.Length != 4)
          throw new ArgumentException("AIFC compression IDs must be exactly four characters.", nameof(compressionId));
        commBody.Write(Encoding.ASCII.GetBytes(compressionId));
        var nameBytes = Encoding.ASCII.GetBytes(compressionName);
        var nameLength = Math.Min(byte.MaxValue, nameBytes.Length);
        commBody.WriteByte((byte)nameLength);
        commBody.Write(nameBytes, 0, nameLength);
      }

      var comm = WrapIffChunk("COMM", commBody.ToArray());
      var ssndBody = new byte[8 + payload.Length];
      payload.CopyTo(ssndBody.AsSpan(8));
      var ssnd = WrapIffChunk("SSND", ssndBody);
      var fver = aifc ? WrapIffChunk("FVER", [0xA2, 0x80, 0x51, 0x40]) : [];
      var formType = aifc ? "AIFC"u8 : "AIFF"u8;
      var formSize = checked(4 + fver.Length + comm.Length + ssnd.Length);

      Span<byte> header = stackalloc byte[12];
      "FORM"u8.CopyTo(header);
      BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)formSize));
      formType.CopyTo(header[8..]);
      output.Write(header);
      if (fver.Length != 0) output.Write(fver);
      output.Write(comm);
      output.Write(ssnd);
    }

    private static byte[] WrapIffChunk(string id, byte[] body) {
      var paddedLength = body.Length + (body.Length & 1);
      var chunk = new byte[8 + paddedLength];
      Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
      BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), checked((uint)body.Length));
      body.CopyTo(chunk.AsSpan(8));
      return chunk;
    }
  }

  private sealed class AuAdapter : IAudioPcmSource, IAudioPcmTarget {
    private static readonly string[] Codecs = ["pcm", "mulaw", "alaw"];

    public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var parsed = new AuReader().Read(materialized.ToArray());
      return parsed.Encoding switch {
        1 => DecodeAuCompanded(parsed, MuLawCodec.Decode(parsed.SoundData)),
        2 => new AudioPcmBuffer(
          new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 8, AudioPcmEncoding.SignedInteger),
          (byte[])parsed.SoundData.Clone()),
        3 => DecodeBigEndianInteger(parsed, 16),
        4 => DecodeBigEndianInteger(parsed, 24),
        5 => DecodeBigEndianInteger(parsed, 32),
        6 => DecodeBigEndianFloat(parsed, 32),
        7 => DecodeBigEndianFloat(parsed, 64),
        27 => DecodeAuCompanded(parsed, ALawCodec.Decode(parsed.SoundData)),
        _ => throw new NotSupportedException($"AU encoding {parsed.Encoding} is not supported by the canonical PCM pipeline."),
      };
    }

    public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
      if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
        reason = $"AU does not support codec '{codecId}' in this writer";
        return false;
      }
      if (format.Channels < 1 || format.SampleRate < 1) {
        reason = "AU requires a positive sample rate and at least one channel";
        return false;
      }
      if (codecId.Equals("mulaw", StringComparison.OrdinalIgnoreCase) ||
          codecId.Equals("alaw", StringComparison.OrdinalIgnoreCase)) {
        if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
          reason = "G.711 AU encoding requires signed PCM16 input";
          return false;
        }
        reason = null;
        return true;
      }
      if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
        if (format.BitsPerSample is not (32 or 64)) {
          reason = "AU floating-point PCM supports 32 or 64 bits";
          return false;
        }
        reason = null;
        return true;
      }
      if (format.BitsPerSample is not (8 or 16 or 24 or 32)) {
        reason = "AU integer PCM supports 8/16/24/32 bits";
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

      uint encoding;
      byte[] payload;
      if (codecId.Equals("mulaw", StringComparison.OrdinalIgnoreCase)) {
        encoding = 1;
        payload = MuLawCodec.Encode(ReadPcm16(pcm.InterleavedData));
      } else if (codecId.Equals("alaw", StringComparison.OrdinalIgnoreCase)) {
        encoding = 27;
        payload = ALawCodec.Encode(ReadPcm16(pcm.InterleavedData));
      } else if (pcm.Format.Encoding == AudioPcmEncoding.IeeeFloat) {
        encoding = pcm.Format.BitsPerSample == 32 ? 6u : 7u;
        payload = SwapSampleEndianness(pcm.InterleavedData, pcm.Format.BytesPerSample);
      } else if (pcm.Format.BitsPerSample == 8) {
        encoding = 2;
        payload = (byte[])pcm.InterleavedData.Clone();
        if (pcm.Format.Encoding == AudioPcmEncoding.UnsignedInteger)
          for (var i = 0; i < payload.Length; ++i) payload[i] ^= 0x80;
      } else {
        encoding = pcm.Format.BitsPerSample switch {
          16 => 3u,
          24 => 4u,
          32 => 5u,
          _ => throw new UnreachableException(),
        };
        payload = SwapSampleEndianness(pcm.InterleavedData, pcm.Format.BytesPerSample);
      }

      WriteAu(output, encoding, pcm.Format.SampleRate, pcm.Format.Channels, payload);
    }

    private static AudioPcmBuffer DecodeAuCompanded(AuReader.ParsedAu parsed, short[] samples) {
      var bytes = new byte[samples.Length * 2];
      for (var i = 0; i < samples.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
      return new AudioPcmBuffer(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, 16, AudioPcmEncoding.SignedInteger),
        bytes);
    }

    private static AudioPcmBuffer DecodeBigEndianInteger(AuReader.ParsedAu parsed, int bitsPerSample)
      => new(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, bitsPerSample, AudioPcmEncoding.SignedInteger),
        SwapSampleEndianness(parsed.SoundData, bitsPerSample / 8));

    private static AudioPcmBuffer DecodeBigEndianFloat(AuReader.ParsedAu parsed, int bitsPerSample)
      => new(
        new AudioPcmFormat(parsed.SampleRate, parsed.NumChannels, bitsPerSample, AudioPcmEncoding.IeeeFloat),
        SwapSampleEndianness(parsed.SoundData, bitsPerSample / 8));

    private static void WriteAu(Stream output, uint encoding, int sampleRate, int channels, byte[] payload) {
      Span<byte> header = stackalloc byte[24];
      ".snd"u8.CopyTo(header);
      BinaryPrimitives.WriteUInt32BigEndian(header[4..], 24);
      BinaryPrimitives.WriteUInt32BigEndian(header[8..], checked((uint)payload.Length));
      BinaryPrimitives.WriteUInt32BigEndian(header[12..], encoding);
      BinaryPrimitives.WriteUInt32BigEndian(header[16..], checked((uint)sampleRate));
      BinaryPrimitives.WriteUInt32BigEndian(header[20..], checked((uint)channels));
      output.Write(header);
      output.Write(payload);
    }
  }

  private sealed class Mp3Adapter : IAudioPcmSource, IAudioPcmTarget {
    private static readonly string[] Codecs = ["mp3", "mpeg-layer3"];

    public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var info = Mp3Codec.ReadStreamInfo(materialized);
      materialized.Position = 0;
      using var pcm = new MemoryStream();
      Mp3Codec.Decompress(materialized, pcm);
      return new AudioPcmBuffer(
        new AudioPcmFormat(info.SampleRate, info.Channels, 16, AudioPcmEncoding.SignedInteger),
        pcm.ToArray());
    }

    public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
      if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
        reason = $"codec '{codecId}' is not MPEG Layer III";
        return false;
      }
      if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
        reason = "MP3 encoding requires signed PCM16 input";
        return false;
      }
      if (format.Channels is < 1 or > 2) {
        reason = "MP3 supports mono or stereo input";
        return false;
      }
      if (format.SampleRate is < 8_000 or > 48_000) {
        reason = "MP3 input sample rate must be between 8 and 48 kHz";
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
      if ((pcm.InterleavedData.Length & 1) != 0)
        throw new InvalidDataException("PCM16 payload has an odd byte length.");

      var samples = ReadPcm16(pcm.InterleavedData);
      var bitrate = options.GetOptionInt("bitrate", 128);
      if (bitrate > 1_000) bitrate = (bitrate + 500) / 1_000;
      var quality = options.GetOptionInt("quality", options.Level ?? 5);
      var variableBitrate = options.GetOptionBool("vbr", false);
      var outputRate = options.HasOption("sample-rate")
        ? (int?)options.GetOptionInt("sample-rate", pcm.Format.SampleRate)
        : null;
      var channelMode = options.GetOption("channel-mode", "auto").ToLowerInvariant() switch {
        "stereo" => Mp3EncoderChannelMode.Stereo,
        "joint" or "joint-stereo" or "jointstereo" => Mp3EncoderChannelMode.JointStereo,
        "dual" or "dual-channel" => Mp3EncoderChannelMode.DualChannel,
        "mono" => Mp3EncoderChannelMode.Mono,
        _ => Mp3EncoderChannelMode.Auto,
      };

      var encoded = Mp3Encoder.Encode(samples, new Mp3EncoderOptions(
        pcm.Format.SampleRate,
        pcm.Format.Channels,
        bitrate,
        channelMode,
        quality,
        variableBitrate,
        outputRate));
      output.Write(encoded);
    }
  }

  /// <summary>Quite OK Audio. Fixed 16-bit signed samples, in and out.</summary>
  private sealed class QoaAdapter : IAudioPcmSource, IAudioPcmTarget {
    private static readonly string[] Codecs = ["qoa"];

    public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var info = QoaCodec.ReadStreamInfo(materialized);
      materialized.Position = 0;
      using var pcm = new MemoryStream();
      QoaCodec.Decompress(materialized, pcm);
      return new AudioPcmBuffer(
        new AudioPcmFormat(info.SampleRate, info.Channels, 16), pcm.ToArray());
    }

    public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
      if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
        reason = $"codec '{codecId}' is not QOA";
        return false;
      }
      if (format.Channels is < 1 or > 255) {
        reason = "QOA carries between one and 255 channels";
        return false;
      }
      if (format.SampleRate is < 1 or > 0xFFFFFF) {
        reason = "QOA stores the sample rate in 24 bits";
        return false;
      }

      return CanEncodeInteger(format, "QOA", [16], out reason);
    }

    public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
      ArgumentNullException.ThrowIfNull(output);
      ArgumentNullException.ThrowIfNull(pcm);
      if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
        throw new NotSupportedException(reason);

      using var input = new MemoryStream(pcm.InterleavedData, writable: false);
      QoaCodec.Compress(input, output, pcm.Format.Channels, pcm.Format.SampleRate);
    }
  }

  /// <summary>
  /// Ogg Opus. The decoder always runs at 48 kHz whatever the original input
  /// rate was, so that — and not <c>InputSampleRate</c> — is what the decoded
  /// buffer carries.
  /// </summary>
  private sealed class OpusAdapter : IAudioPcmSource {
    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var info = OpusCodec.ReadStreamInfo(materialized);
      materialized.Position = 0;
      using var pcm = new MemoryStream();
      OpusCodec.Decompress(materialized, pcm);
      return new AudioPcmBuffer(
        new AudioPcmFormat(48_000, info.Channels, 16), pcm.ToArray());
    }
  }

  // WAVE keeps 8-bit samples unsigned and everything wider signed; the integer
  // codecs here follow it.
  private static AudioPcmEncoding IntegerEncodingFor(int bitsPerSample)
    => bitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger;

  private static bool CanEncodeInteger(
      AudioPcmFormat format, string name, int[] widths, out string? reason) {
    if (format.Channels < 1) {
      reason = $"{name} requires at least one channel";
      return false;
    }
    if (format.SampleRate < 1) {
      reason = $"{name} requires a positive sample rate";
      return false;
    }
    if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
      reason = $"{name} takes integer PCM, not IEEE floating point";
      return false;
    }
    if (!widths.Contains(format.BitsPerSample)) {
      reason = $"{name} supports {string.Join('/', widths)}-bit PCM";
      return false;
    }
    if (format.BitsPerSample == 8 && format.Encoding != AudioPcmEncoding.UnsignedInteger) {
      reason = "8-bit PCM must use the unsigned WAV sample representation";
      return false;
    }

    reason = null;
    return true;
  }

  private sealed class WavPackAdapter : IAudioPcmSource, IAudioPcmTarget {
    private static readonly string[] Codecs = ["wavpack", "wv"];

    public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

    public AudioPcmBuffer DecodePcm(Stream input) {
      ArgumentNullException.ThrowIfNull(input);
      using var materialized = Materialize(input);
      var info = WavPackCodec.ReadStreamInfo(materialized);
      materialized.Position = 0;
      using var pcm = new MemoryStream();
      WavPackCodec.Decompress(materialized, pcm);
      return new AudioPcmBuffer(
        new AudioPcmFormat(
          info.SampleRate,
          info.Channels,
          info.BitsPerSample,
          info.IsFloat ? AudioPcmEncoding.IeeeFloat
            : info.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger
            : AudioPcmEncoding.SignedInteger),
        pcm.ToArray());
    }

    public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
      if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
        reason = $"codec '{codecId}' is not WavPack";
        return false;
      }
      if (format.Channels < 1) {
        reason = "WavPack requires at least one channel";
        return false;
      }
      if (format.SampleRate < 1) {
        reason = "WavPack requires a positive sample rate";
        return false;
      }
      if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
        if (format.BitsPerSample != 32) {
          reason = "WavPack floating-point input must be 32-bit IEEE float";
          return false;
        }
        reason = null;
        return true;
      }
      if (format.BitsPerSample is not (8 or 16 or 24 or 32)) {
        reason = "WavPack integer input supports 8/16/24/32-bit PCM";
        return false;
      }
      if (format.BitsPerSample == 8 && format.Encoding != AudioPcmEncoding.UnsignedInteger) {
        reason = "8-bit PCM must use unsigned WAV/WavPack sample representation";
        return false;
      }
      if (format.BitsPerSample > 8 && format.Encoding != AudioPcmEncoding.SignedInteger) {
        reason = "16/24/32-bit WavPack integer input must be signed PCM";
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

      using var source = new MemoryStream(pcm.InterleavedData, writable: false);
      WavPackCodec.Compress(
        source,
        output,
        pcm.Format.Channels,
        pcm.Format.SampleRate,
        pcm.Format.BitsPerSample,
        isFloat: pcm.Format.Encoding == AudioPcmEncoding.IeeeFloat);
    }
  }

  private static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has an odd byte length.");
    var samples = new short[data.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
    return samples;
  }

  private static byte[] SwapSampleEndianness(ReadOnlySpan<byte> data, int bytesPerSample) {
    if (bytesPerSample <= 1) return data.ToArray();
    if (data.Length % bytesPerSample != 0)
      throw new InvalidDataException("PCM payload length is not aligned to the sample width.");
    var result = new byte[data.Length];
    for (var offset = 0; offset < data.Length; offset += bytesPerSample)
      for (var i = 0; i < bytesPerSample; ++i)
        result[offset + i] = data[offset + bytesPerSample - 1 - i];
    return result;
  }

  private static MemoryStream Materialize(Stream input) {
    if (input.CanSeek) input.Position = 0;
    var memory = new MemoryStream();
    input.CopyTo(memory);
    memory.Position = 0;
    return memory;
  }
}
