#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;

namespace FileFormat.Avi;

/// <summary>
/// Decodes an AVI audio stream's concatenated <c>movi</c> chunk payload into per-speaker
/// mono WAVs (Kind <c>Channel</c>), routed by the stream's WAVEFORMATEX <c>wFormatTag</c>:
/// PCM (0x0001), MS-ADPCM (0x0002), IMA/DVI ADPCM (0x0011), MP2/MP3 (0x0050/0x0055),
/// AC-3 (0x2000), A-law (0x0006) and µ-law (0x0007). WAVE_FORMAT_EXTENSIBLE (0xFFFE)
/// resolves to its embedded SubFormat tag. Every decode is best-effort: any unsupported
/// tag or decode failure leaves the existing raw-track surface untouched and records a
/// human-readable reason for the caller's metadata.
/// </summary>
internal static class AviAudioChannels {
  /// <summary>One decoded mono channel WAV plus its conventional speaker name.</summary>
  internal readonly record struct ChannelWav(string Name, byte[] Wav);

  /// <summary>
  /// The result of attempting to decode one audio stream: the codec label always set,
  /// either the decoded <see cref="Channels"/> or a <see cref="Reason"/> for falling back.
  /// </summary>
  internal sealed record DecodeResult(string Codec, IReadOnlyList<ChannelWav>? Channels, string? Reason);

  internal static DecodeResult Decode(AviReader.Track track) {
    var tag = ResolveFormatTag(track);
    var codec = CodecLabel(tag);

    try {
      var result = tag switch {
        0x0001 => DecodePcm(track),
        0x0003 => DecodeFloat(track),
        0x0002 => DecodeMsAdpcm(track),
        0x0011 => DecodeImaAdpcm(track),
        0x0006 => DecodeLaw(track, mu: false),
        0x0007 => DecodeLaw(track, mu: true),
        0x0050 or 0x0055 => DecodeViaStreamCodec(track, "mp3"),
        0x2000 => DecodeViaStreamCodec(track, "ac3"),
        _ => null,
      };
      if (result is { Count: > 0 })
        return new DecodeResult(codec, result, null);
      return new DecodeResult(codec, null, $"unsupported (format tag 0x{tag:X4})");
    } catch (Exception ex) {
      return new DecodeResult(codec, null, $"decode failed ({ex.GetType().Name})");
    }
  }

  /// <summary>WAVE_FORMAT_EXTENSIBLE carries the real tag in the first two bytes of its SubFormat GUID.</summary>
  private static int ResolveFormatTag(AviReader.Track track) {
    if (track.AudioFormatTag != 0xFFFE)
      return track.AudioFormatTag;
    // WAVEFORMATEXTENSIBLE: 18-byte WAVEFORMATEX + 2 (validBits) + 4 (channelMask) + 16 (SubFormat GUID).
    var f = track.Format;
    return f.Length >= 40 ? BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(24)) : 0xFFFE;
  }

  private static IReadOnlyList<ChannelWav>? DecodePcm(AviReader.Track track) {
    if (track.AudioChannels < 1 || track.AudioBitsPerSample is not (8 or 16 or 24 or 32))
      return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(
      track.Data, track.AudioChannels, track.AudioSampleRate, track.AudioBitsPerSample));
  }

  private static IReadOnlyList<ChannelWav>? DecodeFloat(AviReader.Track track) {
    if (track.AudioChannels < 1 || track.AudioBitsPerSample is not (32 or 64))
      return null;
    return Wrap(PcmCodec.SplitInterleavedFloat(
      track.Data, track.AudioChannels, track.AudioSampleRate, track.AudioBitsPerSample));
  }

  private static IReadOnlyList<ChannelWav>? DecodeMsAdpcm(AviReader.Track track) {
    if (track.AudioBlockAlign <= 0) return null;
    var perChannel = Codec.MsAdpcm.MsAdpcmCodec.Decode(track.Data, track.AudioBlockAlign, track.AudioChannels);
    return FromShortChannels(perChannel, track.AudioSampleRate);
  }

  private static IReadOnlyList<ChannelWav>? DecodeImaAdpcm(AviReader.Track track) {
    if (track.AudioBlockAlign <= 0) return null;
    var perChannel = Codec.ImaAdpcm.ImaAdpcmCodec.Decode(track.Data, track.AudioBlockAlign, track.AudioChannels);
    return FromShortChannels(perChannel, track.AudioSampleRate);
  }

  private static IReadOnlyList<ChannelWav>? DecodeLaw(AviReader.Track track, bool mu) {
    if (track.AudioChannels < 1) return null;
    var samples = mu
      ? Codec.MuLaw.MuLawCodec.Decode(track.Data)
      : Codec.ALaw.ALawCodec.Decode(track.Data);
    var pcm = ShortsToBytes(samples);
    return Wrap(PcmCodec.SplitInterleavedPcm(pcm, track.AudioChannels, track.AudioSampleRate, 16));
  }

  /// <summary>
  /// Routes the concatenated stream payload through a high-level codec whose
  /// <c>Decompress(Stream, Stream)</c> contract emits interleaved LE PCM16
  /// (MP3, AC-3). The stream's own framing is self-delimiting, so the flat
  /// concatenation of movi chunks reproduces every frame in order.
  /// </summary>
  private static IReadOnlyList<ChannelWav>? DecodeViaStreamCodec(AviReader.Track track, string codec) {
    using var src = new MemoryStream(track.Data, writable: false);
    using var pcm = new MemoryStream();
    int channels, sampleRate;
    switch (codec) {
      case "mp3": {
        using var info = new MemoryStream(track.Data, writable: false);
        var hdr = Codec.Mp3.Mp3Codec.ReadStreamInfo(info);
        channels = hdr.Channels; sampleRate = hdr.SampleRate;
        Codec.Mp3.Mp3Codec.Decompress(src, pcm);
        break;
      }
      case "ac3": {
        using var info = new MemoryStream(track.Data, writable: false);
        var hdr = Codec.Ac3.Ac3Codec.ReadStreamInfo(info);
        channels = hdr.Channels; sampleRate = hdr.SampleRate;
        Codec.Ac3.Ac3Codec.Decompress(src, pcm);
        break;
      }
      default:
        return null;
    }
    if (channels < 1 || sampleRate <= 0) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcm.ToArray(), channels, sampleRate, 16));
  }

  // ── plumbing ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? FromShortChannels(short[][] perChannel, int sampleRate) {
    if (perChannel.Length == 0 || perChannel[0].Length == 0) return null;
    var names = ChannelLayout.DefaultNames(perChannel.Length);
    var result = new List<ChannelWav>(perChannel.Length);
    for (var c = 0; c < perChannel.Length; ++c)
      result.Add(new ChannelWav(names[c], PcmCodec.ToWavBlob(ShortsToBytes(perChannel[c]), 1, sampleRate, 16)));
    return result;
  }

  private static byte[] ShortsToBytes(short[] samples) {
    var bytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
    return bytes;
  }

  private static IReadOnlyList<ChannelWav> Wrap(IReadOnlyList<(string Name, byte[] WavBlob)> split) {
    var result = new List<ChannelWav>(split.Count);
    foreach (var (name, wav) in split) result.Add(new ChannelWav(name, wav));
    return result;
  }

  private static string CodecLabel(int tag) => tag switch {
    0x0001 => "pcm",
    0x0003 => "pcm_float",
    0x0002 => "ms_adpcm",
    0x0011 => "ima_adpcm",
    0x0006 => "alaw",
    0x0007 => "ulaw",
    0x0050 => "mp2",
    0x0055 => "mp3",
    0x2000 => "ac3",
    0xFFFE => "extensible",
    _ => $"0x{tag:X4}",
  };
}
