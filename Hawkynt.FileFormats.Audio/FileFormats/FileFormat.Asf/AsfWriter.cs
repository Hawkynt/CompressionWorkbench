#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Writes a single audio stream into a seekable ASF container without touching the encoded media objects.
/// The object and packet layouts follow the Advanced Systems Format specification; payloads larger than a
/// data packet are fragmented and carry the standard replicated media-object size/presentation-time fields.
/// </summary>
internal static class AsfWriter {
  private const int DefaultPacketSize = 4096;
  private const int MinimumPacketSize = 100;
  private const int MaximumPacketSize = ushort.MaxValue;

  // One single-payload packet using WORD packet/padding lengths, DWORD media-object number/offset,
  // a BYTE replicated-data length and the mandatory BYTE stream number.
  private const int PacketHeaderSize = 30;
  private const byte LengthTypeFlags = 0x50;
  private const byte PropertyFlags = 0x7D;

  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] HeaderExtensionObject =
    [0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11, 0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] NoErrorCorrection =
    [0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];
  private static readonly byte[] HeaderExtensionReserved1 =
    [0x11, 0xD2, 0xD3, 0xAB, 0xBA, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];

  internal static readonly string[] KnownCodecs = [
    "pcm", "ms_adpcm", "pcm_float", "alaw", "mulaw", "mpeg", "mp3", "ac3",
    "wmav1", "wmav2", "wmapro", "wmalossless", "wmavoice", "ac3_dolby", "dts", "extensible",
  ];

  internal static bool TryResolveFormatTag(AudioStreamFormat stream, out ushort formatTag) {
    if (TryReadPropertyInt(stream, "format-tag", out var configured) && configured is >= 0 and <= ushort.MaxValue) {
      formatTag = (ushort)configured;
      return true;
    }

    var tag = stream.CodecId.ToLowerInvariant() switch {
      "pcm" => 0x0001,
      "ms_adpcm" => 0x0002,
      "pcm_float" => 0x0003,
      "alaw" => 0x0006,
      "mulaw" => 0x0007,
      "wmavoice" => 0x000A,
      "mpeg" => 0x0050,
      "mp3" => 0x0055,
      "ac3" => 0x0092,
      "wmav1" => 0x0160,
      "wmav2" => 0x0161,
      "wmapro" => 0x0162,
      "wmalossless" => 0x0163,
      "ac3_dolby" => 0x2000,
      "dts" => 0x2001,
      "extensible" => 0xFFFE,
      _ => ParseFormatTagCodec(stream.CodecId),
    };

    if (tag is not (>= 0 and <= ushort.MaxValue)) {
      formatTag = 0;
      return false;
    }

    formatTag = (ushort)tag;
    return true;
  }

  internal static void Write(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!TryResolveFormatTag(stream.Format, out var formatTag))
      throw new NotSupportedException($"ASF has no WAVEFORMATEX tag for codec '{stream.Format.CodecId}'.");
    if (stream.Format.SampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(stream), "ASF audio requires a positive sample rate.");
    if (stream.Format.Channels is <= 0 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(stream), "ASF audio channel count must fit WAVEFORMATEX.");
    if (stream.Format.BitsPerSample is < 0 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(stream), "ASF audio bits per sample must fit WAVEFORMATEX.");
    if (stream.CodecPrivateData is { Length: > ushort.MaxValue })
      throw new ArgumentOutOfRangeException(nameof(stream), "ASF WAVEFORMATEX codec private data is limited to 65535 bytes.");

    var mediaObjects = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (mediaObjects.Length == 0)
      throw new ArgumentException("ASF muxing requires at least one encoded media object.", nameof(stream));
    if (mediaObjects.Any(static packet => packet.Data.Length == 0))
      throw new InvalidDataException("ASF cannot mux an empty encoded media object.");

    var packetSize = options.TryGetInt("packet-size", out var configuredPacketSize)
      ? configuredPacketSize
      : DefaultPacketSize;
    if (packetSize is < MinimumPacketSize or > MaximumPacketSize)
      throw new ArgumentOutOfRangeException(nameof(options), $"ASF packet-size must be {MinimumPacketSize}..{MaximumPacketSize} bytes.");

    var payloadCapacity = packetSize - PacketHeaderSize;
    if (payloadCapacity <= 0)
      throw new InvalidOperationException("ASF packet size leaves no room for payload data.");

    long mediaBytes = 0;
    ulong packetCount = 0;
    foreach (var mediaObject in mediaObjects) {
      mediaBytes = checked(mediaBytes + mediaObject.Data.LongLength);
      packetCount = checked(packetCount + (ulong)((mediaObject.Data.Length + payloadCapacity - 1) / payloadCapacity));
    }

    var byteRate = ResolveByteRate(stream, mediaBytes);
    var blockAlign = ResolveBlockAlign(stream, mediaObjects);
    var timeline = BuildTimeline(stream, mediaObjects, byteRate);
    var playDuration100ns = SamplesTo100Nanoseconds(timeline.MaxEndSamples, stream.Format.SampleRate);
    var maxBitrate = byteRate <= uint.MaxValue / 8 ? (uint)byteRate * 8u : uint.MaxValue;

    var streamProperties = BuildObject(StreamPropertiesObject,
      BuildStreamPropertiesBody(stream, formatTag, byteRate, blockAlign));
    var headerExtension = BuildObject(HeaderExtensionObject, BuildHeaderExtensionBody());

    const ulong filePropertiesObjectSize = 104;
    var headerSize = checked(30UL + filePropertiesObjectSize + (ulong)streamProperties.Length + (ulong)headerExtension.Length);
    var dataObjectSize = checked(50UL + packetCount * (ulong)packetSize);
    var fileSize = checked(headerSize + dataObjectSize);
    var fileId = Guid.NewGuid().ToByteArray();

    var fileProperties = BuildObject(FilePropertiesObject,
      BuildFilePropertiesBody(fileId, fileSize, packetCount, playDuration100ns, packetSize, maxBitrate));

    output.Write(HeaderObject);
    WriteU64(output, headerSize);
    WriteU32(output, 3);
    output.WriteByte(1); // ASF Header Object Reserved1
    output.WriteByte(2); // ASF Header Object Reserved2
    output.Write(fileProperties);
    output.Write(streamProperties);
    output.Write(headerExtension);

    output.Write(DataObject);
    WriteU64(output, dataObjectSize);
    output.Write(fileId);
    WriteU64(output, packetCount);
    output.WriteByte(1); // ASF Data Object reserved bytes are both 0x01.
    output.WriteByte(1);

    uint mediaObjectNumber = 0;
    for (var index = 0; index < mediaObjects.Length; ++index) {
      var mediaObject = mediaObjects[index];
      var timing = timeline.Objects[index];
      var objectLength = mediaObject.Data.Length;
      var offset = 0;
      while (offset < objectLength) {
        var fragmentLength = Math.Min(payloadCapacity, objectLength - offset);
        WritePacket(output, packetSize, mediaObjectNumber, objectLength, offset,
          timing.PresentationTimeMs, timing.DurationMs,
          mediaObject.Data.AsSpan(offset, fragmentLength));
        offset += fragmentLength;
      }
      ++mediaObjectNumber;
    }
  }

  private static byte[] BuildFilePropertiesBody(
    byte[] fileId,
    ulong fileSize,
    ulong packetCount,
    ulong playDuration100ns,
    int packetSize,
    uint maxBitrate
  ) {
    using var body = new MemoryStream(capacity: 80);
    body.Write(fileId);
    WriteU64(body, fileSize);
    WriteU64(body, (ulong)DateTime.UtcNow.ToFileTimeUtc());
    WriteU64(body, packetCount);
    WriteU64(body, playDuration100ns);
    WriteU64(body, playDuration100ns);
    WriteU64(body, 0); // preroll, milliseconds
    WriteU32(body, 0x00000002); // seekable
    WriteU32(body, (uint)packetSize);
    WriteU32(body, (uint)packetSize);
    WriteU32(body, maxBitrate);
    return body.ToArray();
  }

  private static byte[] BuildStreamPropertiesBody(
    AudioEncodedStream stream,
    ushort formatTag,
    long byteRate,
    ushort blockAlign
  ) {
    var codecPrivate = stream.CodecPrivateData ?? [];
    using var wave = new MemoryStream(capacity: 18 + codecPrivate.Length);
    WriteU16(wave, formatTag);
    WriteU16(wave, (ushort)stream.Format.Channels);
    WriteU32(wave, (uint)stream.Format.SampleRate);
    WriteU32(wave, byteRate > uint.MaxValue ? uint.MaxValue : (uint)byteRate);
    WriteU16(wave, blockAlign);
    WriteU16(wave, (ushort)stream.Format.BitsPerSample);
    WriteU16(wave, (ushort)codecPrivate.Length);
    wave.Write(codecPrivate);
    var waveBytes = wave.ToArray();

    using var body = new MemoryStream(capacity: 54 + waveBytes.Length);
    body.Write(AudioStreamType);
    body.Write(NoErrorCorrection);
    WriteU64(body, 0); // time offset
    WriteU32(body, (uint)waveBytes.Length);
    WriteU32(body, 0); // error-correction data length
    WriteU16(body, 1); // stream number 1; encrypted bit clear
    WriteU32(body, 0);
    body.Write(waveBytes);
    return body.ToArray();
  }

  private static byte[] BuildHeaderExtensionBody() {
    using var body = new MemoryStream(capacity: 22);
    body.Write(HeaderExtensionReserved1);
    WriteU16(body, 6);
    WriteU32(body, 0);
    return body.ToArray();
  }

  private static byte[] BuildObject(byte[] guid, byte[] body) {
    using var result = new MemoryStream(capacity: checked(24 + body.Length));
    result.Write(guid);
    WriteU64(result, checked((ulong)(24 + body.Length)));
    result.Write(body);
    return result.ToArray();
  }

  private static void WritePacket(
    Stream output,
    int packetSize,
    uint mediaObjectNumber,
    int mediaObjectSize,
    int mediaObjectOffset,
    uint presentationTimeMs,
    ushort durationMs,
    ReadOnlySpan<byte> fragment
  ) {
    var padding = packetSize - PacketHeaderSize - fragment.Length;
    if (padding < 0 || padding > ushort.MaxValue)
      throw new InvalidOperationException("ASF packet padding does not fit the selected packet layout.");

    output.WriteByte(LengthTypeFlags);
    output.WriteByte(PropertyFlags);
    WriteU16(output, (ushort)packetSize);
    WriteU16(output, (ushort)padding);
    WriteU32(output, presentationTimeMs); // send time
    WriteU16(output, durationMs);

    output.WriteByte(1); // stream number
    WriteU32(output, mediaObjectNumber);
    WriteU32(output, (uint)mediaObjectOffset);
    output.WriteByte(8); // replicated data length
    WriteU32(output, (uint)mediaObjectSize);
    WriteU32(output, presentationTimeMs);
    output.Write(fragment);
    WriteZeros(output, padding);
  }

  private static long ResolveByteRate(AudioEncodedStream stream, long mediaBytes) {
    if (TryReadPropertyLong(stream.Format, "byte-rate", out var configured) && configured > 0)
      return Math.Min(configured, uint.MaxValue);
    if (TryReadPropertyLong(stream.Format, "bitrate", out var bitrate) && bitrate > 0)
      return Math.Min(Math.Max(1, bitrate / 8), uint.MaxValue);

    long durationSamples = 0;
    foreach (var packet in stream.Packets)
      if (!packet.IsHeader && packet.DurationSamples > 0)
        durationSamples = checked(durationSamples + packet.DurationSamples);

    if (durationSamples > 0) {
      var scaled = (UInt128)(ulong)mediaBytes * (uint)stream.Format.SampleRate / (ulong)durationSamples;
      if (scaled == 0)
        return 1;
      return scaled > uint.MaxValue ? uint.MaxValue : (long)scaled;
    }

    return 1;
  }

  private static ushort ResolveBlockAlign(AudioEncodedStream stream, IReadOnlyList<AudioPacket> mediaObjects) {
    if (TryReadPropertyInt(stream.Format, "block-align", out var configured) && configured is > 0 and <= ushort.MaxValue)
      return (ushort)configured;

    if (stream.Format.CodecId.Equals("pcm", StringComparison.OrdinalIgnoreCase) && stream.Format.BitsPerSample > 0) {
      var bytesPerFrame = checked(stream.Format.Channels * ((stream.Format.BitsPerSample + 7) / 8));
      if (bytesPerFrame is > 0 and <= ushort.MaxValue)
        return (ushort)bytesPerFrame;
    }

    var maxObject = mediaObjects.Max(static packet => packet.Data.Length);
    return maxObject is > 0 and <= ushort.MaxValue ? (ushort)maxObject : (ushort)1;
  }

  private static Timeline BuildTimeline(AudioEncodedStream stream, IReadOnlyList<AudioPacket> mediaObjects, long byteRate) {
    var result = new List<ObjectTiming>(mediaObjects.Count);
    long cursor = 0;
    long maxEnd = 0;

    foreach (var packet in mediaObjects) {
      var start = packet.GranulePosition ?? cursor;
      if (start < 0)
        throw new InvalidDataException("ASF cannot represent a negative audio presentation position.");

      var duration = packet.DurationSamples;
      if (duration < 0)
        throw new InvalidDataException("ASF cannot represent a negative packet duration.");
      if (duration == 0 && byteRate > 0) {
        var estimate = (UInt128)(uint)packet.Data.Length * (uint)stream.Format.SampleRate / (ulong)byteRate;
        duration = estimate > (UInt128)long.MaxValue ? long.MaxValue : (long)estimate;
      }

      var end = duration > long.MaxValue - start ? long.MaxValue : start + duration;
      cursor = end;
      maxEnd = Math.Max(maxEnd, end);
      result.Add(new ObjectTiming(
        SamplesToMilliseconds32(start, stream.Format.SampleRate),
        SamplesToMilliseconds16(duration, stream.Format.SampleRate)));
    }

    return new Timeline(result, maxEnd);
  }

  private static ulong SamplesTo100Nanoseconds(long samples, int sampleRate) {
    if (samples <= 0)
      return 0;
    var scaled = (UInt128)(ulong)samples * 10_000_000u / (uint)sampleRate;
    return scaled > ulong.MaxValue ? ulong.MaxValue : (ulong)scaled;
  }

  private static uint SamplesToMilliseconds32(long samples, int sampleRate) {
    if (samples <= 0)
      return 0;
    var scaled = (UInt128)(ulong)samples * 1000u / (uint)sampleRate;
    return scaled > uint.MaxValue ? uint.MaxValue : (uint)scaled;
  }

  private static ushort SamplesToMilliseconds16(long samples, int sampleRate) {
    if (samples <= 0)
      return 0;
    var scaled = (UInt128)(ulong)samples * 1000u / (uint)sampleRate;
    return scaled > ushort.MaxValue ? ushort.MaxValue : (ushort)scaled;
  }

  private static int ParseFormatTagCodec(string codecId) {
    const string prefix = "format_0x";
    if (!codecId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return -1;
    return int.TryParse(codecId.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
      ? value
      : -1;
  }

  private static bool TryReadPropertyInt(AudioStreamFormat stream, string key, out int value) {
    value = 0;
    if (stream.Properties is not { } properties || !properties.TryGetValue(key, out var text))
      return false;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      return int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryReadPropertyLong(AudioStreamFormat stream, string key, out long value) {
    value = 0;
    if (stream.Properties is not { } properties || !properties.TryGetValue(key, out var text))
      return false;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      return long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static void WriteZeros(Stream output, int count) {
    Span<byte> zeros = stackalloc byte[256];
    while (count > 0) {
      var chunk = Math.Min(count, zeros.Length);
      output.Write(zeros[..chunk]);
      count -= chunk;
    }
  }

  private static void WriteU16(Stream output, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteU32(Stream output, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private static void WriteU64(Stream output, ulong value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
    output.Write(buffer);
  }

  private sealed record ObjectTiming(uint PresentationTimeMs, ushort DurationMs);
  private sealed record Timeline(IReadOnlyList<ObjectTiming> Objects, long MaxEndSamples);
}
