#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.RealMedia;

/// <summary>
/// Audio-only RMFF packet mux/remux support. The outer RMFF object model follows
/// draft-heftagaub-rmff-00; codec-specific type data is preserved verbatim when
/// remuxing an existing RealMedia stream. Fresh construction is deliberately
/// limited to AC-3/dnet, whose RealAudio v4 type header can be produced without
/// codec-private configuration.
/// </summary>
internal static class RealMediaMuxer {
  private const int PacketHeaderSize = 12;
  private const int MaxPacketPayload = ushort.MaxValue - PacketHeaderSize;
  private const string AudioDescription = "The Audio Stream";
  private const string AudioMimeType = "audio/x-pn-realaudio";

  private static readonly string[] _supportedCodecs =
    ["ac3", "dnet", "lpcJ", "28_8", "cook", "atrc", "sipr", "raac", "ralf"];

  internal static IReadOnlyList<string> SupportedCodecs => _supportedCodecs;

  internal static bool CanMux(AudioStreamFormat stream, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);

    if (!TryMapCodec(stream.CodecId, out var realMediaCodec, out var canBuildTypeSpecific)) {
      reason = $"RealMedia mux does not support codec '{stream.CodecId}'.";
      return false;
    }

    if (stream.SampleRate < 0 || stream.SampleRate > ushort.MaxValue) {
      reason = "RealMedia audio sample rate must fit in 16 bits.";
      return false;
    }

    if (stream.Channels < 0 || stream.Channels > ushort.MaxValue) {
      reason = "RealMedia audio channel count must fit in 16 bits.";
      return false;
    }

    if (canBuildTypeSpecific) {
      if (stream.SampleRate is not (32000 or 44100 or 48000)) {
        reason = "Fresh RealMedia AC-3 muxing requires a 32, 44.1, or 48 kHz stream.";
        return false;
      }
      if (stream.Channels is < 1 or > 6) {
        reason = "Fresh RealMedia AC-3 muxing supports 1 to 6 channels.";
        return false;
      }
    }

    _ = realMediaCodec;
    reason = null;
    return true;
  }

  internal static void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!CanMux(stream.Format, out var reason))
      throw new NotSupportedException(reason);

    _ = TryMapCodec(stream.Format.CodecId, out var realMediaCodec, out var canBuildTypeSpecific);
    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (packets.Length == 0)
      throw new InvalidDataException("RealMedia mux requires at least one media packet.");

    foreach (var packet in packets) {
      if (packet.Data is null)
        throw new InvalidDataException("RealMedia packet data cannot be null.");
      if (packet.Data.Length > MaxPacketPayload)
        throw new NotSupportedException($"RealMedia packet payloads are limited to {MaxPacketPayload} bytes.");
      if (realMediaCodec == "dnet" && (packet.Data.Length & 1) != 0)
        throw new NotSupportedException("RealMedia AC-3/dnet packets must contain complete 16-bit words.");
    }

    var bitRate = DetermineBitRate(stream, packets, realMediaCodec);
    var typeSpecific = ResolveTypeSpecific(stream, realMediaCodec, canBuildTypeSpecific, bitRate, packets[0].Data.Length);
    var timestamps = BuildTimestamps(stream, packets, realMediaCodec);
    var duration = DetermineDurationMilliseconds(stream, packets, timestamps, realMediaCodec);
    var maxPacketSize = (uint)packets.Max(static packet => packet.Data.Length);
    var avgPacketSize = (uint)(packets.Sum(static packet => (long)packet.Data.Length) / packets.Length);

    var cont = BuildContentDescription(options);
    var mdpr = BuildMediaProperties(typeSpecific, bitRate, maxPacketSize, avgPacketSize, duration);
    var dataOffset64 = 18L + 50L + cont.Length + mdpr.Length;
    if (dataOffset64 > uint.MaxValue)
      throw new NotSupportedException("RealMedia headers exceed the 32-bit RMFF offset limit.");

    var dataSize64 = 18L + packets.Sum(static packet => (long)PacketHeaderSize + packet.Data.Length);
    if (dataSize64 > uint.MaxValue)
      throw new NotSupportedException("RealMedia DATA chunk exceeds the 32-bit RMFF chunk-size limit.");

    WriteFourCc(output, ".RMF");
    WriteU32(output, 18);
    WriteU16(output, 0);
    WriteU32(output, 0);
    WriteU32(output, 4); // PROP, CONT, MDPR, DATA follow the RMF header.

    WriteFourCc(output, "PROP");
    WriteU32(output, 50);
    WriteU16(output, 0);
    WriteU32(output, bitRate);
    WriteU32(output, bitRate);
    WriteU32(output, maxPacketSize);
    WriteU32(output, avgPacketSize);
    WriteU32(output, checked((uint)packets.Length));
    WriteU32(output, duration);
    WriteU32(output, 0); // preroll
    WriteU32(output, 0); // index offset; this writer intentionally emits no INDX chunk
    WriteU32(output, checked((uint)dataOffset64));
    WriteU16(output, 1);
    WriteU16(output, 0x0003); // save enabled + perfect play enabled

    output.Write(cont);
    output.Write(mdpr);

    WriteFourCc(output, "DATA");
    WriteU32(output, checked((uint)dataSize64));
    WriteU16(output, 0);
    WriteU32(output, checked((uint)packets.Length));
    WriteU32(output, 0); // no chained DATA chunk

    for (var i = 0; i < packets.Length; ++i) {
      var packet = packets[i];
      WriteU16(output, 0);
      WriteU16(output, checked((ushort)(packet.Data.Length + PacketHeaderSize)));
      WriteU16(output, 0);
      WriteU32(output, timestamps[i]);
      output.WriteByte(0);
      output.WriteByte(0x02); // every audio access unit is a seekable packet boundary

      if (realMediaCodec == "dnet")
        WriteWordSwapped(output, packet.Data);
      else
        output.Write(packet.Data);
    }
  }

  internal static bool TryDemux(ReadOnlySpan<byte> file, out AudioEncodedStream? stream) {
    stream = null;
    if (file.Length < 18 || !file[..4].SequenceEqual(".RMF"u8))
      return false;

    var streams = new Dictionary<ushort, ParsedStream>();
    try {
      var offset = 0;
      while (offset + 8 <= file.Length) {
        var size = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(offset + 4, 4));
        if (size < 8 || size > int.MaxValue)
          return false;
        var end64 = (long)offset + size;
        if (end64 > file.Length)
          return false;
        var end = (int)end64;
        var id = Encoding.ASCII.GetString(file.Slice(offset, 4));

        switch (id) {
          case "MDPR":
            ParseMediaProperties(file, offset, end, streams);
            break;
          case "DATA":
            ParseData(file, offset, end, streams);
            break;
        }

        offset = end;
      }
    } catch (ArgumentOutOfRangeException) {
      return false;
    } catch (OverflowException) {
      return false;
    }

    var candidates = streams.Values
      .Where(static candidate => candidate.Codec is not null && candidate.Packets.Count > 0)
      .ToArray();
    if (candidates.Length != 1)
      return false;

    var source = candidates[0];
    var canonicalCodec = source.Codec == "dnet" ? "ac3" : source.Codec!;
    if (!TryMapCodec(canonicalCodec, out _, out _))
      return false;

    if (source.Codec == "dnet" && source.Packets.Any(static packet => (packet.Data.Length & 1) != 0))
      return false;

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["bitrate"] = source.AvgBitRate.ToString(CultureInfo.InvariantCulture),
      ["realmedia-codec"] = source.Codec!,
      ["realmedia-duration-ms"] = source.Duration.ToString(CultureInfo.InvariantCulture),
      ["realmedia-timestamp-unit"] = "milliseconds",
    };

    var packets = new AudioPacket[source.Packets.Count];
    for (var i = 0; i < source.Packets.Count; ++i) {
      var sourcePacket = source.Packets[i];
      var data = source.Codec == "dnet" ? WordSwap(sourcePacket.Data) : sourcePacket.Data.ToArray();
      packets[i] = new AudioPacket(
        data,
        DurationSamples(source, i, canonicalCodec),
        sourcePacket.Timestamp);
    }

    stream = new AudioEncodedStream(
      new AudioStreamFormat(canonicalCodec, source.SampleRate, source.Channels, Properties: properties),
      packets,
      source.TypeSpecific?.ToArray());
    return true;
  }

  private static byte[] ResolveTypeSpecific(
      AudioEncodedStream stream,
      string realMediaCodec,
      bool canBuildTypeSpecific,
      uint bitRate,
      int firstPacketSize) {
    if (stream.CodecPrivateData is { Length: > 0 } privateData) {
      var detected = DetectCodec(privateData);
      if (!string.Equals(detected, realMediaCodec, StringComparison.Ordinal))
        throw new NotSupportedException(
          $"RealMedia codec-private data does not identify the expected '{realMediaCodec}' stream.");
      return privateData.ToArray();
    }

    if (!canBuildTypeSpecific)
      throw new NotSupportedException(
        $"Fresh RealMedia '{realMediaCodec}' muxing requires preserved MDPR codec-private data from a RealMedia source.");

    return BuildAc3TypeSpecific(stream.Format.SampleRate, stream.Format.Channels, bitRate, firstPacketSize);
  }

  private static byte[] BuildAc3TypeSpecific(int sampleRate, int channels, uint bitRate, int codedFrameSize) {
    using var result = new MemoryStream(73);
    result.Write(".ra"u8);
    result.WriteByte(0xfd);
    WriteU32(result, 0x00040000);
    WriteFourCc(result, ".ra4");
    WriteU32(result, 0x01b53530);
    WriteU16(result, 4);
    WriteU32(result, 0x39);
    WriteU16(result, sampleRate switch {
      48000 => 1,
      44100 => 2,
      32000 => 3,
      _ => throw new InvalidOperationException("AC-3 sample rate was validated before header construction."),
    });
    WriteU32(result, checked((uint)codedFrameSize));
    WriteU32(result, 0x00051540);
    var bytesPerMinute = Math.Min(uint.MaxValue, (ulong)bitRate * 60 / 8);
    WriteU32(result, (uint)bytesPerMinute);
    WriteU32(result, (uint)bytesPerMinute);
    WriteU16(result, 1);
    WriteU16(result, checked((ushort)codedFrameSize));
    WriteU32(result, 0);
    WriteU16(result, checked((ushort)sampleRate));
    WriteU32(result, 0x10);
    WriteU16(result, checked((ushort)channels));
    WriteByteLengthAscii(result, "Int0");
    WriteByteLengthAscii(result, "dnet");
    WriteU16(result, 0);
    WriteU16(result, 0);
    WriteU16(result, 0);
    result.WriteByte(0);
    return result.ToArray();
  }

  private static byte[] BuildContentDescription(FormatCreateOptions options) {
    using var body = new MemoryStream();
    WriteU16(body, 0);
    WriteU16LengthLatin1(body, options.GetString("title") ?? string.Empty);
    WriteU16LengthLatin1(body, options.GetString("author") ?? string.Empty);
    WriteU16LengthLatin1(body, options.GetString("copyright") ?? string.Empty);
    WriteU16LengthLatin1(body, options.GetString("comment") ?? string.Empty);
    return WrapObject("CONT", body);
  }

  private static byte[] BuildMediaProperties(
      byte[] typeSpecific,
      uint bitRate,
      uint maxPacketSize,
      uint avgPacketSize,
      uint duration) {
    using var body = new MemoryStream();
    WriteU16(body, 0);
    WriteU16(body, 0);
    WriteU32(body, bitRate);
    WriteU32(body, bitRate);
    WriteU32(body, maxPacketSize);
    WriteU32(body, avgPacketSize);
    WriteU32(body, 0);
    WriteU32(body, 0);
    WriteU32(body, duration);
    WriteByteLengthAscii(body, AudioDescription);
    WriteByteLengthAscii(body, AudioMimeType);
    WriteU32(body, checked((uint)typeSpecific.Length));
    body.Write(typeSpecific);
    return WrapObject("MDPR", body);
  }

  private static byte[] WrapObject(string id, MemoryStream body) {
    var size = checked(body.Length + 8);
    if (size > uint.MaxValue)
      throw new NotSupportedException($"RealMedia {id} object exceeds the 32-bit RMFF chunk-size limit.");
    using var result = new MemoryStream(checked((int)size));
    WriteFourCc(result, id);
    WriteU32(result, checked((uint)size));
    body.Position = 0;
    body.CopyTo(result);
    return result.ToArray();
  }

  private static uint DetermineBitRate(AudioEncodedStream stream, AudioPacket[] packets, string realMediaCodec) {
    if (TryGetProperty(stream.Format.Properties, "bitrate", out var text)
        && uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured))
      return configured;

    var totalBytes = (ulong)packets.Sum(static packet => packet.Data.LongLength);
    ulong totalSamples = 0;
    foreach (var packet in packets) {
      var samples = EffectiveDurationSamples(packet, realMediaCodec);
      if (samples > 0)
        totalSamples += checked((ulong)samples);
    }

    if (stream.Format.SampleRate > 0 && totalSamples > 0) {
      var rate = totalBytes * 8UL * checked((uint)stream.Format.SampleRate) / totalSamples;
      return (uint)Math.Min(rate, uint.MaxValue);
    }

    if (stream.Format.SampleRate > 0
        && TryGetProperty(stream.Format.Properties, "realmedia-duration-ms", out var durationText)
        && uint.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationMs)
        && durationMs > 0) {
      var rate = totalBytes * 8000UL / durationMs;
      return (uint)Math.Min(rate, uint.MaxValue);
    }

    return 0;
  }

  private static uint[] BuildTimestamps(AudioEncodedStream stream, AudioPacket[] packets, string realMediaCodec) {
    if (TryGetProperty(stream.Format.Properties, "realmedia-timestamp-unit", out var unit)
        && unit.Equals("milliseconds", StringComparison.OrdinalIgnoreCase)
        && packets.All(static packet => packet.GranulePosition is >= 0 and <= uint.MaxValue))
      return packets.Select(static packet => checked((uint)packet.GranulePosition!.Value)).ToArray();

    var result = new uint[packets.Length];
    ulong elapsedSamples = 0;
    for (var i = 0; i < packets.Length; ++i) {
      if (stream.Format.SampleRate > 0) {
        var timestamp = elapsedSamples * 1000UL / checked((uint)stream.Format.SampleRate);
        if (timestamp > uint.MaxValue)
          throw new NotSupportedException("RealMedia timestamps exceed the 32-bit millisecond limit.");
        result[i] = (uint)timestamp;
      }

      var samples = EffectiveDurationSamples(packets[i], realMediaCodec);
      if (samples > 0)
        elapsedSamples = checked(elapsedSamples + (ulong)samples);
    }
    return result;
  }

  private static uint DetermineDurationMilliseconds(
      AudioEncodedStream stream,
      AudioPacket[] packets,
      uint[] timestamps,
      string realMediaCodec) {
    if (TryGetProperty(stream.Format.Properties, "realmedia-duration-ms", out var durationText)
        && uint.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var preserved))
      return preserved;

    var lastTimestamp = timestamps[^1];
    var finalSamples = EffectiveDurationSamples(packets[^1], realMediaCodec);
    if (stream.Format.SampleRate <= 0 || finalSamples <= 0)
      return lastTimestamp;

    var finalMs = ((ulong)finalSamples * 1000UL + (uint)stream.Format.SampleRate - 1) / (uint)stream.Format.SampleRate;
    var duration = (ulong)lastTimestamp + finalMs;
    if (duration > uint.MaxValue)
      throw new NotSupportedException("RealMedia duration exceeds the 32-bit millisecond limit.");
    return (uint)duration;
  }

  private static long EffectiveDurationSamples(AudioPacket packet, string realMediaCodec)
    => packet.DurationSamples > 0 ? packet.DurationSamples : realMediaCodec == "dnet" ? 1536 : 0;

  private static long DurationSamples(ParsedStream stream, int index, string canonicalCodec) {
    if (stream.SampleRate <= 0)
      return canonicalCodec == "ac3" ? 1536 : 0;

    var start = stream.Packets[index].Timestamp;
    var end = index + 1 < stream.Packets.Count
      ? stream.Packets[index + 1].Timestamp
      : stream.Duration;
    if (end <= start)
      return canonicalCodec == "ac3" ? 1536 : 0;
    return ((long)(end - start) * stream.SampleRate + 500) / 1000;
  }

  private static bool TryMapCodec(string codecId, out string realMediaCodec, out bool canBuildTypeSpecific) {
    canBuildTypeSpecific = false;
    switch (codecId.ToLowerInvariant()) {
      case "ac3":
      case "dnet":
        realMediaCodec = "dnet";
        canBuildTypeSpecific = true;
        return true;
      case "lpcj":
        realMediaCodec = "lpcJ";
        return true;
      case "28_8":
        realMediaCodec = "28_8";
        return true;
      case "cook":
        realMediaCodec = "cook";
        return true;
      case "atrc":
        realMediaCodec = "atrc";
        return true;
      case "sipr":
        realMediaCodec = "sipr";
        return true;
      case "raac":
        realMediaCodec = "raac";
        return true;
      case "ralf":
        realMediaCodec = "ralf";
        return true;
      default:
        realMediaCodec = string.Empty;
        return false;
    }
  }

  private static string? DetectCodec(ReadOnlySpan<byte> typeSpecific) {
    foreach (var codec in new[] { "lpcJ", "28_8", "dnet", "sipr", "cook", "atrc", "raac", "ralf" }) {
      if (typeSpecific.IndexOf(Encoding.ASCII.GetBytes(codec)) >= 0)
        return codec;
    }
    return null;
  }

  private static void ParseMediaProperties(
      ReadOnlySpan<byte> file,
      int start,
      int end,
      Dictionary<ushort, ParsedStream> streams) {
    var p = start + 8;
    if (p + 2 + 2 + 7 * 4 > end)
      return;
    p += 2;
    var streamNumber = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(p, 2));
    p += 2;
    var parsed = GetOrCreate(streams, streamNumber);
    parsed.MaxBitRate = ReadU32(file, ref p);
    parsed.AvgBitRate = ReadU32(file, ref p);
    parsed.MaxPacketSize = ReadU32(file, ref p);
    parsed.AvgPacketSize = ReadU32(file, ref p);
    parsed.StartTime = ReadU32(file, ref p);
    parsed.Preroll = ReadU32(file, ref p);
    parsed.Duration = ReadU32(file, ref p);
    SkipByteLengthString(file, ref p, end);
    parsed.MimeType = ReadByteLengthString(file, ref p, end);
    if (p + 4 > end)
      return;
    var typeLength = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p, 4));
    p += 4;
    if (typeLength > int.MaxValue || (long)p + typeLength > end)
      return;
    parsed.TypeSpecific = file.Slice(p, (int)typeLength).ToArray();
    parsed.Codec = DetectCodec(parsed.TypeSpecific);
    ParseAudioTypeSpecific(parsed.TypeSpecific, parsed);
  }

  private static void ParseData(
      ReadOnlySpan<byte> file,
      int start,
      int end,
      Dictionary<ushort, ParsedStream> streams) {
    var p = start + 8;
    if (p + 10 > end)
      return;
    p += 2;
    var packetCount = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p, 4));
    p += 8;

    for (uint i = 0; i < packetCount && p + PacketHeaderSize <= end; ++i) {
      _ = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(p, 2));
      var length = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(p + 2, 2));
      var streamNumber = BinaryPrimitives.ReadUInt16BigEndian(file.Slice(p + 4, 2));
      var timestamp = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(p + 6, 4));
      var flags = file[p + 11];
      if (length < PacketHeaderSize || p + length > end)
        return;
      var payload = file.Slice(p + PacketHeaderSize, length - PacketHeaderSize).ToArray();
      GetOrCreate(streams, streamNumber).Packets.Add(new ParsedPacket(payload, timestamp, flags));
      p += length;
    }
  }

  private static void ParseAudioTypeSpecific(byte[] data, ParsedStream stream) {
    var span = data.AsSpan();
    if (span.Length < 6 || !span[..4].SequenceEqual(new byte[] { 0x2e, 0x72, 0x61, 0xfd })) {
      if (stream.Codec is "lpcJ" or "28_8") {
        stream.SampleRate = 8000;
        stream.Channels = 1;
      }
      return;
    }

    var version = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));
    if (version is not (4 or 5))
      return;

    try {
      var p = 6;
      p += 2 + 4 + 4 + 2 + 4;
      p += 2 + 4 + 4 + 4 + 4;
      p += 2 + 2 + 2 + 2;
      if (version == 5)
        p += 6;
      if (p + 8 > span.Length)
        return;
      stream.SampleRate = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(p, 2));
      p += 6;
      stream.Channels = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(p, 2));
    } catch (ArgumentOutOfRangeException) {
      // The codec tag is still useful even when the opaque RealAudio header is truncated.
    }
  }

  private static ParsedStream GetOrCreate(Dictionary<ushort, ParsedStream> streams, ushort number) {
    if (streams.TryGetValue(number, out var stream))
      return stream;
    stream = new ParsedStream { Number = number };
    streams.Add(number, stream);
    return stream;
  }

  private static uint ReadU32(ReadOnlySpan<byte> data, ref int offset) {
    var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    offset += 4;
    return value;
  }

  private static string ReadByteLengthString(ReadOnlySpan<byte> data, ref int offset, int end) {
    if (offset >= end)
      return string.Empty;
    var length = data[offset++];
    if (offset + length > end) {
      offset = end;
      return string.Empty;
    }
    var result = Encoding.Latin1.GetString(data.Slice(offset, length));
    offset += length;
    return result;
  }

  private static void SkipByteLengthString(ReadOnlySpan<byte> data, ref int offset, int end)
    => _ = ReadByteLengthString(data, ref offset, end);

  private static bool TryGetProperty(IReadOnlyDictionary<string, string>? properties, string key, out string value) {
    if (properties is not null)
      foreach (var pair in properties)
        if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) {
          value = pair.Value;
          return true;
        }
    value = string.Empty;
    return false;
  }

  private static void WriteWordSwapped(Stream output, ReadOnlySpan<byte> data) {
    for (var i = 0; i < data.Length; i += 2) {
      output.WriteByte(data[i + 1]);
      output.WriteByte(data[i]);
    }
  }

  private static byte[] WordSwap(ReadOnlySpan<byte> data) {
    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; i += 2) {
      result[i] = data[i + 1];
      result[i + 1] = data[i];
    }
    return result;
  }

  private static void WriteByteLengthAscii(Stream output, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    if (bytes.Length > byte.MaxValue)
      throw new NotSupportedException("RealMedia byte-length string exceeds 255 bytes.");
    output.WriteByte((byte)bytes.Length);
    output.Write(bytes);
  }

  private static void WriteU16LengthLatin1(Stream output, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    if (bytes.Length > ushort.MaxValue)
      throw new NotSupportedException("RealMedia content-description string exceeds 65535 bytes.");
    WriteU16(output, (ushort)bytes.Length);
    output.Write(bytes);
  }

  private static void WriteFourCc(Stream output, string value) {
    if (value.Length != 4)
      throw new ArgumentException("FOURCC must contain exactly four characters.", nameof(value));
    Span<byte> bytes = stackalloc byte[4];
    for (var i = 0; i < 4; ++i)
      bytes[i] = checked((byte)value[i]);
    output.Write(bytes);
  }

  private static void WriteU16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteU32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    output.Write(bytes);
  }

  private sealed class ParsedStream {
    public ushort Number;
    public uint MaxBitRate;
    public uint AvgBitRate;
    public uint MaxPacketSize;
    public uint AvgPacketSize;
    public uint StartTime;
    public uint Preroll;
    public uint Duration;
    public string? MimeType;
    public string? Codec;
    public byte[]? TypeSpecific;
    public int SampleRate;
    public int Channels;
    public List<ParsedPacket> Packets { get; } = [];
  }

  private readonly record struct ParsedPacket(byte[] Data, uint Timestamp, byte Flags);
}
