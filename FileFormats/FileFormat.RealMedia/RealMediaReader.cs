#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Cook;
using Codec.Atrac3;
using Codec.Pcm;
using Codec.Ra144;
using Codec.Sipr;
using Compression.Registry;

namespace FileFormat.RealMedia;

/// <summary>
/// Read-only walker for RealMedia <c>.RMF</c> chunked containers and raw
/// <c>.ra\xFD</c> RealAudio files. RMF chunk integers are big-endian. Audio codecs
/// are identified by scanning the type-specific blob for known RealAudio FOURCCs
/// (no decoding). Parsing degrades gracefully on truncation / malformed chunks.
/// </summary>
internal static class RealMediaReader {

  private static readonly string[] KnownFourCcs =
    ["lpcJ", "28_8", "dnet", "sipr", "cook", "atrc", "raac", "ralf"];

  // ── RMF container ───────────────────────────────────────────────────────────

  internal sealed class StreamProps {
    public int StreamNumber;
    public uint MaxBitrate;
    public uint AvgBitrate;
    public uint MaxPacketSize;
    public uint AvgPacketSize;
    public uint StartTime;
    public uint Preroll;
    public uint Duration;
    public string? Description;
    public string? MimeType;
    public string? Codec;
    public byte[]? TypeSpecific;
    public readonly List<byte[]> Payloads = [];

    // RealAudio v4/v5 header fields parsed from the MDPR type-specific blob. Populated for
    // streams whose type-specific data carries an ".ra\xFD" header (cook/atrc/sipr/…).
    public int RaChannels;
    public int RaSampleRate;
    public int RaCodedFrameSize;   // coded frame size
    public int RaSubPacketH;       // interleaver height
    public int RaFrameSize;        // block_align as written (audio_framesize for cook)
    public int RaSubPacketSize;    // sub_packet_size (== cook block_align)
    public uint RaDeintId;         // 'Int0'/'Int4'/'genr'/'sipr' deinterleaver id
    public byte[]? RaExtradata;    // cook codec extradata (cookversion, subbands, …)

    public string Render() {
      var sb = new StringBuilder();
      sb.AppendLine($"stream_number = {this.StreamNumber}");
      if (this.Codec != null) sb.AppendLine($"codec = {this.Codec}");
      if (this.MimeType != null) sb.AppendLine($"mime_type = {this.MimeType}");
      if (this.Description != null) sb.AppendLine($"description = {this.Description}");
      sb.AppendLine($"max_bitrate = {this.MaxBitrate}");
      sb.AppendLine($"avg_bitrate = {this.AvgBitrate}");
      sb.AppendLine($"max_packet_size = {this.MaxPacketSize}");
      sb.AppendLine($"avg_packet_size = {this.AvgPacketSize}");
      sb.AppendLine($"start_time_ms = {this.StartTime}");
      sb.AppendLine($"preroll_ms = {this.Preroll}");
      sb.AppendLine($"duration_ms = {this.Duration}");
      sb.AppendLine($"packets = {this.Payloads.Count}");
      return sb.ToString();
    }
  }

  public static void BuildRmfEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    var streams = new Dictionary<int, StreamProps>();
    string? title = null, author = null, copyright = null, comment = null;

    try {
      var pos = 0;
      while (pos + 8 <= b.Length) {
        var fourcc = Encoding.ASCII.GetString(b, pos, 4);
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos + 4));
        // Chunk size includes the 8-byte header. Guard against degenerate sizes.
        var chunkEnd = size >= 8 && pos + size <= b.Length ? pos + size : b.Length;

        switch (fourcc) {
          case ".RMF":
            break; // header — fileVersion/numHeaders, nothing to surface
          case "PROP":
            break; // file-level properties summarised by per-stream MDPR entries
          case "MDPR":
            ParseMdpr(b, pos, chunkEnd, streams);
            break;
          case "CONT":
            ParseCont(b, pos, chunkEnd, ref title, ref author, ref copyright, ref comment);
            break;
          case "DATA":
            ParseData(b, pos, chunkEnd, streams);
            break;
          // "INDX" and anything else: skipped.
        }

        if (chunkEnd <= pos) break; // no forward progress — stop
        pos = chunkEnd;
      }
    } catch {
      // Graceful degradation — keep whatever parsed so far.
    }

    if (title != null || author != null || copyright != null || comment != null) {
      var sb = new StringBuilder();
      sb.AppendLine("[ContentDescription]");
      sb.AppendLine($"title = {title ?? ""}");
      sb.AppendLine($"author = {author ?? ""}");
      sb.AppendLine($"copyright = {copyright ?? ""}");
      sb.AppendLine($"comment = {comment ?? ""}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    foreach (var s in streams.Values.OrderBy(s => s.StreamNumber)) {
      entries.Add(new($"streams/stream_{s.StreamNumber:D2}.info.txt", "Tag",
        Encoding.UTF8.GetBytes(s.Render())));
      if (s.Payloads.Count > 0) {
        using var msx = new MemoryStream();
        foreach (var p in s.Payloads) msx.Write(p);
        var streamBytes = msx.ToArray();
        entries.Add(new($"streams/stream_{s.StreamNumber:D2}.bin", "Stream",
          streamBytes, Method: s.Codec ?? "stored"));
        // RealAudio 14.4 ("lpcJ"/"14_4"): the concatenated payloads are raw 20-byte
        // lpcJ blocks — decode them to a mono 8 kHz WAV. Falls back to blob-only.
        AddDecodedLpcJChannel(streamBytes, s.Codec, $"streams/stream_{s.StreamNumber:D2}", entries);
        // Cook / RealAudio G2: deinterleave the packets, decode every frame and surface
        // per-channel WAVs. Falls back to blob-only on any failure.
        AddDecodedCookChannels(s, $"streams/stream_{s.StreamNumber:D2}", entries);
        // RealAudio 8 ("atrc" = ATRAC3): decode the (descrambled) sub-packet stream to
        // per-channel WAVs using the RA header's atrac3 config. Falls back to blob-only.
        AddDecodedAtrac3Channels(streamBytes, s.Codec, s.TypeSpecific,
          $"streams/stream_{s.StreamNumber:D2}", entries);
        // RealAudio SIPR / ACELP.NET ("sipr"): descramble the interleaved superblock(s),
        // decode every coded frame and surface one mono WAV. Falls back to blob-only.
        AddDecodedSiprChannel(s, $"streams/stream_{s.StreamNumber:D2}", entries);
      }
    }
  }

  private static void ParseMdpr(byte[] b, int chunkStart, int chunkEnd, Dictionary<int, StreamProps> streams) {
    // header(8) | u16 objectVersion | u16 streamNumber | u32 maxBitrate | u32 avgBitrate |
    // u32 maxPacketSize | u32 avgPacketSize | u32 startTime | u32 preroll | u32 duration |
    // u8 streamDescLen + desc | u8 mimeLen + mime | u32 typeSpecificLen + typeSpecific
    var p = chunkStart + 8;
    if (p + 2 + 2 + 4 * 7 > chunkEnd) return;
    p += 2; // object version
    var s = new StreamProps {
      StreamNumber = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)),
    };
    p += 2;
    s.MaxBitrate = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.AvgBitrate = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.MaxPacketSize = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.AvgPacketSize = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.StartTime = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.Preroll = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    s.Duration = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;

    s.Description = ReadByteLenString(b, ref p, chunkEnd);
    s.MimeType = ReadByteLenString(b, ref p, chunkEnd);

    if (p + 4 <= chunkEnd) {
      var typeSpecLen = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
      var typeSpecEnd = p + typeSpecLen <= chunkEnd ? p + typeSpecLen : chunkEnd;
      s.Codec = DetectFourCc(b, p, typeSpecEnd);
      ParseRaHeader(b, p, typeSpecEnd, s);
      s.TypeSpecific = b[p..typeSpecEnd];
    }

    streams[s.StreamNumber] = s;
  }

  /// <summary>
  /// Parses the RealAudio v4/v5 header embedded in an MDPR type-specific blob, recording the
  /// interleaver framing (coded_framesize, sub_packet_h, audio_framesize, sub_packet_size,
  /// deint id), the sample rate / channels and — for cook/atrac/sipr — the trailing codec
  /// extradata. Mirrors <c>rm_read_audio_stream_info</c> field-for-field. Degrades silently:
  /// the FOURCC scan already populated <see cref="StreamProps.Codec"/>, so a parse miss only
  /// loses the decode path, not the blob view.
  /// </summary>
  private static void ParseRaHeader(byte[] b, int start, int end, StreamProps s) {
    try {
      if (start + 6 > end) return;
      // RA magic ".ra\xFD" then a big-endian u16 version.
      if (!(b[start] == 0x2E && b[start + 1] == 0x72 && b[start + 2] == 0x61 && b[start + 3] == 0xFD))
        return;
      var version = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(start + 4));
      if (version is not (4 or 5))
        return;

      // p walks the header exactly as rmdec.c's avio reads do (after the u16 version).
      var p = start + 6;
      p += 2;          // unused u16
      p += 4;          // ".ra4"/".ra5" u32
      p += 4;          // data size u32
      p += 2;          // version2 u16
      p += 4;          // header size u32
      if (p + 2 > end) return;
      p += 2;          // flavor u16
      if (p + 4 > end) return;
      s.RaCodedFrameSize = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
      p += 4;          // ??? u32
      p += 4;          // bytes_per_minute u32
      p += 4;          // ??? u32
      if (p + 6 > end) return;
      s.RaSubPacketH = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
      s.RaFrameSize = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;     // block_align as written
      s.RaSubPacketSize = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
      p += 2;          // ??? u16
      if (version == 5)
        p += 6;        // three u16
      if (p + 8 > end) return;
      s.RaSampleRate = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
      p += 4;          // ??? u32
      s.RaChannels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;

      // Deinterleaver id + codec FOURCC. v5: u32 deint_id then 4-byte interleaver tag.
      // v4: length-prefixed "desc" string holds the deint id, then a second desc holds the tag.
      string? interleaverTag;
      if (version == 5) {
        if (p + 8 > end) return;
        s.RaDeintId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        interleaverTag = Encoding.ASCII.GetString(b, p, 4); p += 4;
      } else {
        var first = ReadByteLenString(b, ref p, end);
        s.RaDeintId = first is { Length: >= 4 } ? ReadLe32Fourcc(first) : 0;
        interleaverTag = ReadByteLenString(b, ref p, end);
      }
      _ = interleaverTag;

      // For cook/atrac/sipr the codec extradata follows: u16, u8 (+u8 on v5), u32 length, data.
      if (s.Codec is "cook" or "atrc" or "sipr") {
        p += 2;        // ??? u16
        p += 1;        // ??? u8
        if (version == 5) p += 1;
        if (p + 4 > end) return;
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
        if (len > 0 && p + len <= end)
          s.RaExtradata = b[p..(p + len)];
      }
    } catch {
      // Best-effort: keep whatever was parsed.
    }
  }

  private static uint ReadLe32Fourcc(string s) {
    var bytes = Encoding.Latin1.GetBytes(s);
    uint v = 0;
    for (var i = 0; i < 4 && i < bytes.Length; ++i)
      v |= (uint)bytes[i] << (8 * i);
    return v;
  }

  private static void ParseCont(byte[] b, int chunkStart, int chunkEnd,
    ref string? title, ref string? author, ref string? copyright, ref string? comment) {
    // header(8) | u16 objectVersion | then four u16-length-prefixed (latin1) strings.
    var p = chunkStart + 8;
    if (p + 2 > chunkEnd) return;
    p += 2; // object version
    title = ReadU16LenString(b, ref p, chunkEnd);
    author = ReadU16LenString(b, ref p, chunkEnd);
    copyright = ReadU16LenString(b, ref p, chunkEnd);
    comment = ReadU16LenString(b, ref p, chunkEnd);
  }

  private static void ParseData(byte[] b, int chunkStart, int chunkEnd, Dictionary<int, StreamProps> streams) {
    // header(8) | u16 objectVersion | u32 numPackets | u32 nextDataHeader | packets.
    var p = chunkStart + 8;
    if (p + 2 + 4 + 4 > chunkEnd) return;
    p += 2; // object version
    var numPackets = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4;
    p += 4; // next DATA chunk offset

    for (var i = 0; i < numPackets && p + 12 <= chunkEnd; ++i) {
      // packet: u16 version | u16 length | u16 streamNumber | u32 timestamp |
      // u8 packetGroup | u8 flags | payload
      var version = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
      var length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 2));
      var streamNumber = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 4));
      // Header is 12 bytes for version 0/1. length covers the whole packet incl. header.
      var headerLen = 12;
      if (length < headerLen || p + length > chunkEnd) break;
      var payloadStart = p + headerLen;
      var payloadLen = length - headerLen;
      if (streams.TryGetValue(streamNumber, out var s) && payloadLen > 0)
        s.Payloads.Add(b[payloadStart..(payloadStart + payloadLen)]);
      else if (payloadLen > 0) {
        // Packet for an undeclared stream — create a placeholder so its data is kept.
        var placeholder = new StreamProps { StreamNumber = streamNumber };
        if (streams.TryAdd(streamNumber, placeholder)) s = placeholder;
        s ??= streams[streamNumber];
        s.Payloads.Add(b[payloadStart..(payloadStart + payloadLen)]);
      }
      _ = version;
      p += length;
    }
  }

  // ── raw RealAudio (.ra\xFD) ───────────────────────────────────────────────

  public static void BuildRawRaEntries(byte[] b, List<AudioPseudoArchive.Entry> entries) {
    var sb = new StringBuilder();
    sb.AppendLine("[RealAudio]");
    string? codec = null;
    int? channels = null, sampleRate = null, bits = null;
    var dataStart = b.Length;

    try {
      if (b.Length < 6) {
        FinishRaw(b, entries, sb, codec, 0);
        return;
      }
      var version = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(4));
      sb.AppendLine($"version = {version}");

      if (version == 3) {
        // v3: fixed 8000 Hz mono lpcJ. The header is a u16 size at offset 6; the lpcJ
        // audio (raw 20-byte blocks) begins at offset 8 + headerSize.
        codec = DetectFourCc(b, 0, b.Length) ?? "lpcJ";
        channels = 1; sampleRate = 8000; bits = 16;
        if (b.Length >= 8) {
          var headerSize = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6));
          var off = 8 + headerSize;
          if (off > 0 && off <= b.Length) dataStart = off;
        }
      } else if (version is 4 or 5) {
        // v4/v5 header. Common fields differ in layout; scan for the codec FOURCC
        // (it sits near the end of the fixed header) and pull rate/channels from the
        // documented offsets where they are stable across both versions.
        codec = DetectFourCc(b, 0, b.Length);
        if (version == 4) {
          // v4: sampleRate u16 @ 48, sampleSize u16 @ 52, channels u16 @ 54
          if (b.Length >= 56) {
            sampleRate = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(48));
            bits = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(52));
            channels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(54));
          }
        } else {
          // v5: sampleRate u32 @ 54, sampleSize u16 @ 58, channels u16 @ 60
          if (b.Length >= 62) {
            sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(54));
            bits = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(58));
            channels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(60));
          }
        }
        // dataOffset is u32 @ 12 in v4/v5 ("header size" / data offset).
        if (b.Length >= 16) {
          var off = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
          if (off > 0 && off <= b.Length) dataStart = off;
        }
      }
    } catch {
      // Graceful — surface what we have.
    }

    if (codec != null) sb.AppendLine($"codec = {codec}");
    if (channels != null) sb.AppendLine($"channels = {channels}");
    if (sampleRate != null) sb.AppendLine($"sample_rate = {sampleRate}");
    if (bits != null) sb.AppendLine($"bits_per_sample = {bits}");

    FinishRaw(b, entries, sb, codec, dataStart);
  }

  private static void FinishRaw(byte[] b, List<AudioPseudoArchive.Entry> entries,
    StringBuilder sb, string? codec, int dataStart) {
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
    if (dataStart >= 0 && dataStart < b.Length) {
      var data = b[dataStart..];
      entries.Add(new("streams/stream_00.bin", "Stream", data, Method: codec ?? "stored"));
      // Raw .ra v3 is always lpcJ (14.4); v4/v5 carry the codec FOURCC. Decode lpcJ to WAV.
      AddDecodedLpcJChannel(data, codec, "streams/stream_00", entries);
      // Raw .ra v4/v5 ATRAC3 ("atrc"): the leading header is the RA header itself.
      AddDecodedAtrac3Channels(data, codec, b, "streams/stream_00", entries);
    }
  }

  /// <summary>
  /// If <paramref name="codec"/> identifies RealAudio 14.4 ("lpcJ"/"14_4"), decode the
  /// raw 20-byte lpcJ block stream to a mono 8 kHz WAV entry next to the stream blob.
  /// Any decode failure (malformed/partial data) is swallowed so the blob-only view
  /// survives.
  /// </summary>
  private static void AddDecodedLpcJChannel(byte[] blocks, string? codec, string baseName,
    List<AudioPseudoArchive.Entry> entries) {
    if (codec is not ("lpcJ" or "14_4"))
      return;
    try {
      var pcm = Ra144Codec.Decode(blocks);
      if (pcm.Length == 0)
        return;
      var le = new byte[pcm.Length * 2];
      for (var i = 0; i < pcm.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), pcm[i]);
      var wav = PcmCodec.ToWavBlob(le, channels: 1, sampleRate: 8000, bitsPerSample: 16);
      entries.Add(new($"{baseName}.MONO.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable lpcJ payload — keep the stream blob only.
    }
  }

  /// <summary>
  /// If <paramref name="s"/> is a cook ("cook") stream with a parsed RA header + extradata,
  /// deinterleave the carried packets into the codec's coded-frame order, decode them with
  /// <see cref="CookCodec"/> and surface one mono <c>*.&lt;CHANNEL&gt;.wav</c> entry per
  /// channel. Any failure (unsupported flavor, malformed framing, decode error) is swallowed
  /// so the stream blob view survives.
  /// </summary>
  private static void AddDecodedCookChannels(StreamProps s, string baseName,
      List<AudioPseudoArchive.Entry> entries) {
    if (s.Codec != "cook" || s.RaExtradata is not { Length: >= 8 } || s.Payloads.Count == 0)
      return;
    try {
      var info = new CookCodec.StreamInfo {
        Channels = s.RaChannels,
        SampleRate = s.RaSampleRate,
        BlockAlign = s.RaSubPacketSize > 0 ? s.RaSubPacketSize : s.RaFrameSize,
        Extradata = s.RaExtradata,
      };
      var codec = new CookCodec(info);

      var frames = CookDeinterleaver.Reorder(s.Payloads, s.RaDeintId,
        s.RaSubPacketH, s.RaFrameSize, s.RaSubPacketSize, s.RaCodedFrameSize);
      if (frames.Length == 0)
        return;

      var pcm = codec.DecodeStream(frames);
      if (pcm.Length == 0)
        return;

      // Interleaved 16-bit PCM -> little-endian bytes, then split per channel.
      var le = new byte[pcm.Length * 2];
      for (var i = 0; i < pcm.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), pcm[i]);

      var split = PcmCodec.SplitInterleavedPcm(le, codec.Channels, s.RaSampleRate, bitsPerSample: 16);
      foreach (var (name, wav) in split)
        entries.Add(new($"{baseName}.{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable cook stream — keep the stream blob only.
    }
  }

  /// <summary>
  /// If <paramref name="s"/> is a SIPR ("sipr") stream, map the coded-frame size to a SIPR
  /// mode, descramble the carried superblock(s) with <see cref="SiprReorder"/> (the
  /// <c>DEINT_ID_SIPR</c> nibble-swap), decode every coded frame with <see cref="SiprCodec"/>
  /// and surface one mono 8 kHz WAV. Any failure (16k mode, unsupported coded-frame size,
  /// malformed framing, decode error) is swallowed so the stream blob view survives.
  /// </summary>
  private static void AddDecodedSiprChannel(StreamProps s, string baseName,
      List<AudioPseudoArchive.Entry> entries) {
    if (s.Codec != "sipr" || s.Payloads.Count == 0)
      return;
    try {
      // SIPR mode comes from the coded-frame size (block_align); 20 → 16k is unsupported.
      var modeNullable = SiprCodec.ModeFromBlockAlign(s.RaCodedFrameSize);
      if (modeNullable is not { } mode || mode == SiprCodec.SiprMode.Mode16k)
        return;
      var codec = new SiprCodec(mode);

      // RealMedia carries SIPR with DEINT_ID_SIPR: each superblock is sub_packet_h * frame_size
      // bytes of nibble-swapped data. Descramble every full superblock, then decode the
      // resulting back-to-back coded frames. If the framing isn't a clean superblock multiple,
      // fall back to decoding the concatenated payloads directly.
      using var ms = new MemoryStream();
      foreach (var p in s.Payloads) ms.Write(p);
      var raw = ms.ToArray();

      byte[] frames;
      var superBlock = s.RaSubPacketH * s.RaFrameSize;
      if (s.RaDeintId == SiprReorder.Sipr && superBlock > 0 && raw.Length >= superBlock) {
        using var reordered = new MemoryStream();
        for (var off = 0; off + superBlock <= raw.Length; off += superBlock) {
          var block = SiprReorder.Reorder(raw.AsSpan(off, superBlock), s.RaSubPacketH, s.RaFrameSize);
          reordered.Write(block);
        }
        frames = reordered.ToArray();
      } else {
        frames = raw;
      }

      var pcm = codec.DecodeStream(frames);
      if (pcm.Length == 0)
        return;

      var le = new byte[pcm.Length * 2];
      for (var i = 0; i < pcm.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), pcm[i]);

      var wav = PcmCodec.ToWavBlob(le, channels: 1, sampleRate: 8000, bitsPerSample: 16);
      entries.Add(new($"{baseName}.MONO.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable SIPR stream — keep the stream blob only.
    }
  }

  /// <summary>
  /// If <paramref name="codec"/> identifies ATRAC3 ("atrc", RealAudio 8), parse the RA v4/v5
  /// header in <paramref name="raHeader"/> for the block align (sub-packet size), channel count
  /// and ATRAC3 config (joint-stereo), then decode the RM-scrambled sub-packet stream
  /// <paramref name="payload"/> frame-by-frame into per-channel WAVs. Any failure (no parseable
  /// RA header, unsupported config, truncation) is swallowed so the blob-only view survives.
  /// <para>PRAGMATIC: RealMedia interleaves ATRAC3 sub-packets across a super-block; we decode
  /// the concatenated payload sequentially by block-align frames (each frame is one descrambled
  /// sub-packet) rather than reconstructing the original interleave order.</para>
  /// </summary>
  private static void AddDecodedAtrac3Channels(byte[] payload, string? codec, byte[]? raHeader,
      string baseName, List<AudioPseudoArchive.Entry> entries) {
    if (codec != "atrc" || raHeader == null)
      return;
    try {
      var cfg = ParseRaAtrac3Config(raHeader);
      if (cfg is not var (blockAlign, channels, sampleRate, jointStereo))
        return;
      if (blockAlign <= 0 || channels <= 0 || payload.Length < blockAlign)
        return;

      var codingMode = jointStereo ? 0x12 : 0x2;
      var decoder = new Atrac3Codec(sampleRate, channels, blockAlign, codingMode, scrambled: true);
      var interleaved = decoder.DecodeStream(payload);
      if (interleaved.Length == 0)
        return;

      var rate = sampleRate > 0 ? sampleRate : 44100;
      var le = new byte[interleaved.Length * 2];
      for (var i = 0; i < interleaved.Length; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(le.AsSpan(i * 2), interleaved[i]);

      var splits = PcmCodec.SplitInterleavedPcm(le, channels, rate, bitsPerSample: 16);
      foreach (var (name, wav) in splits)
        entries.Add(new($"{baseName}.{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Undecodable ATRAC3 payload / header — keep the stream blob only.
    }
  }

  /// <summary>
  /// Locates and parses the RealAudio v4/v5 header inside <paramref name="b"/> (it may be the
  /// MDPR type-specific blob or a raw .ra file) and extracts the ATRAC3 stream config:
  /// block align (= sub-packet size), channel count, sample rate and joint-stereo flag.
  /// Returns <see langword="null"/> if no ".ra\xFD" header with version 4/5 is found.
  /// </summary>
  private static (int BlockAlign, int Channels, int SampleRate, bool JointStereo)? ParseRaAtrac3Config(byte[] b) {
    // Find the ".ra\xFD" magic.
    var raStart = -1;
    for (var i = 0; i + 4 <= b.Length; ++i)
      if (b[i] == 0x2E && b[i + 1] == 0x72 && b[i + 2] == 0x61 && b[i + 3] == 0xFD) {
        raStart = i;
        break;
      }
    if (raStart < 0 || raStart + 6 > b.Length)
      return null;

    var version = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(raStart + 4));
    if (version is not (4 or 5))
      return null;

    // RA v4/v5 header layout (big-endian), starting after the 6-byte magic+version:
    //   skip 2 (unused) | u32 ".ra4" | u32 data size | u16 version2 | u32 header size |
    //   u16 flavor | u32 coded_framesize | u32 ??? | u32 bytes_per_minute | u32 ??? |
    //   u16 sub_packet_h | u16 frame_size(block_align) | u16 sub_packet_size | u16 ??? |
    //   [v5: u16 u16 u16] | u16 sample_rate | u32 ??? | u16 channels | ...
    var p = raStart + 6;
    p += 2;                  // unused
    p += 4;                  // ".ra4"/".ra5"
    p += 4;                  // data size
    p += 2;                  // version2
    p += 4;                  // header size
    p += 2;                  // flavor
    p += 4;                  // coded frame size
    p += 4;                  // ???
    p += 4;                  // bytes per minute
    p += 4;                  // ???
    p += 2;                  // sub_packet_h
    if (p + 2 > b.Length) return null;
    p += 2;                  // frame size (container block_align, overwritten below)
    if (p + 2 > b.Length) return null;
    var subPacketSize = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
    p += 2;                  // ???
    if (version == 5)
      p += 6;                // three u16 reserved
    if (p + 2 > b.Length) return null;
    var sampleRate = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
    p += 4;                  // ???
    if (p + 2 > b.Length) return null;
    var channels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;

    // Locate the atrac3 codec extradata (the be32 version=4 marker) after the "atrc" FOURCC.
    var jointStereo = false;
    var atrcPos = -1;
    for (var i = raStart; i + 4 <= b.Length; ++i)
      if (b[i] == (byte)'a' && b[i + 1] == (byte)'t' && b[i + 2] == (byte)'r' && b[i + 3] == (byte)'c') {
        atrcPos = i;
        break;
      }
    if (atrcPos >= 0) {
      // Scan forward for the 10/12-byte atrac3 config block: be32 version(==4),
      // be16 samples_per_frame, be16 delay(==0x88E), be16 coding_mode(0 or 1).
      for (var i = atrcPos + 4; i + 10 <= b.Length; ++i) {
        var ver = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
        if (ver != 4)
          continue;
        var delay = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 6));
        if (delay != 0x88E)
          continue;
        var codingMode = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 8));
        jointStereo = codingMode != 0;
        break;
      }
    }

    if (channels <= 0)
      channels = 2;
    return (subPacketSize, channels, sampleRate, jointStereo);
  }

  // ── helpers ────────────────────────────────────────────────────────────────

  private static string? DetectFourCc(byte[] b, int start, int end) {
    end = Math.Min(end, b.Length);
    for (var i = start; i + 4 <= end; ++i) {
      var s = Encoding.ASCII.GetString(b, i, 4);
      foreach (var fourcc in KnownFourCcs)
        if (s == fourcc) return fourcc;
    }
    return null;
  }

  private static string? ReadByteLenString(byte[] b, ref int p, int end) {
    if (p >= end) return null;
    int len = b[p]; p += 1;
    if (p + len > end) len = end - p;
    if (len < 0) return null;
    var s = Encoding.Latin1.GetString(b, p, len);
    p += len;
    return s;
  }

  private static string? ReadU16LenString(byte[] b, ref int p, int end) {
    if (p + 2 > end) return null;
    int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
    if (p + len > end) len = end - p;
    if (len <= 0) return "";
    var s = Encoding.Latin1.GetString(b, p, len);
    p += len;
    return s;
  }
}
