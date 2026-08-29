using System.Buffers.Binary;
using Codec.Ac3;
using Codec.Dts;
using Compression.Registry;

namespace Compression.Lib;

internal sealed class Ac3AudioAdapter : IAudioPcmSource, IAudioPcmTarget {
  private static readonly string[] Codecs = ["ac3"];
  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var source = Materialize(input);
    var info = Ac3Codec.ReadStreamInfo(source);
    source.Position = 0;
    using var pcm = new MemoryStream();
    Ac3Codec.Decompress(source, pcm);
    return new AudioPcmBuffer(
      new AudioPcmFormat(info.SampleRate, info.Channels, 16, AudioPcmEncoding.SignedInteger),
      pcm.ToArray());
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!codecId.Equals("ac3", StringComparison.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not AC-3";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AC-3 encoding requires signed PCM16 input";
      return false;
    }
    if (format.SampleRate is not (32_000 or 44_100 or 48_000)) {
      reason = "AC-3 encoding supports 32, 44.1, or 48 kHz";
      return false;
    }
    if (format.Channels is < 1 or > 6) {
      reason = "AC-3 encoding supports 1 to 6 channels";
      return false;
    }
    if (!TryResolveLayout(format.Channels, options, out _, out _, out reason)) return false;
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    _ = TryResolveLayout(pcm.Format.Channels, options, out var acmod, out var lfe, out _);
    var samples = ReadPcm16(pcm.InterleavedData);
    var bitrate = options.GetOptionInt("bitrate", pcm.Format.Channels switch {
      1 => 96_000,
      2 => 192_000,
      3 => 256_000,
      4 => 384_000,
      _ => 448_000,
    });
    if (bitrate < 1_000) bitrate *= 1_000;

    var encoded = Ac3Codec.Encode(samples, new Ac3EncoderOptions(
      pcm.Format.SampleRate,
      bitrate,
      acmod,
      lfe,
      options.GetOptionInt("dialnorm", -31),
      options.GetOptionInt("cutoff", 0),
      PadFinalFrame: options.GetOptionBool("pad-final-frame", true)));
    output.Write(encoded);
  }

  private static bool TryResolveLayout(int channels, FormatCreateOptions options,
    out int acmod, out bool lfe, out string? reason) {
    if (options.TryGetInt("acmod", out var requestedAcmod)) {
      acmod = requestedAcmod;
      lfe = options.GetOptionBool("lfe", false);
      var expected = AcmodChannels(acmod) + (lfe ? 1 : 0);
      if (expected != channels) {
        reason = $"acmod={acmod}, lfe={lfe} expects {expected} channels, input has {channels}";
        return false;
      }
      reason = null;
      return true;
    }

    (acmod, lfe) = channels switch {
      1 => (1, false),
      2 => (2, false),
      3 => (3, false),
      4 => (6, false),
      5 => (7, false),
      6 => (7, true),
      _ => (0, false),
    };
    reason = acmod == 0 ? $"no default AC-3 layout for {channels} channels" : null;
    return acmod != 0;
  }

  private static int AcmodChannels(int acmod) => acmod switch {
    0 => 2,
    1 => 1,
    2 => 2,
    3 or 4 => 3,
    5 or 6 => 4,
    7 => 5,
    _ => 0,
  };
}

internal sealed class DtsAudioAdapter : IAudioPcmSource, IAudioPcmTarget {
  private static readonly string[] Codecs = ["dts"];
  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var source = Materialize(input);
    var info = DtsCodec.ReadStreamInfo(source);
    source.Position = 0;
    using var pcm = new MemoryStream();
    DtsCodec.Decompress(source, pcm);
    return new AudioPcmBuffer(
      new AudioPcmFormat(info.SampleRate, info.Channels, 16, AudioPcmEncoding.SignedInteger),
      pcm.ToArray());
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!codecId.Equals("dts", StringComparison.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not DTS core";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "DTS core encoding requires signed PCM16 input";
      return false;
    }
    if (format.Channels is not (1 or 2 or 4 or 5)) {
      reason = "DTS core encoder supports mono, stereo, quad, or 5.0";
      return false;
    }
    if (format.SampleRate is < 8_000 or > 48_000) {
      reason = "DTS core sample rate must be between 8 and 48 kHz";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    var samples = ReadPcm16(pcm.InterleavedData);
    var bitrate = options.GetOptionInt("bitrate", pcm.Format.Channels <= 2 ? 768_000 : 1_536_000);
    if (bitrate < 10_000) bitrate *= 1_000;
    var encoded = DtsCodec.Encode(samples, new DtsEncoderOptions(
      pcm.Format.SampleRate,
      pcm.Format.Channels,
      bitrate,
      options.GetOptionInt("subbands", 16),
      options.GetOptionBool("pad-final-frame", true)));
    output.Write(encoded);
  }
}

file static class ElementaryAudioAdapterHelpers {
  public static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0) throw new InvalidDataException("PCM16 payload has odd length.");
    var samples = new short[data.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
    return samples;
  }

  public static MemoryStream Materialize(Stream input) {
    if (input.CanSeek) input.Position = 0;
    var memory = new MemoryStream();
    input.CopyTo(memory);
    memory.Position = 0;
    return memory;
  }
}

file static short[] ReadPcm16(ReadOnlySpan<byte> data) => ElementaryAudioAdapterHelpers.ReadPcm16(data);
file static MemoryStream Materialize(Stream input) => ElementaryAudioAdapterHelpers.Materialize(input);
