#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Flv;

/// <summary>
/// One complete FLV tag payload together with the fields required by its 11-byte tag header.
/// The upper three bits of <see cref="HeaderByte"/> are retained so filtered/encrypted tags can
/// be remuxed without interpreting their payload.
/// </summary>
public sealed record FlvRawTag(byte HeaderByte, uint TimestampMs, uint StreamId, byte[] Body) {
  public byte TagType => (byte)(this.HeaderByte & 0x1F);
}

/// <summary>
/// FLV container writer/remuxer. The low-level path preserves complete FLV tag payloads and timing;
/// the audio path maps the repository's container-neutral AAC/MP3 packet surface into FLV tags.
/// </summary>
public static class FlvMuxer {

  public static readonly IReadOnlyList<string> SupportedAudioCodecs = ["aac", "aac-lc", "mp3"];

  private static readonly int[] AacSampleRates = [
    96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
    16000, 12000, 11025, 8000, 7350,
  ];

  /// <summary>Writes a complete FLV file from already packetized FLV tag bodies.</summary>
  public static void Mux(
      Stream output,
      IEnumerable<FlvRawTag> tags,
      byte version = 1,
      bool? hasAudio = null,
      bool? hasVideo = null) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(tags);

    var materialized = tags as IReadOnlyList<FlvRawTag> ?? tags.ToArray();
    var audio = hasAudio ?? materialized.Any(static tag => tag.TagType == FlvReader.TagAudio);
    var video = hasVideo ?? materialized.Any(static tag => tag.TagType == FlvReader.TagVideo);

    Span<byte> header = stackalloc byte[FlvReader.HeaderSize + 4];
    header[0] = (byte)'F';
    header[1] = (byte)'L';
    header[2] = (byte)'V';
    header[3] = version;
    header[4] = (byte)((audio ? 0x04 : 0) | (video ? 0x01 : 0));
    BinaryPrimitives.WriteUInt32BigEndian(header[5..9], FlvReader.HeaderSize);
    BinaryPrimitives.WriteUInt32BigEndian(header[9..13], 0);
    output.Write(header);

    foreach (var tag in materialized)
      WriteTag(output, tag);
  }

  /// <summary>
  /// Rewrites an FLV container from its native tags. Codec payload bytes, timestamps, tag ordering,
  /// header flag bits and StreamID are retained; tag sizes/back-pointers are recalculated.
  /// </summary>
  public static void Remux(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var file = ReadAll(input);
    var parsed = Parse(file);
    Mux(output, parsed.Tags, parsed.Version, parsed.HasAudio, parsed.HasVideo);
  }

  /// <summary>Checks whether the encoded audio stream can be wrapped in classic FLV audio tags.</summary>
  public static bool CanMuxAudio(AudioStreamFormat format, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    if (!SupportedAudioCodecs.Contains(format.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"FLV audio muxing supports AAC and MP3, not codec '{format.CodecId}'.";
      return false;
    }

    if (format.Channels is < 1 or > 2) {
      reason = "classic FLV audio muxing supports mono or stereo streams.";
      return false;
    }

    if (format.SampleRate <= 0) {
      reason = "FLV audio muxing requires a positive sample rate.";
      return false;
    }

    if (format.CodecId.Equals("mp3", StringComparison.OrdinalIgnoreCase)
        && format.SampleRate is not (8000 or 11025 or 22050 or 44100 or 48000)) {
      reason = "classic FLV MP3 supports 8, 11.025, 22.05, 44.1 or 48 kHz.";
      return false;
    }

    reason = null;
    return true;
  }

  /// <summary>Writes one encoded AAC or MP3 stream into an audio-only FLV container.</summary>
  public static void MuxAudio(Stream output, AudioEncodedStream stream) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    if (!CanMuxAudio(stream.Format, out var reason))
      throw new NotSupportedException(reason);

    var tags = stream.Format.CodecId.Equals("mp3", StringComparison.OrdinalIgnoreCase)
      ? BuildMp3Tags(stream)
      : BuildAacTags(stream);
    Mux(output, tags, hasAudio: true, hasVideo: false);
  }

  /// <summary>
  /// Exposes the first classic AAC audio stream as container-neutral encoded packets so callers can
  /// remux it without an encode/decode cycle. Other FLV audio codecs currently return false.
  /// </summary>
  public static bool TryDemuxAudio(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    try {
      var parsed = Parse(ReadAll(input));
      byte[]? config = null;
      var packets = new List<AudioPacket>();
      foreach (var tag in parsed.Tags) {
        if (tag.TagType != FlvReader.TagAudio || tag.Body.Length < 2)
          continue;
        if ((tag.Body[0] >> 4) != 10)
          continue;
        switch (tag.Body[1]) {
          case 0:
            config = tag.Body.AsSpan(2).ToArray();
            break;
          case 1:
            packets.Add(new AudioPacket(tag.Body.AsSpan(2).ToArray(), DurationSamples: 1024));
            break;
        }
      }

      if (config is not { Length: >= 2 } || packets.Count == 0 || !TryParseAudioSpecificConfig(config, out var sampleRate, out var channels)) {
        stream = null;
        return false;
      }

      stream = new AudioEncodedStream(
        new AudioStreamFormat("aac", sampleRate, channels),
        packets,
        config);
      return true;
    } catch (InvalidDataException) {
      stream = null;
      return false;
    }
  }

  private static List<FlvRawTag> BuildAacTags(AudioEncodedStream stream) {
    if (stream.CodecPrivateData is not { Length: >= 2 } config)
      throw new InvalidDataException("FLV AAC muxing requires AudioSpecificConfig codec-private data.");

    // Adobe specifies that SoundRate/SoundSize/SoundType are ignored for AAC. The conventional
    // interoperable value is 44 kHz, 16-bit, stereo (0xAF), also used by established muxers.
    const byte audioHeader = 0xAF;
    var tags = new List<FlvRawTag>(stream.Packets.Count + 1) {
      new(FlvReader.TagAudio, 0, 0, Concat([audioHeader, 0], config)),
    };

    long samplePosition = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        continue;
      if (packet.Data.Length == 0)
        throw new InvalidDataException("FLV cannot carry an empty AAC access unit.");

      tags.Add(new FlvRawTag(
        FlvReader.TagAudio,
        SamplesToTimestamp(samplePosition, stream.Format.SampleRate),
        0,
        Concat([audioHeader, 1], packet.Data)));
      samplePosition = checked(samplePosition + (packet.DurationSamples > 0 ? packet.DurationSamples : 1024));
    }

    if (tags.Count == 1)
      throw new ArgumentException("FLV AAC muxing requires at least one access unit.", nameof(stream));
    return tags;
  }

  private static List<FlvRawTag> BuildMp3Tags(AudioEncodedStream stream) {
    var soundFormat = stream.Format.SampleRate == 8000 ? 14 : 2;
    var rateBits = stream.Format.SampleRate switch {
      11025 => 1,
      22050 => 2,
      44100 or 48000 => 3,
      8000 => 0,
      _ => throw new NotSupportedException($"FLV MP3 sample rate {stream.Format.SampleRate} Hz is not supported."),
    };
    var audioHeader = (byte)((soundFormat << 4) | (rateBits << 2) | 0x02 | (stream.Format.Channels > 1 ? 1 : 0));
    var tags = new List<FlvRawTag>(stream.Packets.Count);
    long samplePosition = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        continue;
      if (packet.Data.Length == 0)
        throw new InvalidDataException("FLV cannot carry an empty MP3 frame packet.");
      if (packet.DurationSamples <= 0)
        throw new InvalidDataException("FLV MP3 muxing requires DurationSamples on every packet so timestamps are not invented.");

      tags.Add(new FlvRawTag(
        FlvReader.TagAudio,
        SamplesToTimestamp(samplePosition, stream.Format.SampleRate),
        0,
        Concat([audioHeader], packet.Data)));
      samplePosition = checked(samplePosition + packet.DurationSamples);
    }

    if (tags.Count == 0)
      throw new ArgumentException("FLV MP3 muxing requires at least one frame packet.", nameof(stream));
    return tags;
  }

  private static void WriteTag(Stream output, FlvRawTag tag) {
    if (tag.Body.Length > 0xFFFFFF)
      throw new InvalidDataException($"FLV tag body is {tag.Body.Length} bytes; DataSize is limited to 24 bits.");
    if (tag.StreamId > 0xFFFFFF)
      throw new InvalidDataException($"FLV StreamID {tag.StreamId} exceeds 24 bits.");

    Span<byte> header = stackalloc byte[11];
    header[0] = tag.HeaderByte;
    WriteUInt24(header[1..4], (uint)tag.Body.Length);
    WriteUInt24(header[4..7], tag.TimestampMs & 0xFFFFFF);
    header[7] = (byte)(tag.TimestampMs >> 24);
    WriteUInt24(header[8..11], tag.StreamId);
    output.Write(header);
    output.Write(tag.Body);

    Span<byte> previousTagSize = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(previousTagSize, checked((uint)(11 + tag.Body.Length)));
    output.Write(previousTagSize);
  }

  private static ParsedFlv Parse(ReadOnlySpan<byte> data) {
    if (data.Length < FlvReader.HeaderSize + 4 || data[0] != (byte)'F' || data[1] != (byte)'L' || data[2] != (byte)'V')
      throw new InvalidDataException("FLV: missing or truncated header.");

    var version = data[3];
    var flags = data[4];
    var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data[5..9]);
    if (dataOffset < FlvReader.HeaderSize || dataOffset > (uint)(data.Length - 4))
      throw new InvalidDataException("FLV: DataOffset is out of range.");
    var offset = checked((int)dataOffset);
    if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)) != 0)
      throw new InvalidDataException("FLV: PreviousTagSize0 must be zero.");

    var tags = new List<FlvRawTag>();
    var p = offset + 4;
    while (p < data.Length) {
      if (data.Length - p < 15)
        throw new InvalidDataException("FLV: truncated tag header or PreviousTagSize.");

      var size = ReadUInt24(data.Slice(p + 1, 3));
      var bodyStart = p + 11;
      var bodyEnd = checked(bodyStart + (int)size);
      if (bodyEnd > data.Length - 4)
        throw new InvalidDataException("FLV: tag body overruns the input.");

      var timestamp = ReadUInt24(data.Slice(p + 4, 3)) | ((uint)data[p + 7] << 24);
      var streamId = ReadUInt24(data.Slice(p + 8, 3));
      var previousSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(bodyEnd, 4));
      if (previousSize != 11u + size)
        throw new InvalidDataException($"FLV: PreviousTagSize {previousSize} does not match tag size {11u + size}.");

      tags.Add(new FlvRawTag(data[p], timestamp, streamId, data.Slice(bodyStart, (int)size).ToArray()));
      p = bodyEnd + 4;
    }

    return new ParsedFlv(version, (flags & 0x04) != 0, (flags & 0x01) != 0, tags);
  }

  private static bool TryParseAudioSpecificConfig(ReadOnlySpan<byte> config, out int sampleRate, out int channels) {
    sampleRate = 0;
    channels = 0;
    if (config.Length < 2)
      return false;
    var sampleRateIndex = ((config[0] & 0x07) << 1) | (config[1] >> 7);
    if (sampleRateIndex >= AacSampleRates.Length)
      return false;
    channels = (config[1] >> 3) & 0x0F;
    if (channels is < 1 or > 7)
      return false;
    sampleRate = AacSampleRates[sampleRateIndex];
    return true;
  }

  private static uint SamplesToTimestamp(long samples, int sampleRate) {
    if (samples < 0 || sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(samples));
    var milliseconds = checked(samples * 1000 / sampleRate);
    if ((ulong)milliseconds > uint.MaxValue)
      throw new InvalidDataException("FLV timestamp exceeds the 32-bit timestamp field.");
    return (uint)milliseconds;
  }

  private static byte[] Concat(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix) {
    var result = new byte[prefix.Length + suffix.Length];
    prefix.CopyTo(result);
    suffix.CopyTo(result.AsSpan(prefix.Length));
    return result;
  }

  private static uint ReadUInt24(ReadOnlySpan<byte> source)
    => ((uint)source[0] << 16) | ((uint)source[1] << 8) | source[2];

  private static void WriteUInt24(Span<byte> destination, uint value) {
    destination[0] = (byte)(value >> 16);
    destination[1] = (byte)(value >> 8);
    destination[2] = (byte)value;
  }

  private static byte[] ReadAll(Stream input) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ms.ToArray();
  }

  private sealed record ParsedFlv(byte Version, bool HasAudio, bool HasVideo, IReadOnlyList<FlvRawTag> Tags);
}
