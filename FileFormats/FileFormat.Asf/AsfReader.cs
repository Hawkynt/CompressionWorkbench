#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Asf;

/// <summary>
/// Defensive ASF Header/Data walker used by the pseudo-archive and mux/remux path.
/// Besides the human-readable metadata view it retains the exact Stream Properties
/// bodies plus media-object boundaries/timestamps/keyframe flags required to remux
/// WMV/WMA payloads without decoding or re-encoding them.
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
    public int? BlockAlign;
    public byte[]? ExtraData;
    public int? Width;
    public int? Height;
    public string? FourCc;
    public byte[] StreamPropertiesBody = [];

    public string Render() {
      var sb = new StringBuilder();
      sb.AppendLine($"stream_number = {this.StreamNumber}");
      sb.AppendLine($"type = {this.Kind}");
      if (this.CodecName != null) sb.AppendLine($"codec = {this.CodecName}");
      if (this.FormatTag != null) sb.AppendLine($"format_tag = 0x{this.FormatTag:X4}");
      if (this.FourCc != null) sb.AppendLine($"codec_fourcc = {this.FourCc}");
      if (this.Width != null) sb.AppendLine($"width = {this.Width}");
      if (this.Height != null) sb.AppendLine($"height = {this.Height}");
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
    public readonly List<byte[]> PreservedHeaderObjects = [];
    public byte[]? DataPayload;

    /// <summary>Reassembled elementary bitstream per stream number.</summary>
    public readonly Dictionary<int, byte[]> StreamPayloads = [];

    /// <summary>Completed media-object layout per stream, retained for remuxing.</summary>
    public readonly Dictionary<int, List<AsfMediaObjectInfo>> StreamObjects = [];

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

      var pos = 30;
      var headerEnd = (int)headerSize;
      for (var i = 0; i < numObjects && pos + 24 <= headerEnd; ++i) {
        var guid = Guid(blob, pos);
        var objSize = BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(pos + 16));
        if (objSize < 24 || objSize > int.MaxValue || pos + (long)objSize > headerEnd)
          break;
        var objectLength = (int)objSize;
        var bodyStart = pos + 24;
        var bodyLen = objectLength - 24;

        if (guid.SequenceEqual(FilePropertiesObject)) {
          ParseFileProperties(result, blob, bodyStart, bodyLen);
        } else if (guid.SequenceEqual(StreamPropertiesObject)) {
          ParseStreamProperties(result, blob, bodyStart, bodyLen);
        } else {
          // Every other Header Object child is opaque remux state. That includes Codec List,
          // Header Extension (and its nested Extended Stream Properties / language metadata),
          // mutual-exclusion/bitrate objects, padding, and vendor extensions. We can parse the
          // known human-readable objects as well, but preserving their exact bytes is what makes
          // a payload-only remux metadata-lossless.
          result.PreservedHeaderObjects.Add(blob[pos..(pos + objectLength)]);
          if (guid.SequenceEqual(ContentDescriptionObject))
            ParseContentDescription(result, blob, bodyStart, bodyLen);
          else if (guid.SequenceEqual(ExtendedContentDescriptionObject))
            ParseExtendedContentDescription(result, blob, bodyStart, bodyLen);
          else if (guid.SequenceEqual(CodecListObject))
            ParseCodecList(result, blob, bodyStart, bodyLen);
        }

        pos += objectLength;
      }

      ParseDataObject(result, blob, headerEnd);
    } catch {
      // Graceful degradation — keep whatever parsed so far.
    }
    return result;
  }

  private static void ParseFileProperties(Parsed r, byte[] b, int start, int len) {
    if (len < 16 + 8 * 6 + 4 * 4)
      return;
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
    if (len < 54)
      return;

    var info = new StreamInfo {
      StreamPropertiesBody = b[start..(start + len)],
    };
    var typeGuid = Guid(b, start);
    info.Kind = typeGuid.SequenceEqual(AudioStreamType) ? "audio"
              : typeGuid.SequenceEqual(VideoStreamType) ? "video"
              : "other";

    var p = start + 32;
    info.TimeOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p))); p += 8;
    var typeSpecificLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    var errorCorrectionLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    if (typeSpecificLengthRaw > int.MaxValue || errorCorrectionLengthRaw > int.MaxValue)
      return;
    var typeSpecificLen = (int)typeSpecificLengthRaw;
    var errorCorrectionLen = (int)errorCorrectionLengthRaw;
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    p += 4; // reserved
    info.StreamNumber = flags & 0x7F;
    info.Encrypted = (flags & 0x8000) != 0;

    var typeSpecStart = p;
    var streamEnd = start + len;
    if (typeSpecStart + typeSpecificLen > streamEnd || typeSpecificLen < 0 || errorCorrectionLen < 0)
      return;

    if (info.Kind == "audio" && typeSpecificLen >= 18) {
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart));
      info.FormatTag = tag;
      info.CodecName = WaveFormatCodecName(tag);
      info.Channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 2));
      var sampleRateRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart + 4));
      if (sampleRateRaw <= int.MaxValue)
        info.SampleRate = (int)sampleRateRaw;
      info.ByteRate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart + 8));
      info.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 12));
      info.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 14));
      var cbSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 16));
      var extraStart = typeSpecStart + 18;
      if (cbSize > 0 && extraStart + cbSize <= typeSpecStart + typeSpecificLen)
        info.ExtraData = b[extraStart..(extraStart + cbSize)];
    } else if (info.Kind == "video" && typeSpecificLen >= 11) {
      var widthRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart));
      var heightRaw = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(typeSpecStart + 4));
      if (widthRaw <= int.MaxValue) info.Width = (int)widthRaw;
      if (heightRaw <= int.MaxValue) info.Height = (int)heightRaw;
      var formatDataSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(typeSpecStart + 9));
      var bitmapInfoStart = typeSpecStart + 11;
      if (formatDataSize >= 40 && bitmapInfoStart + 40 <= typeSpecStart + typeSpecificLen) {
        var fourCc = Encoding.ASCII.GetString(b, bitmapInfoStart + 16, 4);
        info.FourCc = fourCc;
        info.CodecName = VideoCodecName(fourCc);
      }
    }

    r.Streams.Add(info);
  }

  private static void ParseContentDescription(Parsed r, byte[] b, int start, int len) {
    if (len < 10)
      return;
    var p = start;
    var titleLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var authorLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var copyrightLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var descLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var ratingLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    var end = start + len;
    r.Title = ReadUtf16(b, ref p, titleLen, end);
    r.Author = ReadUtf16(b, ref p, authorLen, end);
    r.Copyright = ReadUtf16(b, ref p, copyrightLen, end);
    r.DescriptionText = ReadUtf16(b, ref p, descLen, end);
    r.Rating = ReadUtf16(b, ref p, ratingLen, end);
  }

  private static void ParseExtendedContentDescription(Parsed r, byte[] b, int start, int len) {
    if (len < 2)
      return;
    var p = start;
    var end = start + len;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
    for (var i = 0; i < count && p + 2 <= end; ++i) {
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      var name = ReadUtf16(b, ref p, nameLen, end) ?? "";
      if (p + 4 > end)
        break;
      var valueType = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      var valueLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      if (p + valueLen > end)
        break;
      var value = RenderTagValue(b, p, valueLen, valueType);
      p += valueLen;
      r.ExtendedTags.Add((name, value));
    }
  }

  private static void ParseCodecList(Parsed r, byte[] b, int start, int len) {
    if (len < 20)
      return;
    var p = start + 16;
    var end = start + len;
    var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
    for (uint i = 0; i < count && p + 2 <= end; ++i) {
      p += 2; // codec type (1=video, 2=audio)
      if (p + 2 > end)
        break;
      var nameChars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      var name = ReadUtf16(b, ref p, nameChars * 2, end) ?? "";
      if (p + 2 > end)
        break;
      var descChars = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      ReadUtf16(b, ref p, descChars * 2, end);
      if (p + 2 > end)
        break;
      var infoBytes = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
      if (p + infoBytes > end)
        break;
      p += infoBytes;
      if (name.Length > 0)
        r.CodecListEntries.Add(name);
    }
  }

  private static void ParseDataObject(Parsed r, byte[] b, int dataStart) {
    if (dataStart + 24 > b.Length || !Guid(b, dataStart).SequenceEqual(DataObject))
      return;
    var objSize = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(dataStart + 16));
    const int packetsOffset = 50;
    var objEnd = objSize >= packetsOffset && objSize <= int.MaxValue && dataStart + (long)objSize <= b.Length
      ? dataStart + (int)objSize
      : b.Length;
    var payloadStart = Math.Min(dataStart + packetsOffset, objEnd);
    r.DataPayload = b[payloadStart..objEnd];

    var packetSize = r.MinPacketSize.HasValue && r.MinPacketSize == r.MaxPacketSize
      ? checked((int)r.MinPacketSize.Value)
      : checked((int)(r.MaxPacketSize ?? 0));
    foreach (var (streamNumber, streamData) in AsfDepayloader.Depayload(r.DataPayload, packetSize)) {
      var objects = streamData.Objects
        .Select(static o => new AsfMediaObjectInfo(o.Data.Length, o.PresentationTimeMs, o.KeyFrame))
        .ToList();
      if (objects.Count > 0)
        r.StreamObjects[streamNumber] = objects;
      var blob = streamData.ToBlob();
      if (blob.Length > 0)
        r.StreamPayloads[streamNumber] = blob;
    }
  }

  private static ReadOnlySpan<byte> Guid(byte[] b, int offset) => b.AsSpan(offset, 16);

  private static string? ReadUtf16(byte[] b, ref int p, int byteLen, int end) {
    if (byteLen <= 0)
      return "";
    if (p + byteLen > end)
      byteLen = end - p;
    if (byteLen <= 0)
      return "";
    var value = Encoding.Unicode.GetString(b, p, byteLen).TrimEnd('\0');
    p += byteLen;
    return value;
  }

  private static string RenderTagValue(byte[] b, int p, int len, int valueType) => valueType switch {
    0 => Encoding.Unicode.GetString(b, p, len).TrimEnd('\0'),
    2 => (len >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) != 0).ToString(),
    3 => len >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    4 => len >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    5 => len >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)).ToString(CultureInfo.InvariantCulture) : "",
    _ => "0x" + Convert.ToHexString(b, p, len),
  };

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

  private static string VideoCodecName(string fourCc) => fourCc.ToUpperInvariant() switch {
    "WMV1" => "wmv1",
    "WMV2" => "wmv2",
    "WMV3" => "wmv3",
    "WVC1" => "vc1",
    "WMVA" => "wmva",
    _ => fourCc.TrimEnd('\0', ' ').ToLowerInvariant(),
  };
}