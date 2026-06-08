#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;

namespace FileFormat.Matroska;

/// <summary>
/// Decodes a Matroska/WebM audio <c>TrackEntry</c> into per-speaker mono WAVs (Kind
/// <c>Channel</c>), routed by CodecID:
/// <list type="bullet">
///   <item><c>A_AAC</c> — CodecPrivate is the AudioSpecificConfig; blocks are wrapped in
///     ADTS and decoded as AAC-LC.</item>
///   <item><c>A_MPEG/L3</c>, <c>A_MPEG/L2</c> → MP3/MP2 frames.</item>
///   <item><c>A_AC3</c>, <c>A_EAC3</c> → AC-3 sync frames.</item>
///   <item><c>A_VORBIS</c> — CodecPrivate holds the 3 xiph-laced setup headers; an Ogg
///     stream is synthesised from those + the audio packets.</item>
///   <item><c>A_OPUS</c> — CodecPrivate is the OpusHead; an Ogg-Opus stream is synthesised.</item>
///   <item><c>A_FLAC</c> — CodecPrivate is the <c>fLaC</c> header + metadata blocks; a FLAC
///     stream is synthesised from those + the frames.</item>
///   <item><c>A_PCM/INT/LIT</c>, <c>A_PCM/INT/BIG</c>, <c>A_PCM/FLOAT/IEEE</c> → raw PCM.</item>
///   <item><c>A_MS/ACM</c> — CodecPrivate is a WAVEFORMATEX; routed by wFormatTag.</item>
/// </list>
/// Best-effort: failures keep the raw track surface and record a reason.
/// </summary>
internal static class MkvAudioChannels {
  internal readonly record struct ChannelWav(string Name, byte[] Wav);

  internal sealed record DecodeResult(string Codec, IReadOnlyList<ChannelWav>? Channels, string? Reason);

  internal static DecodeResult Decode(MkvDemuxer.Track track) {
    var codec = CodecLabel(track.CodecId);
    try {
      var channels = track.CodecId switch {
        "A_AAC" => DecodeAac(track),
        "A_MPEG/L3" or "A_MPEG/L2" or "A_MPEG/L1" => DecodeViaStream(track.FrameBytes, "mp3"),
        "A_AC3" or "A_EAC3" => DecodeViaStream(track.FrameBytes, "ac3"),
        "A_VORBIS" => DecodeVorbis(track),
        "A_OPUS" => DecodeOpus(track),
        "A_FLAC" => DecodeFlac(track),
        "A_PCM/INT/LIT" => DecodePcm(track, bigEndian: false, isFloat: false),
        "A_PCM/INT/BIG" => DecodePcm(track, bigEndian: true, isFloat: false),
        "A_PCM/FLOAT/IEEE" => DecodePcm(track, bigEndian: false, isFloat: true),
        "A_MS/ACM" => DecodeAcm(track),
        _ => null,
      };
      if (channels is { Count: > 0 })
        return new DecodeResult(codec, channels, null);
      return new DecodeResult(codec, null, $"unsupported ({track.CodecId})");
    } catch (Exception ex) {
      return new DecodeResult(codec, null, $"decode failed ({ex.GetType().Name})");
    }
  }

  // ── A_AAC ──────────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeAac(MkvDemuxer.Track track) {
    var asc = track.CodecPrivate;
    if (asc == null || asc.Length < 2) return null;
    var (_, srIdx, channelConfig) = Codec.Aac.AacCodec.ParseAudioSpecificConfig(asc);
    var units = track.Frames.Count > 0
      ? track.Frames.Select(f => f.Data).ToList()
      : [track.FrameBytes];
    var adts = AacAdtsWrapper.Wrap(units, srIdx, channelConfig);
    return DecodeViaStream(adts, "aac");
  }

  // ── A_VORBIS ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeVorbis(MkvDemuxer.Track track) {
    var headers = SplitXiphLacedHeaders(track.CodecPrivate);
    if (headers.Count != 3) return null;
    var packets = track.Frames.Count > 0
      ? track.Frames.Select(f => f.Data).ToList()
      : SplitNonEmpty(track.FrameBytes);
    var ogg = OggStreamWriter.Build(serial: 1, headers, packets, granuleEnd: (ulong)packets.Count * 1024);

    using var src = new MemoryStream(ogg, writable: false);
    using var info = new MemoryStream(ogg, writable: false);
    var meta = Codec.Vorbis.VorbisCodec.ReadStreamInfo(info);
    using var pcm = new MemoryStream();
    Codec.Vorbis.VorbisCodec.Decompress(src, pcm);
    var pcmBytes = pcm.ToArray();
    if (pcmBytes.Length == 0 || meta.Channels < 1) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcmBytes, meta.Channels, meta.SampleRate, 16));
  }

  // ── A_OPUS ───────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeOpus(MkvDemuxer.Track track) {
    var head = track.CodecPrivate;
    if (head == null || head.Length < 19) return null;
    var channels = head[9];
    var packets = track.Frames.Count > 0
      ? track.Frames.Select(f => f.Data).ToList()
      : SplitNonEmpty(track.FrameBytes);
    var ogg = OggStreamWriter.Build(serial: 1, [head], packets, granuleEnd: (ulong)packets.Count * 960);

    using var src = new MemoryStream(ogg, writable: false);
    using var info = new MemoryStream(ogg, writable: false);
    var meta = Codec.Opus.OpusCodec.ReadStreamInfo(info);
    using var pcm = new MemoryStream();
    Codec.Opus.OpusCodec.Decompress(src, pcm);
    var pcmBytes = pcm.ToArray();
    var ch = channels > 0 ? channels : meta.Channels;
    if (pcmBytes.Length == 0 || ch < 1) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcmBytes, ch, meta.SampleRate, 16));
  }

  // ── A_FLAC ───────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeFlac(MkvDemuxer.Track track) {
    var priv = track.CodecPrivate;
    if (priv == null || priv.Length < 4) return null;
    using var flac = new MemoryStream();
    // CodecPrivate normally starts with the "fLaC" magic + metadata blocks. If the magic
    // is absent (some muxers strip it), prepend it.
    if (priv[0] == 0x66 && priv[1] == 0x4C && priv[2] == 0x61 && priv[3] == 0x43) {
      flac.Write(priv);
    } else {
      flac.Write("fLaC"u8);
      flac.Write(priv);
    }
    flac.Write(track.FrameBytes);

    var props = Codec.Flac.FlacCodec.ReadAudioProperties(flac.ToArray());
    flac.Position = 0;
    using var pcm = new MemoryStream();
    Codec.Flac.FlacCodec.Decompress(flac, pcm);
    var pcmBytes = pcm.ToArray();
    if (pcmBytes.Length == 0 || props.Channels < 1) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcmBytes, props.Channels, props.SampleRate, props.BitsPerSample));
  }

  // ── A_PCM ─────────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodePcm(MkvDemuxer.Track track, bool bigEndian, bool isFloat) {
    if (track.AudioChannels < 1 || track.AudioSampleRate <= 0) return null;
    var bits = track.AudioBitDepth > 0 ? track.AudioBitDepth : 16;
    var data = bigEndian && bits == 16 ? SwapEndian16(track.FrameBytes) : track.FrameBytes;
    if (isFloat) {
      if (bits is not (32 or 64)) return null;
      return Wrap(PcmCodec.SplitInterleavedFloat(data, track.AudioChannels, track.AudioSampleRate, bits));
    }
    if (bits is not (8 or 16 or 24 or 32)) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(data, track.AudioChannels, track.AudioSampleRate, bits));
  }

  // ── A_MS/ACM (WAVEFORMATEX) ──────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeAcm(MkvDemuxer.Track track) {
    var fmt = track.CodecPrivate;
    if (fmt == null || fmt.Length < 16) return null;
    var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0));
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
    var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
    var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
    var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
    if (channels < 1 || sampleRate <= 0) return null;

    switch (tag) {
      case 0x0001:
        if (bits is not (8 or 16 or 24 or 32)) return null;
        return Wrap(PcmCodec.SplitInterleavedPcm(track.FrameBytes, channels, sampleRate, bits));
      case 0x0002: {
        if (blockAlign <= 0) return null;
        var per = Codec.MsAdpcm.MsAdpcmCodec.Decode(track.FrameBytes, blockAlign, channels);
        return FromShortChannels(per, sampleRate);
      }
      case 0x0011: {
        if (blockAlign <= 0) return null;
        var per = Codec.ImaAdpcm.ImaAdpcmCodec.Decode(track.FrameBytes, blockAlign, channels);
        return FromShortChannels(per, sampleRate);
      }
      case 0x0006:
        return Wrap(PcmCodec.SplitInterleavedPcm(ShortsToBytes(Codec.ALaw.ALawCodec.Decode(track.FrameBytes)), channels, sampleRate, 16));
      case 0x0007:
        return Wrap(PcmCodec.SplitInterleavedPcm(ShortsToBytes(Codec.MuLaw.MuLawCodec.Decode(track.FrameBytes)), channels, sampleRate, 16));
      case 0x0050 or 0x0055:
        return DecodeViaStream(track.FrameBytes, "mp3");
      case 0x2000:
        return DecodeViaStream(track.FrameBytes, "ac3");
      default:
        return null;
    }
  }

  // ── shared decode-via-codec (AAC / MP3 / AC-3) ─────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeViaStream(byte[] stream, string codec) {
    using var src = new MemoryStream(stream, writable: false);
    using var pcm = new MemoryStream();
    int channels, rate;
    switch (codec) {
      case "aac": {
        using var info = new MemoryStream(stream, writable: false);
        var i = Codec.Aac.AacCodec.ReadStreamInfo(info);
        using var rateProbe = new MemoryStream(stream, writable: false);
        rate = Codec.Aac.AacCodec.ReadCoreSampleRate(rateProbe);
        channels = i.Channels;
        Codec.Aac.AacCodec.Decompress(src, pcm);
        break;
      }
      case "mp3": {
        using var info = new MemoryStream(stream, writable: false);
        var i = Codec.Mp3.Mp3Codec.ReadStreamInfo(info);
        channels = i.Channels; rate = i.SampleRate;
        Codec.Mp3.Mp3Codec.Decompress(src, pcm);
        break;
      }
      case "ac3": {
        using var info = new MemoryStream(stream, writable: false);
        var i = Codec.Ac3.Ac3Codec.ReadStreamInfo(info);
        channels = i.Channels; rate = i.SampleRate;
        Codec.Ac3.Ac3Codec.Decompress(src, pcm);
        break;
      }
      default:
        return null;
    }
    if (channels < 1 || rate <= 0) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcm.ToArray(), channels, rate, 16));
  }

  // ── helpers ───────────────────────────────────────────────────────────────────

  /// <summary>
  /// Splits a Vorbis CodecPrivate (xiph-laced 3 headers): byte[0]=2 (count-1), then the
  /// first two header lengths as 255-summed lacing, then the three concatenated headers.
  /// </summary>
  internal static IReadOnlyList<byte[]> SplitXiphLacedHeaders(byte[]? codecPrivate) {
    if (codecPrivate == null || codecPrivate.Length < 1) return [];
    var count = codecPrivate[0] + 1;
    if (count != 3) return [];
    var p = 1;
    var len0 = 0; while (p < codecPrivate.Length && codecPrivate[p] == 255) { len0 += 255; ++p; }
    if (p < codecPrivate.Length) { len0 += codecPrivate[p]; ++p; }
    var len1 = 0; while (p < codecPrivate.Length && codecPrivate[p] == 255) { len1 += 255; ++p; }
    if (p < codecPrivate.Length) { len1 += codecPrivate[p]; ++p; }

    var len2 = codecPrivate.Length - p - len0 - len1;
    if (len0 < 0 || len1 < 0 || len2 < 0 || p + len0 + len1 + len2 > codecPrivate.Length) return [];

    var h0 = codecPrivate.AsSpan(p, len0).ToArray(); p += len0;
    var h1 = codecPrivate.AsSpan(p, len1).ToArray(); p += len1;
    var h2 = codecPrivate.AsSpan(p, len2).ToArray();
    return [h0, h1, h2];
  }

  private static List<byte[]> SplitNonEmpty(byte[] data) => data.Length > 0 ? [data] : [];

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

  private static byte[] SwapEndian16(byte[] data) {
    var swapped = new byte[data.Length];
    for (var i = 0; i + 1 < data.Length; i += 2) { swapped[i] = data[i + 1]; swapped[i + 1] = data[i]; }
    return swapped;
  }

  private static IReadOnlyList<ChannelWav> Wrap(IReadOnlyList<(string Name, byte[] WavBlob)> split) {
    var result = new List<ChannelWav>(split.Count);
    foreach (var (name, wav) in split) result.Add(new ChannelWav(name, wav));
    return result;
  }

  private static string CodecLabel(string codecId) => codecId switch {
    "A_AAC" => "aac",
    "A_MPEG/L3" => "mp3",
    "A_MPEG/L2" => "mp2",
    "A_MPEG/L1" => "mp1",
    "A_AC3" => "ac3",
    "A_EAC3" => "eac3",
    "A_VORBIS" => "vorbis",
    "A_OPUS" => "opus",
    "A_FLAC" => "flac",
    "A_PCM/INT/LIT" or "A_PCM/INT/BIG" or "A_PCM/FLOAT/IEEE" => "pcm",
    "A_MS/ACM" => "acm",
    _ => codecId,
  };
}
