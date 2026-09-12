#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// Packet-preserving Matroska audio muxer. Encoded access units are copied verbatim into
/// SimpleBlocks; only EBML/container metadata and timestamps are rebuilt.
/// </summary>
internal static class MkvAudioMuxer {
  private const ulong TimestampScaleNanoseconds = 1_000_000;
  private const long ClusterDurationMilliseconds = 5_000;
  private const int OpusPacketClockRate = 48_000;
  private const string WriterName = "CompressionWorkbench";

  private static readonly string[] Codecs = [
    "aac", "aac-lc", "mp2", "mp3", "ac3", "eac3", "e-ac-3", "flac", "opus", "vorbis",
  ];

  internal static IReadOnlyList<string> SupportedCodecs => Codecs;

  internal static bool CanMux(AudioStreamFormat format, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);

    if (!Codecs.Contains(format.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"Matroska audio muxing does not support codec '{format.CodecId}'.";
      return false;
    }
    if (format.SampleRate <= 0) {
      reason = "Matroska audio muxing requires a positive sample rate.";
      return false;
    }
    if (format.Channels is < 1 or > 255) {
      reason = "Matroska audio muxing requires between 1 and 255 channels.";
      return false;
    }
    if (format.BitsPerSample < 0) {
      reason = "Matroska audio bit depth cannot be negative.";
      return false;
    }

    reason = null;
    return true;
  }

  internal static void Mux(Stream output, AudioEncodedStream stream) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    if (!CanMux(stream.Format, out var reason))
      throw new NotSupportedException(reason);

    var codec = NormalizeCodec(stream.Format.CodecId);
    ValidateCodecPrivate(codec, stream);
    var packetClockRate = codec == "opus" ? OpusPacketClockRate : stream.Format.SampleRate;

    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (packets.Length == 0)
      throw new ArgumentException("Matroska muxing requires at least one encoded packet.", nameof(stream));

    var durations = new long[packets.Length];
    for (var i = 0; i < packets.Length; ++i) {
      if (packets[i].Data.Length == 0)
        throw new InvalidDataException("Matroska cannot mux an empty encoded packet.");

      durations[i] = packets[i].DurationSamples > 0
        ? packets[i].DurationSamples
        : InferDurationSamples(codec, packets[i].Data);
      if (durations[i] <= 0)
        throw new InvalidDataException(
          $"Matroska muxing codec '{stream.Format.CodecId}' requires DurationSamples on every packet whose duration cannot be inferred from the packet header.");
    }

    using var segment = new MemoryStream();
    segment.Write(BuildInfo(packetClockRate, durations.Sum()));
    segment.Write(BuildTracks(stream));

    var cues = new List<(long Time, long Position)>();
    long cumulativeSamples = 0;
    var packetIndex = 0;
    while (packetIndex < packets.Length) {
      var clusterTimestamp = SamplesToMilliseconds(cumulativeSamples, packetClockRate);
      var clusterPosition = segment.Position;
      using var clusterBody = new MemoryStream();
      WriteUnsignedElement(clusterBody, 0xE7, (ulong)clusterTimestamp); // Timestamp
      cues.Add((clusterTimestamp, clusterPosition));

      while (packetIndex < packets.Length) {
        var packetTimestamp = SamplesToMilliseconds(cumulativeSamples, packetClockRate);
        var relative = packetTimestamp - clusterTimestamp;
        if (relative >= ClusterDurationMilliseconds || relative > short.MaxValue)
          break;

        WriteSimpleBlock(clusterBody, checked((short)relative), packets[packetIndex].Data);
        cumulativeSamples = checked(cumulativeSamples + durations[packetIndex]);
        ++packetIndex;
      }

      WriteElement(segment, 0x1F43B675, clusterBody.ToArray()); // Cluster
    }

    segment.Write(BuildCues(cues));

    WriteElement(output, 0x1A45DFA3, BuildEbmlHeader()); // EBML
    WriteElement(output, 0x18538067, segment.ToArray());  // Segment
  }

  internal static long InferDurationSamples(string codecId, ReadOnlySpan<byte> packet) {
    var codec = NormalizeCodec(codecId);
    return codec switch {
      "aac" => 1024,
      "mp2" => 1152,
      "mp3" => MpegAudioFrameSamples(packet),
      "ac3" => 1536,
      "eac3" => Eac3FrameSamples(packet),
      "opus" => OpusPacketSamples(packet),
      _ => 0,
    };
  }

  private static byte[] BuildEbmlHeader() {
    using var body = new MemoryStream();
    WriteUnsignedElement(body, 0x4286, 1); // EBMLVersion
    WriteUnsignedElement(body, 0x42F7, 1); // EBMLReadVersion
    WriteUnsignedElement(body, 0x42F2, 4); // EBMLMaxIDLength
    WriteUnsignedElement(body, 0x42F3, 8); // EBMLMaxSizeLength
    WriteStringElement(body, 0x4282, "matroska");
    WriteUnsignedElement(body, 0x4287, 4); // DocTypeVersion
    WriteUnsignedElement(body, 0x4285, 4); // DocTypeReadVersion
    return body.ToArray();
  }

  private static byte[] BuildInfo(int sampleRate, long totalSamples) {
    using var body = new MemoryStream();
    WriteUnsignedElement(body, 0x2AD7B1, TimestampScaleNanoseconds);
    if (totalSamples > 0)
      WriteFloatElement(body, 0x4489, totalSamples * 1000d / sampleRate); // Duration in Segment ticks (ms)
    WriteStringElement(body, 0x4D80, WriterName); // MuxingApp
    WriteStringElement(body, 0x5741, WriterName); // WritingApp
    return ElementBytes(0x1549A966, body.ToArray()); // Info
  }

  private static byte[] BuildTracks(AudioEncodedStream stream) {
    using var track = new MemoryStream();
    WriteUnsignedElement(track, 0xD7, 1);   // TrackNumber
    WriteUnsignedElement(track, 0x73C5, 1); // TrackUID
    WriteUnsignedElement(track, 0x83, 2);   // TrackType = audio
    WriteUnsignedElement(track, 0x9C, 0);   // FlagLacing = false
    WriteStringElement(track, 0x22B59C, Property(stream.Format, "language") ?? "und");

    var codec = NormalizeCodec(stream.Format.CodecId);
    WriteStringElement(track, 0x86, MatroskaCodecId(codec));
    if (stream.CodecPrivateData is { Length: > 0 } codecPrivate)
      WriteElement(track, 0x63A2, codecPrivate);

    if (codec == "opus") {
      var head = stream.CodecPrivateData!;
      var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10, 2));
      var delay = checked((ulong)preSkip * 1_000_000_000UL / OpusPacketClockRate);
      WriteUnsignedElement(track, 0x56AA, delay);       // CodecDelay
      WriteUnsignedElement(track, 0x56BB, 80_000_000); // SeekPreRoll = 80 ms
    }

    using var audio = new MemoryStream();
    var sampleRate = stream.Format.SampleRate;
    if (codec == "opus") {
      var inputRate = BinaryPrimitives.ReadUInt32LittleEndian(stream.CodecPrivateData!.AsSpan(12, 4));
      if (inputRate != 0)
        sampleRate = checked((int)inputRate);
    }
    WriteFloatElement(audio, 0xB5, sampleRate);               // SamplingFrequency
    WriteUnsignedElement(audio, 0x9F, (ulong)stream.Format.Channels);
    if (stream.Format.BitsPerSample > 0)
      WriteUnsignedElement(audio, 0x6264, (ulong)stream.Format.BitsPerSample);
    WriteElement(track, 0xE1, audio.ToArray());

    using var tracks = new MemoryStream();
    WriteElement(tracks, 0xAE, track.ToArray());
    return ElementBytes(0x1654AE6B, tracks.ToArray());
  }

  private static byte[] BuildCues(IReadOnlyList<(long Time, long Position)> cues) {
    using var body = new MemoryStream();
    foreach (var cue in cues) {
      using var positions = new MemoryStream();
      WriteUnsignedElement(positions, 0xF7, 1); // CueTrack
      WriteUnsignedElement(positions, 0xF1, checked((ulong)cue.Position)); // CueClusterPosition

      using var point = new MemoryStream();
      WriteUnsignedElement(point, 0xB3, checked((ulong)cue.Time)); // CueTime
      WriteElement(point, 0xB7, positions.ToArray());
      WriteElement(body, 0xBB, point.ToArray());
    }
    return ElementBytes(0x1C53BB6B, body.ToArray());
  }

  private static void WriteSimpleBlock(Stream output, short relativeTimestamp, ReadOnlySpan<byte> packet) {
    using var body = new MemoryStream(capacity: checked(packet.Length + 4));
    body.WriteByte(0x81); // track 1 as EBML vint
    Span<byte> time = stackalloc byte[2];
    BinaryPrimitives.WriteInt16BigEndian(time, relativeTimestamp);
    body.Write(time);
    body.WriteByte(0x80); // keyframe, no lacing, not discardable
    body.Write(packet);
    WriteElement(output, 0xA3, body.ToArray());
  }

  private static void ValidateCodecPrivate(string codec, AudioEncodedStream stream) {
    var privateData = stream.CodecPrivateData;
    switch (codec) {
      case "aac" when privateData is not { Length: >= 2 }:
        throw new InvalidDataException("A_AAC requires AudioSpecificConfig in CodecPrivateData.");
      case "opus":
        if (privateData is not { Length: >= 19 } || !privateData.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
          throw new InvalidDataException("A_OPUS requires an RFC 7845 OpusHead in CodecPrivateData.");
        if (privateData[9] != stream.Format.Channels)
          throw new InvalidDataException("OpusHead channel count must match AudioStreamFormat.Channels.");
        break;
      case "vorbis" when MkvAudioChannels.SplitXiphLacedHeaders(privateData).Count != 3:
        throw new InvalidDataException("A_VORBIS requires the three Xiph-laced setup headers in CodecPrivateData.");
      case "flac" when privateData is not { Length: >= 4 } || !privateData.AsSpan(0, 4).SequenceEqual("fLaC"u8):
        throw new InvalidDataException("A_FLAC requires the native fLaC signature and metadata in CodecPrivateData.");
    }
  }

  private static string MatroskaCodecId(string codec) => codec switch {
    "aac" => "A_AAC",
    "mp2" => "A_MPEG/L2",
    "mp3" => "A_MPEG/L3",
    "ac3" => "A_AC3",
    "eac3" => "A_EAC3",
    "flac" => "A_FLAC",
    "opus" => "A_OPUS",
    "vorbis" => "A_VORBIS",
    _ => throw new NotSupportedException($"No Matroska CodecID mapping for '{codec}'."),
  };

  internal static string? CanonicalCodecId(string matroskaCodecId) => matroskaCodecId switch {
    "A_AAC" => "aac",
    "A_MPEG/L2" => "mp2",
    "A_MPEG/L3" => "mp3",
    "A_AC3" => "ac3",
    "A_EAC3" => "eac3",
    "A_FLAC" => "flac",
    "A_OPUS" => "opus",
    "A_VORBIS" => "vorbis",
    _ => null,
  };

  private static string NormalizeCodec(string codec) => codec.ToLowerInvariant() switch {
    "aac-lc" => "aac",
    "e-ac-3" => "eac3",
    var value => value,
  };

  private static string? Property(AudioStreamFormat format, string key)
    => format.Properties is { } properties && properties.TryGetValue(key, out var value) ? value : null;

  private static long SamplesToMilliseconds(long samples, int sampleRate) {
    var whole = samples / sampleRate;
    var remainder = samples % sampleRate;
    return checked(whole * 1000 + remainder * 1000 / sampleRate);
  }

  private static int MpegAudioFrameSamples(ReadOnlySpan<byte> packet) {
    if (packet.Length < 4 || packet[0] != 0xFF || (packet[1] & 0xE0) != 0xE0)
      return 0;
    var version = (packet[1] >> 3) & 0x03;
    var layer = (packet[1] >> 1) & 0x03;
    return layer switch {
      0x03 => 384,
      0x02 => 1152,
      0x01 => version == 0x03 ? 1152 : 576,
      _ => 0,
    };
  }

  private static int Eac3FrameSamples(ReadOnlySpan<byte> packet) {
    if (packet.Length < 5 || packet[0] != 0x0B || packet[1] != 0x77)
      return 0;
    var fscod = packet[4] >> 6;
    if (fscod == 3)
      return 6 * 256;
    var numBlocks = (packet[4] >> 4) & 0x03;
    return (numBlocks switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 }) * 256;
  }

  private static int OpusPacketSamples(ReadOnlySpan<byte> packet) {
    if (packet.IsEmpty)
      return 0;
    var toc = packet[0];
    var config = toc >> 3;
    var samplesPerFrame = config switch {
      < 12 => (config & 3) switch { 0 => 480, 1 => 960, 2 => 1920, _ => 2880 },
      < 16 => (config & 1) == 0 ? 480 : 960,
      _ => (config & 3) switch { 0 => 120, 1 => 240, 2 => 480, _ => 960 },
    };
    var countCode = toc & 0x03;
    var frameCount = countCode switch {
      0 => 1,
      1 or 2 => 2,
      3 when packet.Length >= 2 => packet[1] & 0x3F,
      _ => 0,
    };
    var total = samplesPerFrame * frameCount;
    return frameCount is >= 1 and <= 48 && total <= 5760 ? total : 0;
  }

  private static byte[] ElementBytes(ulong id, ReadOnlySpan<byte> body) {
    using var result = new MemoryStream();
    WriteElement(result, id, body);
    return result.ToArray();
  }

  private static void WriteUnsignedElement(Stream output, ulong id, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
    var first = 0;
    while (first < 7 && bytes[first] == 0)
      ++first;
    WriteElement(output, id, bytes[first..]);
  }

  private static void WriteFloatElement(Stream output, ulong id, double value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
    WriteElement(output, id, bytes);
  }

  private static void WriteStringElement(Stream output, ulong id, string value)
    => WriteElement(output, id, Encoding.UTF8.GetBytes(value));

  private static void WriteElement(Stream output, ulong id, ReadOnlySpan<byte> body) {
    WriteId(output, id);
    WriteSize(output, checked((ulong)body.Length));
    output.Write(body);
  }

  private static void WriteId(Stream output, ulong id) {
    var length = id switch {
      <= 0xFF => 1,
      <= 0xFFFF => 2,
      <= 0xFFFFFF => 3,
      <= 0xFFFFFFFF => 4,
      _ => throw new ArgumentOutOfRangeException(nameof(id), "Matroska element IDs are at most four octets."),
    };
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)id));
    output.Write(bytes[(4 - length)..]);
  }

  private static void WriteSize(Stream output, ulong value) {
    for (var length = 1; length <= 8; ++length) {
      var valueBits = 7 * length;
      var reservedUnknown = (1UL << valueBits) - 1;
      if (value >= reservedUnknown)
        continue;

      var encoded = value | (1UL << valueBits);
      Span<byte> bytes = stackalloc byte[8];
      BinaryPrimitives.WriteUInt64BigEndian(bytes, encoded);
      output.Write(bytes[(8 - length)..]);
      return;
    }
    throw new InvalidDataException("EBML element body exceeds the 56-bit size field.");
  }
}
