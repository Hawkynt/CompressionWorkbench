using System.Buffers.Binary;
using Codec.Mp3;
using Codec.WavPack;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Non-invasive adapters for established format descriptors that already have
/// codec read/write implementations but do not yet implement the common audio
/// conversion interfaces directly.
/// </summary>
internal static class AudioFormatAdapters {
  private static readonly Mp3Adapter Mp3 = new();
  private static readonly WavPackAdapter WavPack = new();

  public static IAudioPcmSource? ResolvePcmSource(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmSource ?? descriptor.Id switch {
      "Mp3" => Mp3,
      "WavPack" => WavPack,
      _ => null,
    };

  public static IAudioPcmTarget? ResolvePcmTarget(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmTarget ?? descriptor.Id switch {
      "Mp3" => Mp3,
      "WavPack" => WavPack,
      _ => null,
    };

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

      var samples = new short[pcm.InterleavedData.Length / 2];
      for (var i = 0; i < samples.Length; ++i)
        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2, 2));

      var bitrate = options.GetOptionInt("bitrate", 128);
      if (bitrate > 1_000) bitrate = (bitrate + 500) / 1_000;
      var quality = options.GetOptionInt("quality", options.Level ?? 5);
      var variableBitrate = options.GetOptionBool("vbr", false);
      var outputRate = options.HasOption("sample-rate") ? options.GetOptionInt("sample-rate", pcm.Format.SampleRate) : null;
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

  private static MemoryStream Materialize(Stream input) {
    if (input.CanSeek) input.Position = 0;
    var memory = new MemoryStream();
    input.CopyTo(memory);
    memory.Position = 0;
    return memory;
  }
}
