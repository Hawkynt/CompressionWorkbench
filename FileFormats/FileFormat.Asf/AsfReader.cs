#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Asf;

/// <summary>
/// Minimal read-only walker for the top-level ASF objects we care about: the
/// Header Object's children (File Properties, Stream Properties, Content
/// Description, Extended Content Description, Codec List) and the Data Object.
/// All integers are little-endian. Parsing degrades gracefully: any structural
/// inconsistency stops the walk and the partial result is returned.
/// </summary>
internal static class AsfReader {

  // ── Object GUIDs (16-byte little-endian on disk) ──────────────────────────
  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] ContentDescriptionObject =
    [0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] ExtendedContentDescriptionObject =
    [0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50];
  private static readonly byte[] CodecListObject =
    [0x40, 0x52, 0xD1, 0x86, 0x1D, 0x31, 0xD0, 0x11, 0xA3, 0xA4, 0x00, 0xA0, 0xC9, 0x03, 0x48, 0xF6];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

  // Stream type GUIDs.
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] VideoStreamType =
    [0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  internal sealed class StreamInfo {
    public int StreamNumber;
    public string Kind = "unknown";
    public bool Encrypted;
    public long TimeOffset;
    public string? CodecName;
    public int? FormatTag;
    public int? Channels;
    public int? SampleRate;
    public int? BitsPerSample;
    public long? ByteRate;

    public string Render() {
      var sb = new StringBuilder();
      sb.AppendLine($"stream_number = {this.StreamNumber}");
      sb.AppendLine($"type = {this.Kind}");
      if (this.CodecName != null) sb.AppendLine($"codec = {this.CodecName}");
      if (this.FormatTag != null) sb.AppendLine($"format_tag = 0x{this.FormatTag:X4}");
      if (this.Channels != null) sb.AppendLine($"channels = {this.Channels}");
      if (this.SampleRate != null) sb.AppendLine($"sample_rate = {this.SampleRate}");
      if (this.BitsPerSample != null) sb.AppendLine($"bits_per_sample = {this.BitsPerSample}");
      if (this.ByteRate != null) sb.AppendLine($"bitrate = {this.ByteRate.Value * 8}");
      sb.AppendLine($"encrypted = {(this.Encrypted ? "true" : "false")}");
      sb.AppendLine($"time_offset_100ns = {this.TimeOffset}");
      return sb.ToString();
    }
  }

  internal sealed class Parsed {
    public ulong? FileSize;
    public ulong? CreationDate;
    public ulong? DataPacketCount;
    public ulong? PlayDuration100ns;
    public ulong? SendDuration100ns;
    public ulong? Preroll;
    public uint? MinPacketSize;
    public uint? MaxPacketSize;
    public uint? MaxBitrate;
    public string? Title;
    public string? Author;
    public string? Copyright;
    public string? DescriptionText;
    public string? Rating;
    public readonly List<(string Name, string Value)> ExtendedTags = [];
    public readonly List<string> CodecListEntries = [];
    public readonly List<StreamInfo> Streams = [];
    public byte[]? DataPayload;

    public string RenderMetadataIni() {
      var sb = new StringBuilder();
      sb.AppendLine("[FileProperties]");
      if (this.FileSize != null) sb.AppendLine($"file_size = {this.FileSize}");
      if (this.CreationDate != null) sb.AppendLine($"creation_date_filetime = {this.CreationDate}");
      if (this.DataPacketCount != null) sb.AppendLine($"data_packets = {this.DataPacketCount}");
      if (this.PlayDuration100ns != null) sb.AppendLine($"play_duration_100ns = {this.PlayDuration100ns}");
      if (this.SendDuration100ns != null) sb.AppendLine($"send_duration_100ns = {this.SendDuration100ns}");
      if (this.Preroll != null) sb.AppendLine($"preroll_ms = {this.Preroll}");
      if (this.MinPacketSize != null) sb.AppendLine($"min_packet_size = {this.MinPacketSize}");
      if (this.MaxPacketSize != null) sb.AppendLine($"max_packet_size = {this.MaxPacketSize}");
      if (this.MaxBitrate != null) sb.AppendLine($"max_bitrate = {this.MaxBitrate}");
      sb.AppendLine();
      sb.AppendLine("[ContentDescription]");
      sb.AppendLine($"title = {this.Title ?? ""}");
      sb.AppendLine($"author = {this.Author ?? ""}");
      sb.AppendLine($"copyright = {this.Copyright ?? ""}");
      sb.AppendLine($"description = {this.DescriptionText ?? ""}");
      sb.AppendLine($"rating = {this.Rating ?? ""}");
      if (this.CodecListEntries.Count > 0) {
        sb.AppendLine();
        sb.AppendLine("[CodecList]");
        for (var i = 0; i < this.CodecListEntries.Count; ++i)
          sb.AppendLine($"codec_{i} = {this.CodecListEntries[i]}");
      }
      return sb.ToString();
    }

    public string RenderTagsIni() {
      var sb = new StringBuilder();
      sb.AppendLine("[ExtendedContentDescription]");
      foreach (var (name, value) in this.ExtendedTags)
        sb.AppendLine($"{name} = {value}");
      return sb.ToString();
    }
  }

  public static Parsed Parse(byte[] blob) {
    var result = new Parsed();
    try {
      if (blob.Length < 30 || !Guid(blob, 0).SequenceEqual(HeaderObject))
        return result;

      var headerSize = BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(16));
      if (headerSize < 30 || headerSize > (ulong)blob.Length)
        headerSize = (ulong)blob.Length;
      var numObjects = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(24));

      // Child objects start at offset 30 (16 GUID + 8 size + 4 count + 2 reserved).
      var pos = 30;
      var headerEnd = (int)headerSize;
      for (var i = 0; i < numObjects && pos + 24 <= headerEnd; ++i) {
        var guid = Guid(blob, pos);
        var objSize = BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(pos + 16));
        if (objSize < 24 || pos + (long)objSize > headerEnd)
          break; // malformed — keep what we have
        var bodyStart = pos + 24;
        var bodyLen = (int)objSize - 24;

        if (guid.SequenceEqual(FilePropertiesObject))
          ParseFileProperties(result, blob, bodyStart, bodyLen);
        else if (guid.SequenceEqual(StreamPropertiesObject))
          ParseStreamProperties(result, blob, bodyStart, bodyLen);
        else if (guid.SequenceEqual(ContentDescriptionObject))
          ParseContentDescription(result, blob, bodyStart, bodyLen);
        else if (guid.SequenceEqual(ExtendedContentDescriptionObject))
          ParseExtendedContentDescription(result, blob, bodyStart, bodyLen);
        else if (guid.SequenceEqual(CodecListObject))
          ParseCodecList(result, blob, bodyStart, bodyLen);

        pos += (int)objSize;
      }

      // Data Object follows the header (it lives at the top level, after the header).
      ParseDataObject(result, blob, headerEnd);
    } catch {
      // Graceful degradation — keep whatever parsed so far.
    }
    return result;
  }

  private static void ParseFileProperties(Parsed r, byte[] b, int start, int len) {
    // body: GUID fileId(16) | u64 fileSize | u64 creationDate | u64 dataPacketsCount |
    // u64 playDuration | u64 sendDuration | u64 preroll | u32 flags |
    // u32 minDataPacketSize | u32 maxDataPacketSize | u32 maxBitrate
    if (len < 16 + 8 * 6 + 4 * 4) return;
    var p = start + 16;
    r.FileSize = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    r.CreationDate = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    r.DataPacketCount = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    r.PlayDuration100ns = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    r.SendDuration100ns = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    r.Preroll = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    p += 4; // flags
    r.MinPacketSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    r.MaxPacketSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    r.MaxBitrate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
  }

  private static void ParseStreamProperties(Parsed r, byte[] b, int start, int len) {
    // body: GUID streamType(16) | GUID errorCorrection(16) | u64 timeOffset |
    // u32 typeSpecificLen | u32 errorCorrectionLen | u16 flags | u32 reserved |
    // typeSpecific data | errorCorrection data
    if (len < 16 + 16 + 8 + 4 + 4 + 2 + 4) return;
    var info = new StreamInfo();
    var typeGuid = Guid(b, start);
    info.Kind = typeGuid.SequenceEqual(AudioStreamType) ? "audio"
              : typeGuid.SequenceEqual(VideoStreamType) ? "video"
              : "other";
    var p = start + 32;
    info.TimeOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8;
    var typeSpecificLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    p += 4; // error-correction data length
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    p += 4; // reserved
    info.StreamNumber = flags & 0x7F;
    info.Encrypted = (flags & 0x8000) != 0;

    var typeSpecStart = p;
    if (info.Kind == "audio" && typeSpecificLen >= 18 && typeSpecStart + 18 <= start + len) {
      // WAVEFORMATEX: u16 wFormatTag | u16 nChannels | u32 nSamplesPerSec |
      // u32 nAvgBytesPerSec | u16 nBlockAlign | u16 wBitsPerSample | u16 cbSize
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart));
      info.FormatTag = tag;
      info.CodecName = WaveFormatCodecName(tag);
      info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 2));
      info.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart + 4));
      info.ByteRate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart + 8));
      info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 14));
    }
    r.Streams.Add(info);
  }

  private static void ParseContentDescription(Parsed r, byte[] b, int start, int len) {
    if (len < 10) return;
    var p = start;
    int titleLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    int authorLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    int copyrightLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    int descLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    int ratingLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var end = start + len;
    r.Title = ReadUtf16(b, ref p, titleLen, end);
    r.Author = ReadUtf16(b, ref p, authorLen, end);
    r.Copyright = ReadUtf16(b, ref p, copyrightLen, end);
    r.DescriptionText = ReadUtf16(b, ref p, descLen, end);
    r.Rating = ReadUtf16(b, ref p, ratingLen, end);
  }

  private static void ParseExtendedContentDescription(Parsed r, byte[] b, int start, int len) {
    if (len < 2) return;
    var p = start;
    var end = start + len;
    int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    for (var i = 0; i < count && p + 2 <= end; ++i) {
      int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      var name = ReadUtf16(b, ref p, nameLen, end) ?? "";
      if (p + 4 > end) break;
      int valueType = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      int valueLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      if (p + valueLen > end) break;
      var value = RenderTagValue(b, p, valueLen, valueType);
      p += valueLen;
      r.ExtendedTags.Add((name, value));
    }
  }

  private static void ParseCodecList(Parsed r, byte[] b, int start, int len) {
    // body: GUID reserved(16) | u32 codecEntriesCount | entries.
    // Each entry: u16 type | u16 codecNameLen(chars) | name(UTF16) |
    // u16 codecDescLen(chars) | desc(UTF16) | u16 codecInfoLen(bytes) | info.
    if (len < 20) return;
    var p = start + 16;
    var end = start + len;
    int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    for (var i = 0; i < count && p + 2 <= end; ++i) {
      p += 2; // codec type (1=video, 2=audio)
      if (p + 2 > end) break;
      int nameChars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      var name = ReadUtf16(b, ref p, nameChars * 2, end) ?? "";
      if (p + 2 > end) break;
      int descChars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      ReadUtf16(b, ref p, descChars * 2, end);
      if (p + 2 > end) break;
      int infoBytes = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      p += infoBytes;
      if (name.Length > 0) r.CodecListEntries.Add(name);
    }
  }

  private static void ParseDataObject(Parsed r, byte[] b, int dataStart) {
    // Data Object: GUID(16) | u64 size | GUID fileId(16) | u64 totalPackets | u16 reserved | packets
    if (dataStart + 24 > b.Length) return;
    if (!Guid(b, dataStart).SequenceEqual(DataObject)) return;
    var objSize = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(dataStart + 16));
    // Packets begin after GUID(16) + size(8) + fileId(16) + totalPackets(8) + reserved(2) = 50.
    const int packetsOffset = 50;
    var objEnd = objSize >= packetsOffset && dataStart + (long)objSize <= b.Length
      ? dataStart + (int)objSize
      : b.Length;
    var payloadStart = dataStart + packetsOffset;
    if (payloadStart > objEnd) payloadStart = objEnd;
    r.DataPayload = b[payloadStart..objEnd];
  }

  // ── helpers ────────────────────────────────────────────────────────────────

  private static ReadOnlySpan<byte> Guid(byte[] b, int offset) => b.AsSpan(offset, 16);

  private static string? ReadUtf16(byte[] b, ref int p, int byteLen, int end) {
    if (byteLen <= 0) return "";
    if (p + byteLen > end) byteLen = end - p;
    if (byteLen <= 0) return "";
    var s = Encoding.Unicode.GetString(b, p, byteLen).TrimEnd('\0');
    p += byteLen;
    return s;
  }

  private static string RenderTagValue(byte[] b, int p, int len, int valueType) => valueType switch {
    0 => Encoding.Unicode.GetString(b, p, len).TrimEnd('\0'),       // Unicode string
    2 => (len >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) != 0).ToString(), // BOOL
    3 => len >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    4 => len >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    5 => len >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    _ => "0x" + Convert.ToHexString(b, p, len),                     // BYTE array / unknown
  };

  /// <summary>
  /// Maps a WAVEFORMATEX <c>wFormatTag</c> to a short codec name, mirroring the
  /// WAV descriptor's mapping. Falls back to the hex code for uncatalogued values.
  /// </summary>
  private static string WaveFormatCodecName(int formatTag) => formatTag switch {
    0x0001 => "pcm",
    0x0002 => "ms_adpcm",
    0x0003 => "pcm_float",
    0x0006 => "alaw",
    0x0007 => "mulaw",
    0x0050 => "mpeg",
    0x0055 => "mp3",
    0x0092 => "ac3",
    0x0160 => "wmav1",
    0x0161 => "wmav2",
    0x0162 => "wmapro",
    0x0163 => "wmalossless",
    0x000A => "wmavoice",
    0x2000 => "ac3_dolby",
    0x2001 => "dts",
    0xFFFE => "extensible",
    _ => $"format_0x{formatTag:X4}",
  };
}
