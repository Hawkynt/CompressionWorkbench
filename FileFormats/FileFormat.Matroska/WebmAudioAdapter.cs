#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Opus;
using Compression.Registry;

namespace FileFormat.Matroska;

/// <summary>
/// Packet-preserving WebM audio mux/remux support. WebM is a constrained Matroska
/// profile, so this deliberately lives beside the existing EBML reader rather than
/// introducing a second container implementation.
/// </summary>
internal static class WebmAudioAdapter {
  internal static IReadOnlyList<string> SupportedCodecs { get; } = ["opus", "vorbis"];

  private const long TimestampScaleNanoseconds = 1_000_000;
  private const long OpusSeekPreRollNanoseconds = 80_000_000;
  private const long NanosecondsPerSecond = 1_000_000_000;
  private const int OpusClockRate = 48_000;
  private const long MaxClusterDurationTicks = 5_000;
  private const int MaxClusterBytes = 5 * 1024 * 1024;

  private const ulong IdEbml = 0x1A45DFA3;
  private const ulong IdEbmlVersion = 0x4286;
  private const ulong IdEbmlReadVersion = 0x42F7;
  private const ulong IdEbmlMaxIdLength = 0x42F2;
  private const ulong IdEbmlMaxSizeLength = 0x42F3;
  private const ulong IdDocType = 0x4282;
  private const ulong IdDocTypeVersion = 0x4287;
  private const ulong IdDocTypeReadVersion = 0x4285;
  private const ulong IdSegment = 0x18538067;
  private const ulong IdSeekHead = 0x114D9B74;
  private const ulong IdSeek = 0x4DBB;
  private const ulong IdSeekId = 0x53AB;
  private const ulong IdSeekPosition = 0x53AC;
  private const ulong IdInfo = 0x1549A966;
  private const ulong IdTimestampScale = 0x2AD7B1;
  private const ulong IdDuration = 0x4489;
  private const ulong IdMuxingApp = 0x4D80;
  private const ulong IdWritingApp = 0x5741;
  private const ulong IdTracks = 0x1654AE6B;
  private const ulong IdTrackEntry = 0xAE;
  private const ulong IdTrackNumber = 0xD7;
  private const ulong IdTrackUid = 0x73C5;
  private const ulong IdTrackType = 0x83;
  private const ulong IdFlagLacing = 0x9C;
  private const ulong IdCodecId = 0x86;
  private const ulong IdCodecPrivate = 0x63A2;
  private const ulong IdCodecDelay = 0x56AA;
  private const ulong IdSeekPreRoll = 0x56BB;
  private const ulong IdLanguage = 0x22B59C;
  private const ulong IdAudio = 0xE1;
  private const ulong IdSamplingFrequency = 0xB5;
  private const ulong IdChannels = 0x9F;
  private const ulong IdBitDepth = 0x6264;
  private const ulong IdCluster = 0x1F43B675;
  private const ulong IdClusterTimestamp = 0xE7;
  private const ulong IdSimpleBlock = 0xA3;
  private const ulong IdBlockGroup = 0xA0;
  private const ulong IdBlock = 0xA1;

  private sealed record AudioTrack(
    int Number,
    string CodecId,
    byte[] CodecPrivate,
    int SampleRate,
    int Channels,
    int BitDepth
  );

  private sealed record TimedPacket(byte[] Data, long TimestampNanoseconds, long DurationNanoseconds);

  private sealed record ParsedBlock(int TrackNumber, short RelativeTimestamp, IReadOnlyList<byte[]> Frames);

  internal static bool CanMux(AudioStreamFormat format, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);

    if (NormalizeCodec(format.CodecId) is null) {
      reason = $"WebM audio mux supports Opus and Vorbis, not '{format.CodecId}'.";
      return false;
    }

    if (format.SampleRate <= 0) {
      reason = "WebM audio mux requires a positive sample rate.";
      return false;
    }

    if (format.Channels is < 1 or > 8) {
      reason = "WebM audio mux supports 1-8 channels.";
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

    var codecId = NormalizeCodec(stream.Format.CodecId)!;
    var codecPrivate = codecId == "A_OPUS"
      ? PrepareOpusPrivate(stream)
      : PrepareVorbisPrivate(stream);

    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (packets.Length == 0)
      throw new InvalidDataException("WebM mux requires at least one media packet.");

    var timedPackets = new TimedPacket[packets.Length];
    var timestamp = 0L;
    for (var i = 0; i < packets.Length; ++i) {
      if (packets[i].Data.Length == 0)
        throw new InvalidDataException($"WebM packet {i} is empty.");

      var duration = GetPacketDurationNanoseconds(packets[i], codecId, stream.Format.SampleRate);
      if (duration <= 0)
        throw new InvalidDataException($"WebM packet {i} has no usable duration.");
      timedPackets[i] = new TimedPacket(packets[i].Data, timestamp, duration);
      timestamp = checked(timestamp + duration);
    }

    var ebml = BuildEbmlHeader();
    var info = BuildInfo(timestamp);
    var tracks = BuildTracks(stream.Format, codecId, codecPrivate);
    var clusters = BuildClusters(timedPackets);

    // SeekPosition is fixed to eight payload bytes, making SeekHead's encoded length
    // independent of the actual offsets. One dry build therefore fixes both offsets.
    var placeholderSeekHead = BuildSeekHead(0, 0);
    var seekHead = BuildSeekHead(
      (ulong)placeholderSeekHead.Length,
      checked((ulong)(placeholderSeekHead.Length + info.Length)));

    using var segmentBody = new MemoryStream();
    segmentBody.Write(seekHead);
    segmentBody.Write(info);
    segmentBody.Write(tracks);
    segmentBody.Write(clusters);

    output.Write(ebml);
    output.Write(Element(IdSegment, segmentBody.ToArray()));
  }

  internal static bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;

    try {
      using var buffer = new MemoryStream();
      input.CopyTo(buffer);
      var file = buffer.ToArray();
      if (file.Length == 0) return false;

      var ebml = new EbmlReader(file);
      var pos = 0L;
      EbmlReader.Element? segment = null;
      while (pos < file.Length) {
        var element = ebml.Read(ref pos);
        if (element is null) break;
        if (element.Value.Id != IdSegment) continue;
        segment = element;
        break;
      }
      if (segment is null) return false;

      var children = ebml.Children(segment.Value).ToArray();
      var timestampScale = TimestampScaleNanoseconds;
      foreach (var child in children) {
        if (child.Id != IdInfo) continue;
        foreach (var field in ebml.Children(child))
          if (field.Id == IdTimestampScale) {
            var value = ebml.ReadUnsigned(field);
            if (value is > 0 and <= long.MaxValue)
              timestampScale = (long)value;
          }
      }

      var tracks = new List<AudioTrack>();
      foreach (var child in children)
        if (child.Id == IdTracks)
          ParseTracks(ebml, child, tracks);

      var supported = tracks.Where(static track => NormalizeCodec(track.CodecId) is not null).ToArray();
      if (supported.Length != 1) return false;
      var track = supported[0];
      var codec = NormalizeCodec(track.CodecId)!;
      var sampleRate = ResolveSampleRate(track, codec);
      var channels = ResolveChannels(track, codec);
      if (sampleRate <= 0 || channels is < 1 or > 8) return false;

      var frames = new List<(byte[] Data, long TimestampNanoseconds, int LaceCount)>();
      foreach (var child in children) {
        if (child.Id != IdCluster) continue;
        ParseCluster(ebml, child, track.Number, timestampScale, frames);
      }
      if (frames.Count == 0) return false;

      // Opus packets carry their own duration. Vorbis does not: without a codec
      // setup parser the only exact timing source is the next block timestamp, so
      // laced Vorbis blocks are rejected instead of manufacturing bogus timing.
      if (codec == "A_VORBIS" && frames.Any(static frame => frame.LaceCount != 1))
        return false;

      var packets = new AudioPacket[frames.Count];
      long granule = 0;
      long previousVorbisDuration = 0;
      for (var i = 0; i < frames.Count; ++i) {
        long durationSamples;
        if (codec == "A_OPUS") {
          durationSamples = GetOpusDurationSamples(frames[i].Data, sampleRate);
          if (durationSamples <= 0) return false;
        } else {
          if (i + 1 < frames.Count) {
            var delta = frames[i + 1].TimestampNanoseconds - frames[i].TimestampNanoseconds;
            if (delta <= 0) return false;
            durationSamples = NanosecondsToSamples(delta, sampleRate);
            if (durationSamples <= 0) return false;
            previousVorbisDuration = durationSamples;
          } else {
            durationSamples = previousVorbisDuration;
            if (durationSamples <= 0) return false;
          }
        }

        granule = checked(granule + durationSamples);
        packets[i] = new AudioPacket(frames[i].Data, durationSamples, granule);
      }

      stream = new AudioEncodedStream(
        new AudioStreamFormat(
          codec == "A_OPUS" ? "opus" : "vorbis",
          sampleRate,
          channels,
          track.BitDepth),
        packets,
        track.CodecPrivate);
      return true;
    } catch (InvalidDataException) {
      return false;
    } catch (OverflowException) {
      return false;
    }
  }

  private static byte[] PrepareOpusPrivate(AudioEncodedStream stream) {
    var source = stream.CodecPrivateData;
    if (source is null || !source.AsSpan().StartsWith("OpusHead"u8))
      source = stream.Packets.FirstOrDefault(static packet => packet.IsHeader && packet.Data.AsSpan().StartsWith("OpusHead"u8))?.Data;

    byte[] result;
    if (source is null) {
      if (stream.Format.Channels is not (1 or 2))
        throw new InvalidDataException("Opus WebM without OpusHead can only synthesize mapping-family-0 mono/stereo headers.");
      result = new byte[19];
      "OpusHead"u8.CopyTo(result);
      result[8] = 1;
      result[9] = (byte)stream.Format.Channels;
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)stream.Format.SampleRate);
      result[18] = 0;
    } else {
      if (source.Length < 19)
        throw new InvalidDataException("OpusHead is truncated.");
      result = source.ToArray();
    }

    if (result[9] != stream.Format.Channels)
      throw new InvalidDataException($"OpusHead channels ({result[9]}) do not match stream channels ({stream.Format.Channels}).");
    if (result[18] == 0 && result[9] > 2)
      throw new InvalidDataException("Opus mapping family 0 is only valid for mono/stereo.");
    if (result[18] != 0 && result.Length < 21 + result[9])
      throw new InvalidDataException("OpusHead channel mapping is truncated.");

    var inputRate = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(12, 4));
    if (inputRate == 0)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)stream.Format.SampleRate);
    else if (inputRate != stream.Format.SampleRate)
      throw new InvalidDataException($"OpusHead input rate ({inputRate}) does not match stream sample rate ({stream.Format.SampleRate}).");

    return result;
  }

  private static byte[] PrepareVorbisPrivate(AudioEncodedStream stream) {
    byte[] result;
    if (stream.CodecPrivateData is { Length: > 0 } supplied) {
      result = supplied.ToArray();
    } else {
      var headers = stream.Packets.Where(static packet => packet.IsHeader).Take(3).Select(static packet => packet.Data).ToArray();
      if (headers.Length != 3)
        throw new InvalidDataException("Vorbis WebM requires Matroska CodecPrivate or three header packets.");
      result = BuildVorbisPrivate(headers);
    }

    if (!TrySplitVorbisPrivate(result, out var parsedHeaders))
      throw new InvalidDataException("Vorbis CodecPrivate is not valid Matroska Xiph-laced header data.");
    var identification = parsedHeaders[0];
    if (identification.Length < 30 || !identification.AsSpan(0, 7).SequenceEqual(new byte[] { 0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' }))
      throw new InvalidDataException("Vorbis identification header is invalid.");

    var channels = identification[11];
    var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(identification.AsSpan(12, 4));
    if (channels != stream.Format.Channels)
      throw new InvalidDataException($"Vorbis header channels ({channels}) do not match stream channels ({stream.Format.Channels}).");
    if (sampleRate != stream.Format.SampleRate)
      throw new InvalidDataException($"Vorbis header sample rate ({sampleRate}) does not match stream sample rate ({stream.Format.SampleRate}).");
    return result;
  }

  private static long GetPacketDurationNanoseconds(AudioPacket packet, string codecId, int sampleRate) {
    if (packet.DurationSamples > 0)
      return SamplesToNanoseconds(packet.DurationSamples, sampleRate);
    if (codecId == "A_OPUS") {
      var samples = GetOpusDurationSamples(packet.Data, sampleRate);
      return samples <= 0 ? 0 : SamplesToNanoseconds(samples, sampleRate);
    }
    return 0;
  }

  private static long GetOpusDurationSamples(ReadOnlySpan<byte> packet, int sampleRate) {
    if (packet.Length == 0) return 0;
    var frameCount = OpusPacketReader.CountFrames(packet);
    if (frameCount <= 0) return 0;
    var samplesAt48k = checked((long)OpusPacketReader.ParseToc(packet[0]).FrameSamplesAt48k * frameCount);
    return checked(samplesAt48k * sampleRate / OpusClockRate);
  }

  private static long SamplesToNanoseconds(long samples, int sampleRate) {
    var seconds = samples / sampleRate;
    var remainder = samples % sampleRate;
    return checked(seconds * NanosecondsPerSecond + remainder * NanosecondsPerSecond / sampleRate);
  }

  private static long NanosecondsToSamples(long nanoseconds, int sampleRate) {
    var seconds = nanoseconds / NanosecondsPerSecond;
    var remainder = nanoseconds % NanosecondsPerSecond;
    return checked(seconds * sampleRate + (remainder * sampleRate + NanosecondsPerSecond / 2) / NanosecondsPerSecond);
  }

  private static byte[] BuildEbmlHeader() => Master(IdEbml,
    UInt(IdEbmlVersion, 1),
    UInt(IdEbmlReadVersion, 1),
    UInt(IdEbmlMaxIdLength, 4),
    UInt(IdEbmlMaxSizeLength, 8),
    Utf8(IdDocType, "webm"),
    UInt(IdDocTypeVersion, 4),
    UInt(IdDocTypeReadVersion, 2));

  private static byte[] BuildInfo(long durationNanoseconds) => Master(IdInfo,
    UInt(IdTimestampScale, TimestampScaleNanoseconds),
    Float(IdDuration, durationNanoseconds / (double)TimestampScaleNanoseconds),
    Utf8(IdMuxingApp, "CompressionWorkbench"),
    Utf8(IdWritingApp, "CompressionWorkbench"));

  private static byte[] BuildTracks(AudioStreamFormat format, string codecId, byte[] codecPrivate) {
    var fields = new List<byte[]> {
      UInt(IdTrackNumber, 1),
      UInt(IdTrackUid, 1),
      UInt(IdTrackType, 2),
      UInt(IdFlagLacing, 0),
      Utf8(IdCodecId, codecId),
      Binary(IdCodecPrivate, codecPrivate),
      Utf8(IdLanguage, "und"),
    };

    if (codecId == "A_OPUS") {
      var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(codecPrivate.AsSpan(10, 2));
      fields.Add(UInt(IdCodecDelay, (ulong)(preSkip * (long)NanosecondsPerSecond / OpusClockRate)));
      fields.Add(UInt(IdSeekPreRoll, OpusSeekPreRollNanoseconds));
    }

    var audioFields = new List<byte[]> {
      Float(IdSamplingFrequency, format.SampleRate),
      UInt(IdChannels, format.Channels),
    };
    if (format.BitsPerSample > 0)
      audioFields.Add(UInt(IdBitDepth, format.BitsPerSample));
    fields.Add(Master(IdAudio, audioFields));

    return Master(IdTracks, Master(IdTrackEntry, fields));
  }

  private static byte[] BuildSeekHead(ulong infoPosition, ulong tracksPosition) => Master(IdSeekHead,
    BuildSeek(IdInfo, infoPosition),
    BuildSeek(IdTracks, tracksPosition));

  private static byte[] BuildSeek(ulong targetId, ulong position) => Master(IdSeek,
    Binary(IdSeekId, IdBytes(targetId)),
    UInt(IdSeekPosition, position, width: 8));

  private static byte[] BuildClusters(IReadOnlyList<TimedPacket> packets) {
    using var output = new MemoryStream();
    var packetIndex = 0;
    while (packetIndex < packets.Count) {
      var clusterStartTick = packets[packetIndex].TimestampNanoseconds / TimestampScaleNanoseconds;
      using var body = new MemoryStream();
      body.Write(UInt(IdClusterTimestamp, clusterStartTick));
      var framesInCluster = 0;

      while (packetIndex < packets.Count) {
        var packet = packets[packetIndex];
        var tick = packet.TimestampNanoseconds / TimestampScaleNanoseconds;
        var relative = tick - clusterStartTick;
        if (framesInCluster > 0 && (relative > MaxClusterDurationTicks || relative > short.MaxValue))
          break;

        var block = BuildSimpleBlock(packet.Data, checked((short)relative));
        if (framesInCluster > 0 && body.Length + block.Length > MaxClusterBytes)
          break;

        body.Write(block);
        ++framesInCluster;
        ++packetIndex;
      }

      output.Write(Element(IdCluster, body.ToArray()));
    }
    return output.ToArray();
  }

  private static byte[] BuildSimpleBlock(byte[] payload, short relativeTimestamp) {
    var body = new byte[4 + payload.Length];
    body[0] = 0x81; // TrackNumber 1 as EBML vint.
    BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(1, 2), relativeTimestamp);
    body[3] = 0x80; // audio packet: keyframe bit, no lacing/discardable flags.
    payload.CopyTo(body, 4);
    return Element(IdSimpleBlock, body);
  }

  private static void ParseTracks(EbmlReader ebml, EbmlReader.Element tracks, List<AudioTrack> result) {
    foreach (var entry in ebml.Children(tracks)) {
      if (entry.Id != IdTrackEntry) continue;
      var number = 0;
      var type = 0UL;
      var codec = string.Empty;
      byte[] codecPrivate = [];
      var sampleRate = 0;
      var channels = 0;
      var bitDepth = 0;

      foreach (var field in ebml.Children(entry)) {
        switch (field.Id) {
          case IdTrackNumber: number = checked((int)ebml.ReadUnsigned(field)); break;
          case IdTrackType: type = ebml.ReadUnsigned(field); break;
          case IdCodecId: codec = ebml.ReadString(field); break;
          case IdCodecPrivate: codecPrivate = ebml.ReadBinary(field); break;
          case IdAudio: ParseAudio(ebml, field, ref sampleRate, ref channels, ref bitDepth); break;
        }
      }

      if (type == 2 && number > 0)
        result.Add(new AudioTrack(number, codec, codecPrivate, sampleRate, channels, bitDepth));
    }
  }

  private static void ParseAudio(EbmlReader ebml, EbmlReader.Element audio, ref int sampleRate, ref int channels, ref int bitDepth) {
    foreach (var field in ebml.Children(audio)) {
      switch (field.Id) {
        case IdSamplingFrequency: sampleRate = checked((int)Math.Round(ReadFloat(ebml.Body(field)))); break;
        case IdChannels: channels = checked((int)ebml.ReadUnsigned(field)); break;
        case IdBitDepth: bitDepth = checked((int)ebml.ReadUnsigned(field)); break;
      }
    }
  }

  private static void ParseCluster(
    EbmlReader ebml,
    EbmlReader.Element cluster,
    int targetTrack,
    long timestampScale,
    List<(byte[] Data, long TimestampNanoseconds, int LaceCount)> result) {
    var children = ebml.Children(cluster).ToArray();
    var clusterTimestamp = 0L;
    foreach (var child in children)
      if (child.Id == IdClusterTimestamp) {
        var value = ebml.ReadUnsigned(child);
        if (value > long.MaxValue) throw new InvalidDataException("WebM cluster timestamp is too large.");
        clusterTimestamp = (long)value;
      }

    foreach (var child in children) {
      if (child.Id == IdSimpleBlock) {
        AppendBlock(ebml.Body(child), clusterTimestamp, timestampScale, targetTrack, result);
        continue;
      }

      if (child.Id != IdBlockGroup) continue;
      foreach (var field in ebml.Children(child))
        if (field.Id == IdBlock)
          AppendBlock(ebml.Body(field), clusterTimestamp, timestampScale, targetTrack, result);
    }
  }

  private static void AppendBlock(
    ReadOnlySpan<byte> body,
    long clusterTimestamp,
    long timestampScale,
    int targetTrack,
    List<(byte[] Data, long TimestampNanoseconds, int LaceCount)> result) {
    if (!TryParseBlock(body, out var block) || block.TrackNumber != targetTrack) return;
    var absoluteTicks = checked(clusterTimestamp + block.RelativeTimestamp);
    if (absoluteTicks < 0) throw new InvalidDataException("WebM block timestamp precedes time zero.");
    var timestampNanoseconds = checked(absoluteTicks * timestampScale);
    foreach (var frame in block.Frames)
      result.Add((frame, timestampNanoseconds, block.Frames.Count));
  }

  private static bool TryParseBlock(ReadOnlySpan<byte> body, out ParsedBlock block) {
    block = null!;
    if (body.Length < 4) return false;

    var pos = 0;
    if (!TryReadVint(body, ref pos, out var trackNumber, out _)) return false;
    if (trackNumber is 0 or > int.MaxValue || pos + 3 > body.Length) return false;

    var relativeTimestamp = BinaryPrimitives.ReadInt16BigEndian(body.Slice(pos, 2));
    pos += 2;
    var flags = body[pos++];
    var lacing = (flags >> 1) & 0x03;
    var payload = body[pos..];

    IReadOnlyList<byte[]> frames;
    if (lacing == 0)
      frames = [payload.ToArray()];
    else if (!TrySplitLaced(payload, lacing, out var split))
      return false;
    else
      frames = split;

    block = new ParsedBlock((int)trackNumber, relativeTimestamp, frames);
    return true;
  }

  private static bool TrySplitLaced(ReadOnlySpan<byte> payload, int lacing, out List<byte[]> frames) {
    frames = [];
    if (payload.Length < 1) return false;
    var count = payload[0] + 1;
    var pos = 1;
    var sizes = new int[count];

    switch (lacing) {
      case 1: // Xiph lacing.
        for (var i = 0; i < count - 1; ++i) {
          var size = 0;
          while (true) {
            if (pos >= payload.Length) return false;
            var value = payload[pos++];
            size = checked(size + value);
            if (value != 255) break;
          }
          sizes[i] = size;
        }
        break;

      case 2: { // fixed lacing.
        var remaining = payload.Length - pos;
        if (remaining % count != 0) return false;
        Array.Fill(sizes, remaining / count);
        break;
      }

      case 3: { // EBML lacing.
        if (!TryReadVint(payload, ref pos, out var firstSize, out _ ) || firstSize > int.MaxValue) return false;
        sizes[0] = (int)firstSize;
        var previous = (long)firstSize;
        for (var i = 1; i < count - 1; ++i) {
          if (!TryReadSignedVint(payload, ref pos, out var delta)) return false;
          previous = checked(previous + delta);
          if (previous is < 0 or > int.MaxValue) return false;
          sizes[i] = (int)previous;
        }
        break;
      }

      default:
        return false;
    }

    if (lacing != 2) {
      var used = 0L;
      for (var i = 0; i < count - 1; ++i)
        used += sizes[i];
      var finalSize = payload.Length - pos - used;
      if (finalSize is < 0 or > int.MaxValue) return false;
      sizes[count - 1] = (int)finalSize;
    }

    for (var i = 0; i < count; ++i) {
      if (sizes[i] < 0 || pos + sizes[i] > payload.Length) return false;
      frames.Add(payload.Slice(pos, sizes[i]).ToArray());
      pos += sizes[i];
    }
    return pos == payload.Length;
  }

  private static bool TryReadVint(ReadOnlySpan<byte> data, ref int pos, out ulong value, out int length) {
    value = 0;
    length = 0;
    if (pos >= data.Length) return false;
    var first = data[pos];
    for (var i = 0; i < 8; ++i)
      if ((first & (0x80 >> i)) != 0) {
        length = i + 1;
        break;
      }
    if (length == 0 || pos + length > data.Length) return false;

    value = (ulong)(first & (0xFF >> length));
    for (var i = 1; i < length; ++i)
      value = (value << 8) | data[pos + i];
    pos += length;
    return true;
  }

  private static bool TryReadSignedVint(ReadOnlySpan<byte> data, ref int pos, out long value) {
    value = 0;
    if (!TryReadVint(data, ref pos, out var raw, out var length)) return false;
    var bias = (1L << (7 * length - 1)) - 1;
    value = checked((long)raw - bias);
    return true;
  }

  private static int ResolveSampleRate(AudioTrack track, string codec) {
    if (track.SampleRate > 0) return track.SampleRate;
    if (codec == "A_OPUS" && track.CodecPrivate.AsSpan().StartsWith("OpusHead"u8) && track.CodecPrivate.Length >= 16) {
      var rate = BinaryPrimitives.ReadUInt32LittleEndian(track.CodecPrivate.AsSpan(12, 4));
      return rate is > 0 and <= int.MaxValue ? (int)rate : OpusClockRate;
    }
    if (codec == "A_VORBIS" && TrySplitVorbisPrivate(track.CodecPrivate, out var headers) && headers[0].Length >= 16)
      return BinaryPrimitives.ReadInt32LittleEndian(headers[0].AsSpan(12, 4));
    return 0;
  }

  private static int ResolveChannels(AudioTrack track, string codec) {
    if (track.Channels > 0) return track.Channels;
    if (codec == "A_OPUS" && track.CodecPrivate.AsSpan().StartsWith("OpusHead"u8) && track.CodecPrivate.Length >= 10)
      return track.CodecPrivate[9];
    if (codec == "A_VORBIS" && TrySplitVorbisPrivate(track.CodecPrivate, out var headers) && headers[0].Length >= 12)
      return headers[0][11];
    return 0;
  }

  private static byte[] BuildVorbisPrivate(IReadOnlyList<byte[]> headers) {
    using var result = new MemoryStream();
    result.WriteByte(2);
    WriteXiphLength(result, headers[0].Length);
    WriteXiphLength(result, headers[1].Length);
    foreach (var header in headers)
      result.Write(header);
    return result.ToArray();
  }

  private static bool TrySplitVorbisPrivate(byte[] data, out byte[][] headers) {
    headers = [];
    if (data.Length < 4 || data[0] != 2) return false;
    var pos = 1;
    if (!TryReadXiphLength(data, ref pos, out var first) || !TryReadXiphLength(data, ref pos, out var second)) return false;
    if (first < 0 || second < 0 || pos + first + second > data.Length) return false;
    var third = data.Length - pos - first - second;
    if (third <= 0) return false;
    headers = [
      data.AsSpan(pos, first).ToArray(),
      data.AsSpan(pos + first, second).ToArray(),
      data.AsSpan(pos + first + second, third).ToArray(),
    ];
    return true;
  }

  private static void WriteXiphLength(Stream output, int length) {
    while (length >= 255) {
      output.WriteByte(255);
      length -= 255;
    }
    output.WriteByte((byte)length);
  }

  private static bool TryReadXiphLength(ReadOnlySpan<byte> data, ref int pos, out int length) {
    length = 0;
    while (true) {
      if (pos >= data.Length) return false;
      var value = data[pos++];
      length = checked(length + value);
      if (value != 255) return true;
    }
  }

  private static double ReadFloat(ReadOnlySpan<byte> body) => body.Length switch {
    4 => BinaryPrimitives.ReadSingleBigEndian(body),
    8 => BinaryPrimitives.ReadDoubleBigEndian(body),
    _ => 0,
  };

  private static string? NormalizeCodec(string codecId) => codecId.ToUpperInvariant() switch {
    "OPUS" or "A_OPUS" => "A_OPUS",
    "VORBIS" or "A_VORBIS" => "A_VORBIS",
    _ => null,
  };

  private static byte[] Master(ulong id, params byte[][] children) => Master(id, (IEnumerable<byte[]>)children);

  private static byte[] Master(ulong id, IEnumerable<byte[]> children) {
    using var body = new MemoryStream();
    foreach (var child in children)
      body.Write(child);
    return Element(id, body.ToArray());
  }

  private static byte[] UInt(ulong id, long value, int width = 0) {
    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    return UInt(id, (ulong)value, width);
  }

  private static byte[] UInt(ulong id, int value, int width = 0) {
    if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    return UInt(id, (ulong)value, width);
  }

  private static byte[] UInt(ulong id, ulong value, int width = 0) {
    if (width == 0) {
      width = 1;
      while (width < 8 && value >= (1UL << (width * 8))) ++width;
    }
    if (width is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(width));
    var body = new byte[width];
    for (var i = width - 1; i >= 0; --i) {
      body[i] = (byte)value;
      value >>= 8;
    }
    if (value != 0) throw new ArgumentOutOfRangeException(nameof(value));
    return Element(id, body);
  }

  private static byte[] Float(ulong id, double value) {
    var body = new byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(body, value);
    return Element(id, body);
  }

  private static byte[] Utf8(ulong id, string value) => Element(id, Encoding.UTF8.GetBytes(value));

  private static byte[] Binary(ulong id, byte[] value) => Element(id, value);

  private static byte[] Element(ulong id, byte[] body) {
    using var result = new MemoryStream();
    result.Write(IdBytes(id));
    WriteSize(result, (ulong)body.Length);
    result.Write(body);
    return result.ToArray();
  }

  private static byte[] IdBytes(ulong id) {
    var width = id switch {
      <= 0xFF => 1,
      <= 0xFFFF => 2,
      <= 0xFFFFFF => 3,
      <= 0xFFFFFFFF => 4,
      _ => throw new ArgumentOutOfRangeException(nameof(id), "EBML element IDs are at most four bytes."),
    };
    var result = new byte[width];
    for (var i = width - 1; i >= 0; --i) {
      result[i] = (byte)id;
      id >>= 8;
    }
    return result;
  }

  private static void WriteSize(Stream output, ulong size) {
    var width = 1;
    for (; width <= 8; ++width) {
      var maxKnownSize = (1UL << (7 * width)) - 2;
      if (size <= maxKnownSize) break;
    }
    if (width > 8) throw new ArgumentOutOfRangeException(nameof(size));

    Span<byte> encoded = stackalloc byte[8];
    var value = size;
    for (var i = width - 1; i >= 0; --i) {
      encoded[i] = (byte)value;
      value >>= 8;
    }
    encoded[0] |= (byte)(1 << (8 - width));
    output.Write(encoded[..width]);
  }
}
