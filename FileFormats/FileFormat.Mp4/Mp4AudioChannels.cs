#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;

namespace FileFormat.Mp4;

/// <summary>
/// Decodes the audio tracks of an MP4/MOV file into per-speaker mono WAVs (Kind
/// <c>Channel</c>). For each audio <c>trak</c> the <c>stsd</c> sample entry selects the
/// route:
/// <list type="bullet">
///   <item><c>mp4a</c> + <c>esds</c> AudioSpecificConfig → AAC access units re-wrapped in
///     ADTS headers (object type 2, AAC-LC); object type 0x6B/0x69 → MP3/MP2 frames.</item>
///   <item><c>alac</c> → the ALAC magic cookie + frames (Apple Lossless).</item>
///   <item><c>ac-3</c>/<c>ec-3</c> → AC-3 sync frames.</item>
///   <item><c>Opus</c> + <c>dOps</c> → an Ogg-Opus stream synthesised from OpusHead + packets.</item>
///   <item><c>fLaC</c> + <c>dfLa</c> → a FLAC stream synthesised from the STREAMINFO + frames.</item>
///   <item><c>sowt</c>/<c>twos</c>/<c>lpcm</c>/<c>raw </c>/<c>in24</c>/<c>in32</c> → PCM.</item>
///   <item><c>sawb</c>/<c>samr</c> (AMR) → note only.</item>
/// </list>
/// Every route is best-effort: an unsupported codec or a decode failure leaves the raw
/// track surface intact and records a reason for the caller's metadata.
/// </summary>
internal static class Mp4AudioChannels {
  internal readonly record struct ChannelWav(string Name, byte[] Wav);

  internal sealed record AudioTrack(int TrackId, string Codec, IReadOnlyList<ChannelWav>? Channels, string? Reason);

  /// <summary>Decodes every audio trak in the file; video/other traks are ignored here.</summary>
  /// <summary>
  /// AAC-LC's decoder delay: one whole frame of warm-up precedes the first real
  /// sample, and every decoder drops it.
  /// </summary>
  private const int AacPrimingFrames = 1024;

  /// <summary>Media duration in frames, from the track's mdhd box.</summary>
  private static long ReadMediaFrames(byte[] file, BoxParser.Box mdia) {
    var mdhd = mdia.Children?.FirstOrDefault(b => b.Type == "mdhd");
    if (mdhd is null || mdhd.BodyLength < 24) return 0;

    var at = (int)mdhd.BodyOffset;
    var version = file[at];
    // version 0 packs 32-bit times, version 1 widens them to 64.
    return version == 1 && mdhd.BodyLength >= 36
      ? (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 24, 8))
      : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 16, 4));
  }

  internal static IReadOnlyList<AudioTrack> Decode(byte[] file) {
    var result = new List<AudioTrack>();
    var parser = new BoxParser();
    var boxes = parser.Parse(file);
    var moov = BoxParser.Find(boxes, "moov");
    if (moov?.Children == null) return result;

    var trackOrdinal = 0;
    foreach (var trak in moov.Children.Where(b => b.Type == "trak")) {
      var mdia = trak.Children?.FirstOrDefault(b => b.Type == "mdia");
      var hdlr = mdia?.Children?.FirstOrDefault(b => b.Type == "hdlr");
      if (mdia == null || hdlr == null) continue;
      var handlerType = Encoding.ASCII.GetString(file, (int)hdlr.BodyOffset + 8, 4);
      if (handlerType != "soun") continue;

      var minf = mdia.Children?.FirstOrDefault(b => b.Type == "minf");
      var stbl = minf?.Children?.FirstOrDefault(b => b.Type == "stbl");
      var stsd = stbl?.Children?.FirstOrDefault(b => b.Type == "stsd");
      if (stbl == null || stsd == null) { ++trackOrdinal; continue; }

      var fourcc = ReadFourCc(file, stsd);
      var sampleRate = ReadSampleRate(file, stsd);
      var samples = ReadSamples(file, stbl);
      var mediaFrames = ReadMediaFrames(file, mdia);

      var decoded = TryDecode(file, stsd, fourcc, sampleRate, samples, mediaFrames);
      result.Add(decoded with { TrackId = trackOrdinal });
      ++trackOrdinal;
    }
    return result;
  }

  private static AudioTrack TryDecode(byte[] file, BoxParser.Box stsd, string fourcc,
                                      int sampleRate, IReadOnlyList<byte[]> samples, long mediaFrames = 0) {
    var codec = fourcc.Trim();
    try {
      var channels = fourcc switch {
        "mp4a" => DecodeMp4a(file, stsd, sampleRate, samples, mediaFrames, ref codec),
        "alac" => DecodeAlac(file, stsd, samples),
        "ac-3" or "ec-3" => DecodeViaStream(Concat(samples), "ac3"),
        "Opus" => DecodeOpus(file, stsd, samples),
        "fLaC" => DecodeFlac(file, stsd, samples),
        "sowt" or "lpcm" or "twos" or "raw " or "in24" or "in32" or "NONE"
          => DecodePcm(file, stsd, fourcc, sampleRate, Concat(samples)),
        "sawb" or "samr" => null,
        _ => null,
      };
      if (channels is { Count: > 0 })
        return new AudioTrack(0, codec, channels, null);
      return new AudioTrack(0, codec, null, $"unsupported ({fourcc.Trim()})");
    } catch (Exception ex) {
      return new AudioTrack(0, codec, null, $"decode failed ({ex.GetType().Name})");
    }
  }

  // ── mp4a (AAC / MP3 / MP2 via esds) ──────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeMp4a(byte[] file, BoxParser.Box stsd, int sampleRate,
                                                       IReadOnlyList<byte[]> samples, long mediaFrames,
                                                       ref string codec) {
    var esds = FindChildBox(file, stsd, "esds");
    if (esds == null) return null;
    if (!TryParseEsds(esds, out var objectType, out var asc)) return null;

    switch (objectType) {
      case 0x40 or 0x67: { // AAC (MPEG-4 / MPEG-2 AAC LC)
        codec = "aac";
        if (asc == null || asc.Length < 2) return null;
        var (_, srIdx, channelConfig) = Codec.Aac.AacCodec.ParseAudioSpecificConfig(asc);
        var adts = AacAdtsWrapper.Wrap(samples, srIdx, channelConfig);
        return DecodeViaStream(adts, "aac", primingFrames: AacPrimingFrames, mediaFrames: mediaFrames);
      }
      case 0x6B or 0x69: { // MP3 / MP2 (MPEG-1/2 audio)
        codec = objectType == 0x6B ? "mp3" : "mp2";
        return DecodeViaStream(Concat(samples), "mp3");
      }
      default:
        codec = $"esds_0x{objectType:X2}";
        return null;
    }
  }

  // ── alac ─────────────────────────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeAlac(byte[] file, BoxParser.Box stsd, IReadOnlyList<byte[]> samples) {
    var cookieBox = FindChildBox(file, stsd, "alac");
    if (cookieBox == null) return null;
    var cookie = Codec.Alac.AlacCookie.Parse(cookieBox);
    var pcm = Codec.Alac.AlacCodec.Decode(Concat(samples), cookie);
    if (pcm.Length == 0 || cookie.NumChannels < 1) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcm, cookie.NumChannels, (int)cookie.SampleRate, cookie.BitDepth));
  }

  // ── Opus (synthesise Ogg from dOps + packets) ────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeOpus(byte[] file, BoxParser.Box stsd, IReadOnlyList<byte[]> samples) {
    var dops = FindChildBox(file, stsd, "dOps");
    if (dops == null) return null;
    var head = OpusHeadFromDOps(dops);
    var channels = head[9];
    var ogg = OggStreamWriter.Build(serial: 1, [head], samples, granuleEnd: (ulong)samples.Count * 960);
    return DecodeViaStream(ogg, "opus", channels);
  }

  /// <summary>dOps body → OpusHead packet (RFC 7845). dOps lacks the "OpusHead" magic + version byte.</summary>
  private static byte[] OpusHeadFromDOps(byte[] dops) {
    // dOps: Version(1) OutputChannelCount(1) PreSkip(2 BE) InputSampleRate(4 BE)
    //       OutputGain(2 BE) ChannelMappingFamily(1) [ChannelMapping...]
    var channels = dops.Length > 1 ? dops[1] : (byte)2;
    var preSkip = dops.Length >= 4 ? BinaryPrimitives.ReadUInt16BigEndian(dops.AsSpan(2)) : (ushort)0;
    var inputRate = dops.Length >= 8 ? BinaryPrimitives.ReadUInt32BigEndian(dops.AsSpan(4)) : 48000u;
    var gain = dops.Length >= 10 ? BinaryPrimitives.ReadInt16BigEndian(dops.AsSpan(8)) : (short)0;
    var mappingFamily = dops.Length >= 11 ? dops[10] : (byte)0;

    var tail = mappingFamily != 0 && dops.Length > 11 ? dops.Length - 11 : 0;
    var head = new byte[19 + tail];
    "OpusHead"u8.CopyTo(head);
    head[8] = 1; // version
    head[9] = channels;
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), inputRate);
    BinaryPrimitives.WriteInt16LittleEndian(head.AsSpan(16), gain);
    head[18] = mappingFamily;
    if (tail > 0) Array.Copy(dops, 11, head, 19, tail);
    return head;
  }

  // ── fLaC (synthesise a FLAC stream from dfLa STREAMINFO + frames) ─────────────

  private static IReadOnlyList<ChannelWav>? DecodeFlac(byte[] file, BoxParser.Box stsd, IReadOnlyList<byte[]> samples) {
    var dfla = FindChildBox(file, stsd, "dfLa");
    if (dfla == null) return null;
    // dfLa is a full box: version(1)+flags(3) then the raw metadata-block stream
    // (STREAMINFO etc.) exactly as it follows the "fLaC" magic.
    if (dfla.Length < 4) return null;
    var metadata = dfla.AsSpan(4).ToArray();
    using var flac = new MemoryStream();
    flac.Write("fLaC"u8);
    flac.Write(metadata);
    foreach (var s in samples) flac.Write(s);

    var props = Codec.Flac.FlacCodec.ReadAudioProperties(flac.ToArray());
    flac.Position = 0;
    using var pcm = new MemoryStream();
    Codec.Flac.FlacCodec.Decompress(flac, pcm);
    var pcmBytes = pcm.ToArray();
    if (pcmBytes.Length == 0 || props.Channels < 1) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcmBytes, props.Channels, props.SampleRate, props.BitsPerSample));
  }

  // ── PCM (sowt/twos/lpcm/raw) ──────────────────────────────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodePcm(byte[] file, BoxParser.Box stsd, string fourcc,
                                                      int sampleRate, byte[] data) {
    var (channels, bits) = ReadPcmGeometry(file, stsd);
    if (channels < 1 || bits is not (8 or 16 or 24 or 32)) return null;

    // 'twos' is big-endian, 'sowt' little-endian; the WAV split expects little-endian.
    var pcm = fourcc == "twos" && bits == 16 ? SwapEndian16(data) : data;
    return Wrap(PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, bits));
  }

  // ── shared decode-via-codec (AAC / MP3 / AC-3 / Opus) ─────────────────────────

  private static IReadOnlyList<ChannelWav>? DecodeViaStream(byte[] stream, string codec, int channelHint = 0,
                                                            int primingFrames = 0, long mediaFrames = 0) {
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
      case "opus": {
        using var info = new MemoryStream(stream, writable: false);
        var i = Codec.Opus.OpusCodec.ReadStreamInfo(info);
        channels = channelHint > 0 ? channelHint : i.Channels;
        rate = i.SampleRate;
        Codec.Opus.OpusCodec.Decompress(src, pcm);
        break;
      }
      default:
        return null;
    }
    if (channels < 1 || rate <= 0) return null;
    return Wrap(PcmCodec.SplitInterleavedPcm(
      TrimPriming(pcm.ToArray(), channels, primingFrames, mediaFrames), channels, rate, 16));
  }

  /// <summary>
  /// Drops the encoder's priming frames from the front and the padding past the
  /// track's declared length from the back.
  /// </summary>
  /// <remarks>
  /// A transform codec cannot start cold: AAC-LC hands back a whole frame of
  /// warm-up before the first real sample, and rounds the tail up to a frame.
  /// The container is what says where the audio actually is — mdhd's duration —
  /// and a decoder that skips both ends up a frame ahead of everyone else's.
  /// </remarks>
  private static byte[] TrimPriming(byte[] pcm, int channels, int primingFrames, long mediaFrames) {
    const int bytesPerSample = 2;
    var frameBytes = channels * bytesPerSample;
    if (frameBytes <= 0) return pcm;

    var priming = Math.Max(0, primingFrames);
    var start = Math.Min((long)priming * frameBytes, pcm.Length);
    var available = (pcm.Length - start) / frameBytes;

    // mdhd counts every coded frame, priming included, so what remains audible
    // after dropping the warm-up is the declared duration less that warm-up.
    var audible = mediaFrames - priming;
    var take = audible > 0 ? Math.Min(audible, available) : available;
    if (start == 0 && take == available) return pcm;

    var trimmed = new byte[take * frameBytes];
    Array.Copy(pcm, start, trimmed, 0, trimmed.Length);
    return trimmed;
  }

  // ── stsd / stbl readers ──────────────────────────────────────────────────────

  private static string ReadFourCc(byte[] file, BoxParser.Box stsd) {
    if (stsd.BodyLength < 16) return "unkn";
    return Encoding.ASCII.GetString(file, (int)stsd.BodyOffset + 12, 4);
  }

  /// <summary>Audio sample entry: ...+8 reserved, +2 channelcount, +2 samplesize, +4, +2 sr(int).16 fixed-point at +24.</summary>
  private static (int Channels, int Bits) ReadPcmGeometry(byte[] file, BoxParser.Box stsd) {
    var entry = (int)stsd.BodyOffset + 8; // skip version/flags + entry_count → first sample entry
    // sample entry: 8 (size+type) + 6 reserved + 2 data_ref + [SoundDescription]
    var sd = entry + 16; // start of SoundDescription (version/revision/vendor...)
    if (sd + 8 > file.Length) return (0, 0);
    var channels = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(sd + 8));
    var bits = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(sd + 10));
    return (channels, bits);
  }

  private static int ReadSampleRate(byte[] file, BoxParser.Box stsd) {
    var entry = (int)stsd.BodyOffset + 8;
    var sd = entry + 16;
    if (sd + 18 > file.Length) return 0;
    // sample rate is a 16.16 fixed-point at offset +16 of the SoundDescription.
    return (int)(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(sd + 16)) >> 16);
  }

  /// <summary>Returns the codec-config child box body (esds/alac/dOps/dfLa) inside the first sample entry.</summary>
  private static byte[]? FindChildBox(byte[] file, BoxParser.Box stsd, string type) {
    var pos = (int)stsd.BodyOffset + 8;
    var end = (int)(stsd.BodyOffset + stsd.BodyLength);
    if (pos + 8 > end) return null;
    var entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
    var entryEnd = Math.Min(pos + entrySize, end);
    // Scan from after the audio sample-entry prelude; layouts vary, so byte-scan for the box.
    for (var p = pos + 8 + 28; p + 8 <= entryEnd;) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
      if (size >= 8 && p + size <= entryEnd) {
        var t = Encoding.ASCII.GetString(file, p + 4, 4);
        if (t == type) return file.AsSpan(p + 8, size - 8).ToArray();
        p += size;
      } else {
        ++p;
      }
    }
    return null;
  }

  private static IReadOnlyList<byte[]> ReadSamples(byte[] file, BoxParser.Box stbl) {
    var stsz = stbl.Children!.FirstOrDefault(b => b.Type == "stsz");
    var stco = stbl.Children!.FirstOrDefault(b => b.Type == "stco");
    var co64 = stbl.Children!.FirstOrDefault(b => b.Type == "co64");
    var stsc = stbl.Children!.FirstOrDefault(b => b.Type == "stsc");
    if (stsz == null || stsc == null || (stco == null && co64 == null)) return [];

    var sizes = ReadSampleSizes(file, stsz);
    var offsets = stco != null ? ReadOffsets32(file, stco) : ReadOffsets64(file, co64!);
    var perChunk = ReadSampleToChunk(file, stsc, offsets.Count);

    var samples = new List<byte[]>(sizes.Count);
    var idx = 0;
    for (var chunk = 0; chunk < offsets.Count; ++chunk) {
      var count = perChunk[chunk];
      var off = offsets[chunk];
      for (var s = 0; s < count && idx < sizes.Count; ++s, ++idx) {
        var size = sizes[idx];
        if (off < 0 || off + size > file.Length) break;
        samples.Add(file.AsSpan((int)off, size).ToArray());
        off += size;
      }
    }
    return samples;
  }

  private static List<int> ReadSampleSizes(byte[] file, BoxParser.Box stsz) {
    var body = file.AsSpan((int)stsz.BodyOffset, (int)stsz.BodyLength);
    var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
    var sizes = new List<int>(count);
    if (fixedSize != 0) {
      for (var i = 0; i < count; ++i) sizes.Add(fixedSize);
    } else {
      for (var i = 0; i < count && 12 + 4 * i + 4 <= body.Length; ++i)
        sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 4 * i)..]));
    }
    return sizes;
  }

  private static List<long> ReadOffsets32(byte[] file, BoxParser.Box stco) {
    var body = file.AsSpan((int)stco.BodyOffset, (int)stco.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count && 8 + 4 * i + 4 <= body.Length; ++i)
      result.Add(BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 4 * i)..]));
    return result;
  }

  private static List<long> ReadOffsets64(byte[] file, BoxParser.Box co64) {
    var body = file.AsSpan((int)co64.BodyOffset, (int)co64.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count && 8 + 8 * i + 8 <= body.Length; ++i)
      result.Add((long)BinaryPrimitives.ReadUInt64BigEndian(body[(8 + 8 * i)..]));
    return result;
  }

  private static List<int> ReadSampleToChunk(byte[] file, BoxParser.Box stsc, int chunkCount) {
    var body = file.AsSpan((int)stsc.BodyOffset, (int)stsc.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var records = new List<(int FirstChunk, int SamplesPerChunk)>(count);
    for (var i = 0; i < count && 8 + 12 * i + 12 <= body.Length; ++i) {
      var fc = (int)BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 12 * i)..]);
      var spc = (int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 12 * i)..]);
      records.Add((fc, spc));
    }
    var perChunk = new List<int>(chunkCount);
    for (var c = 1; c <= chunkCount; ++c) {
      var spc = 0;
      for (var i = 0; i < records.Count; ++i)
        if (records[i].FirstChunk <= c && (i + 1 == records.Count || records[i + 1].FirstChunk > c))
          spc = records[i].SamplesPerChunk;
      perChunk.Add(spc);
    }
    return perChunk;
  }

  // ── esds parser ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Parses the esds box body (an ES_Descriptor) and extracts the
  /// objectTypeIndication and the DecoderSpecificInfo (AudioSpecificConfig).
  /// </summary>
  private static bool TryParseEsds(byte[] esds, out int objectType, out byte[]? asc) {
    objectType = 0;
    asc = null;
    // esds body: 1 version + 3 flags, then descriptor tree.
    var p = 4;
    if (!ReadDescriptor(esds, ref p, out var esTag, out var esEnd) || esTag != 0x03) return false;
    // ES_Descriptor: 2 (ES_ID) + 1 (flags) [+ optional fields]
    var flags = esds[p + 2];
    p += 3;
    if ((flags & 0x80) != 0) p += 2;   // streamDependenceFlag
    if ((flags & 0x40) != 0) { var len = esds[p]; p += 1 + len; } // URL_Flag
    if ((flags & 0x20) != 0) p += 2;   // OCRstreamFlag

    if (!ReadDescriptor(esds, ref p, out var dcTag, out var dcEnd) || dcTag != 0x04) return false;
    objectType = esds[p]; // objectTypeIndication
    p += 13; // objectType(1)+streamType/bufferSize(4)+maxBitrate(4)+avgBitrate(4)

    if (ReadDescriptor(esds, ref p, out var dsiTag, out var dsiEnd) && dsiTag == 0x05) {
      var len = dsiEnd - p;
      if (len > 0 && dsiEnd <= esds.Length) asc = esds.AsSpan(p, len).ToArray();
    }
    return true;
  }

  /// <summary>Reads an MPEG-4 descriptor tag + expandable size, leaving <paramref name="pos"/> on its body.</summary>
  private static bool ReadDescriptor(byte[] data, ref int pos, out int tag, out int end) {
    tag = 0; end = 0;
    if (pos >= data.Length) return false;
    tag = data[pos++];
    var size = 0;
    for (var i = 0; i < 4 && pos < data.Length; ++i) {
      var b = data[pos++];
      size = (size << 7) | (b & 0x7F);
      if ((b & 0x80) == 0) break;
    }
    end = pos + size;
    return true;
  }

  // ── helpers ───────────────────────────────────────────────────────────────────

  private static byte[] Concat(IReadOnlyList<byte[]> parts) {
    var total = 0;
    foreach (var p in parts) total += p.Length;
    var result = new byte[total];
    var off = 0;
    foreach (var p in parts) { p.CopyTo(result, off); off += p.Length; }
    return result;
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
}
