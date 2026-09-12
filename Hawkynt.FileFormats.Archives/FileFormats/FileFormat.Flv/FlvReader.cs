#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Flv;

/// <summary>
/// Reader for Flash Video (<c>.flv</c>) files per the Adobe Flash Video File Format
/// Specification, version 10.1 (Annex E: the FLV file format).
/// </summary>
/// <remarks>
/// <para>
/// An FLV file is a 9-byte header followed by a back-pointer/tag sequence: every
/// tag carries a type (audio, video or script data), a 24-bit body size, a 32-bit
/// timestamp split over a 24-bit field plus an extension byte, and a stream id
/// that is always zero. The reader walks the tags, classifies audio and video by
/// the codec nibble at the start of each body, and concatenates the per-codec
/// payloads:
/// </para>
/// <list type="bullet">
///   <item>AVC video becomes an Annex-B H.264 elementary stream: the parameter
///   sets from the <c>AVCDecoderConfigurationRecord</c> are emitted first and every
///   length-prefixed NAL unit is re-framed with a <c>00 00 00 01</c> start code.</item>
///   <item>AAC audio becomes an ADTS stream: each raw frame gets a 7-byte header
///   built from the <c>AudioSpecificConfig</c> sequence header.</item>
///   <item>MP3 frames are concatenated unchanged, which is a valid MP3 file.</item>
///   <item>Every other codec (Sorenson H.263, VP6, Screen Video, Nellymoser,
///   Speex, ADPCM, PCM …) is concatenated frame by frame with the FLV-specific
///   per-frame header bytes removed; those streams have no standard raw file form.</item>
///   <item>Script data tags are kept as raw AMF0 bodies, and the <c>onMetaData</c>
///   ECMA array is decoded into key/value pairs for the metadata summary.</item>
/// </list>
/// </remarks>
public sealed class FlvReader {

  public const int HeaderSize = 9;
  public const byte TagAudio = 8;
  public const byte TagVideo = 9;
  public const byte TagScript = 18;

  /// <summary>One demuxed stream.</summary>
  public sealed record ElementaryStream(string Kind, string Codec, string Extension, int TagCount, uint FirstTimestampMs, uint LastTimestampMs, byte[] Payload) {
    public string EntryName => $"{this.Kind}_{this.Codec}{this.Extension}";
  }

  /// <summary>One script-data tag kept as its raw AMF0 body.</summary>
  public sealed record ScriptTag(string Name, uint TimestampMs, byte[] Body);

  /// <summary>Result of parsing an FLV file.</summary>
  public sealed record FlvFile(
    int Version,
    bool HasAudioFlag,
    bool HasVideoFlag,
    int TagCount,
    uint LastTimestampMs,
    IReadOnlyList<ElementaryStream> Streams,
    IReadOnlyList<ScriptTag> Scripts,
    IReadOnlyDictionary<string, string> Metadata);

  private sealed class Accumulator {
    public string Kind = "";
    public string Codec = "";
    public string Extension = ".bin";
    public int TagCount;
    public uint FirstTimestamp;
    public uint LastTimestamp;
    public readonly MemoryStream Payload = new();
    public int NalLengthSize = 4;
    public byte[]? AacConfig;
  }

  public static FlvFile Read(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize || data[0] != (byte)'F' || data[1] != (byte)'L' || data[2] != (byte)'V')
      throw new InvalidDataException("FLV: missing 'FLV' signature.");
    var version = data[3];
    var flags = data[4];
    var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[5..]);
    if (dataOffset < HeaderSize || dataOffset > (uint)data.Length)
      throw new InvalidDataException("FLV: header DataOffset is out of range.");

    var streams = new Dictionary<string, Accumulator>();
    var scripts = new List<ScriptTag>();
    var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
    var tagCount = 0;
    uint lastTimestamp = 0;

    // The header is followed by PreviousTagSize0, then tags each followed by their size.
    var p = (int)dataOffset + 4;
    while (p + 11 <= data.Length) {
      var tagType = (byte)(data[p] & 0x1F);
      var dataSize = (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
      var timestamp = (uint)((data[p + 7] << 24) | (data[p + 4] << 16) | (data[p + 5] << 8) | data[p + 6]);
      var bodyStart = p + 11;
      var bodyEnd = bodyStart + dataSize;
      if (bodyEnd > data.Length) break;
      var body = data[bodyStart..bodyEnd];
      ++tagCount;
      lastTimestamp = Math.Max(lastTimestamp, timestamp);

      switch (tagType) {
        case TagAudio: ReadAudio(body, timestamp, streams); break;
        case TagVideo: ReadVideo(body, timestamp, streams); break;
        case TagScript:
          var name = ReadScriptName(body);
          scripts.Add(new ScriptTag(name, timestamp, body.ToArray()));
          if (name == "onMetaData") ReadOnMetaData(body, metadata);
          break;
      }
      p = bodyEnd + 4; // skip PreviousTagSize
    }

    var result = streams.Values
      .OrderBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.Codec, StringComparer.Ordinal)
      .Select(a => new ElementaryStream(a.Kind, a.Codec, a.Extension, a.TagCount, a.FirstTimestamp, a.LastTimestamp, a.Payload.ToArray()))
      .ToList();
    return new FlvFile(version, (flags & 0x04) != 0, (flags & 0x01) != 0, tagCount, lastTimestamp, result, scripts, metadata);
  }

  // ── audio ────────────────────────────────────────────────────────────────

  public static string AudioCodecName(int soundFormat) => soundFormat switch {
    0 => "pcm", 1 => "adpcm", 2 => "mp3", 3 => "pcm_le", 4 => "nellymoser16k", 5 => "nellymoser8k",
    6 => "nellymoser", 7 => "g711_alaw", 8 => "g711_ulaw", 10 => "aac", 11 => "speex", 14 => "mp3_8k", 15 => "device",
    _ => $"audio{soundFormat}",
  };

  private static void ReadAudio(ReadOnlySpan<byte> body, uint timestamp, Dictionary<string, Accumulator> streams) {
    if (body.Length < 1) return;
    var soundFormat = body[0] >> 4;
    var codec = AudioCodecName(soundFormat);
    var acc = Get(streams, "audio", codec, soundFormat switch { 2 or 14 => ".mp3", 10 => ".aac", 0 or 3 => ".pcm", _ => ".bin" });
    var payload = body[1..];

    if (soundFormat == 10) {
      if (payload.Length < 1) return;
      var packetType = payload[0];
      payload = payload[1..];
      if (packetType == 0) { acc.AacConfig = payload.ToArray(); return; }
      if (acc.AacConfig == null) return; // raw frame before the sequence header: nothing to frame it with
      WriteAdtsFrame(acc.Payload, acc.AacConfig, payload);
    } else {
      acc.Payload.Write(payload);
    }
    Touch(acc, timestamp);
  }

  /// <summary>Writes one ADTS-framed AAC access unit from an <c>AudioSpecificConfig</c> and a raw frame.</summary>
  public static void WriteAdtsFrame(Stream output, ReadOnlySpan<byte> audioSpecificConfig, ReadOnlySpan<byte> frame) {
    if (audioSpecificConfig.Length < 2) return;
    var objectType = audioSpecificConfig[0] >> 3;
    var samplingIndex = ((audioSpecificConfig[0] & 0x07) << 1) | (audioSpecificConfig[1] >> 7);
    var channelConfig = (audioSpecificConfig[1] >> 3) & 0x0F;
    var profile = Math.Clamp(objectType - 1, 0, 3);
    var frameLength = 7 + frame.Length;
    Span<byte> h = stackalloc byte[7];
    h[0] = 0xFF;
    h[1] = 0xF1; // MPEG-4, layer 0, no CRC
    h[2] = (byte)((profile << 6) | (samplingIndex << 2) | ((channelConfig >> 2) & 0x01));
    h[3] = (byte)(((channelConfig & 0x03) << 6) | ((frameLength >> 11) & 0x03));
    h[4] = (byte)((frameLength >> 3) & 0xFF);
    h[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
    h[6] = 0xFC;
    output.Write(h);
    output.Write(frame);
  }

  // ── video ────────────────────────────────────────────────────────────────

  public static string VideoCodecName(int codecId) => codecId switch {
    1 => "jpeg", 2 => "h263", 3 => "screen", 4 => "vp6", 5 => "vp6a", 6 => "screen2", 7 => "h264",
    _ => $"video{codecId}",
  };

  private static void ReadVideo(ReadOnlySpan<byte> body, uint timestamp, Dictionary<string, Accumulator> streams) {
    if (body.Length < 1) return;
    var codecId = body[0] & 0x0F;
    var frameType = body[0] >> 4;
    if (frameType == 5) return; // video info / command frame, carries no picture data
    var codec = VideoCodecName(codecId);
    var acc = Get(streams, "video", codec, codecId == 7 ? ".h264" : ".bin");
    var payload = body[1..];

    switch (codecId) {
      case 7: {
        if (payload.Length < 4) return;
        var packetType = payload[0];
        payload = payload[4..]; // AVCPacketType + 24-bit composition time
        if (packetType == 0) { WriteAvcParameterSets(acc, payload); return; }
        if (packetType != 1) return;
        WriteAnnexB(acc.Payload, payload, acc.NalLengthSize);
        break;
      }
      case 4: payload = payload.Length >= 1 ? payload[1..] : payload; acc.Payload.Write(payload); break; // VP6 adjustment byte
      case 5: payload = payload.Length >= 4 ? payload[4..] : payload; acc.Payload.Write(payload); break; // VP6A adjustment + alpha offset
      default: acc.Payload.Write(payload); break;
    }
    Touch(acc, timestamp);
  }

  private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

  /// <summary>Emits the SPS/PPS sets of an <c>AVCDecoderConfigurationRecord</c> as Annex-B NAL units and records the NAL length size.</summary>
  private static void WriteAvcParameterSets(Accumulator acc, ReadOnlySpan<byte> record) {
    if (record.Length < 6) return;
    acc.NalLengthSize = (record[4] & 0x03) + 1;
    var numSps = record[5] & 0x1F;
    var q = 6;
    for (var i = 0; i < numSps && q + 2 <= record.Length; ++i) {
      var len = BinaryPrimitives.ReadUInt16BigEndian(record[q..]);
      q += 2;
      if (q + len > record.Length) return;
      acc.Payload.Write(StartCode);
      acc.Payload.Write(record.Slice(q, len));
      q += len;
    }
    if (q >= record.Length) return;
    var numPps = record[q++];
    for (var i = 0; i < numPps && q + 2 <= record.Length; ++i) {
      var len = BinaryPrimitives.ReadUInt16BigEndian(record[q..]);
      q += 2;
      if (q + len > record.Length) return;
      acc.Payload.Write(StartCode);
      acc.Payload.Write(record.Slice(q, len));
      q += len;
    }
  }

  /// <summary>Re-frames length-prefixed NAL units with Annex-B start codes.</summary>
  public static void WriteAnnexB(Stream output, ReadOnlySpan<byte> nalUnits, int lengthSize) {
    var q = 0;
    while (q + lengthSize <= nalUnits.Length) {
      var len = 0;
      for (var i = 0; i < lengthSize; ++i) len = (len << 8) | nalUnits[q + i];
      q += lengthSize;
      if (len <= 0 || q + len > nalUnits.Length) break;
      output.Write(StartCode);
      output.Write(nalUnits.Slice(q, len));
      q += len;
    }
  }

  // ── script data (AMF0) ───────────────────────────────────────────────────

  private static string ReadScriptName(ReadOnlySpan<byte> body) {
    // AMF0: type marker 0x02 (string), u16 length, UTF-8 bytes.
    if (body.Length < 3 || body[0] != 0x02) return "script";
    var len = BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
    if (3 + len > body.Length) return "script";
    return Encoding.UTF8.GetString(body.Slice(3, len));
  }

  private static void ReadOnMetaData(ReadOnlySpan<byte> body, Dictionary<string, string> metadata) {
    var p = 3 + BinaryPrimitives.ReadUInt16BigEndian(body[1..]);
    if (p >= body.Length) return;
    var marker = body[p++];
    if (marker == 0x08) p += 4; // ECMA array: approximate count, then key/value pairs
    else if (marker != 0x03) return; // only objects and ECMA arrays carry named members
    while (p + 2 <= body.Length) {
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
      p += 2;
      if (keyLen == 0) break; // 00 00 09 object end
      if (p + keyLen > body.Length) break;
      var key = Encoding.UTF8.GetString(body.Slice(p, keyLen));
      p += keyLen;
      if (!TryReadAmf0Value(body, ref p, out var value)) break;
      if (value != null) metadata[key] = value;
    }
  }

  /// <summary>Decodes one AMF0 value; scalars are rendered as text, containers are skipped and yield null.</summary>
  private static bool TryReadAmf0Value(ReadOnlySpan<byte> data, ref int p, out string? value) {
    value = null;
    if (p >= data.Length) return false;
    var marker = data[p++];
    switch (marker) {
      case 0x00: // number
        if (p + 8 > data.Length) return false;
        value = BinaryPrimitives.ReadDoubleBigEndian(data[p..]).ToString("R", CultureInfo.InvariantCulture);
        p += 8;
        return true;
      case 0x01: // boolean
        if (p + 1 > data.Length) return false;
        value = data[p++] != 0 ? "true" : "false";
        return true;
      case 0x02: { // string
        if (p + 2 > data.Length) return false;
        var len = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
        p += 2;
        if (p + len > data.Length) return false;
        value = Encoding.UTF8.GetString(data.Slice(p, len));
        p += len;
        return true;
      }
      case 0x05: case 0x06: // null / undefined
        return true;
      case 0x0B: // date: double + s16 timezone
        if (p + 10 > data.Length) return false;
        value = BinaryPrimitives.ReadDoubleBigEndian(data[p..]).ToString("R", CultureInfo.InvariantCulture);
        p += 10;
        return true;
      case 0x0C: { // long string
        if (p + 4 > data.Length) return false;
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
        p += 4;
        if (len < 0 || p + len > data.Length) return false;
        value = Encoding.UTF8.GetString(data.Slice(p, len));
        p += len;
        return true;
      }
      case 0x03: // object
      case 0x08: { // ECMA array
        if (marker == 0x08) p += 4;
        while (p + 2 <= data.Length) {
          var keyLen = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
          p += 2;
          if (keyLen == 0) { ++p; return true; } // 00 00 09
          p += keyLen;
          if (!TryReadAmf0Value(data, ref p, out _)) return false;
        }
        return false;
      }
      case 0x0A: { // strict array
        if (p + 4 > data.Length) return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
        p += 4;
        for (uint i = 0; i < count; ++i)
          if (!TryReadAmf0Value(data, ref p, out _)) return false;
        return true;
      }
      default:
        return false;
    }
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static Accumulator Get(Dictionary<string, Accumulator> streams, string kind, string codec, string extension) {
    var key = kind + "/" + codec;
    if (!streams.TryGetValue(key, out var acc)) {
      acc = new Accumulator { Kind = kind, Codec = codec, Extension = extension };
      streams[key] = acc;
    }
    return acc;
  }

  private static void Touch(Accumulator acc, uint timestamp) {
    if (acc.TagCount == 0) acc.FirstTimestamp = timestamp;
    ++acc.TagCount;
    acc.LastTimestamp = timestamp;
  }
}
