#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.Ra144;
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
    public readonly List<byte[]> Payloads = [];

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
    }

    streams[s.StreamNumber] = s;
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
