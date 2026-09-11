#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Aac;
using Codec.MsAdpcm;
using Codec.Vorbis;
using Compression.Registry;
using FileFormat.Mp4;

namespace FileFormat.Akb;

public sealed partial class AkbFormatDescriptor {
  private static AkbWriteEntry BuildOggInput(string name, byte[] data, FormatCreateOptions options) {
    using var source = new MemoryStream(data, writable: false);
    var info = VorbisCodec.ReadStreamInfo(source);
    var sampleCount = info.DurationSamples is > 0 and <= uint.MaxValue ? (uint)info.DurationSamples.Value : ReadUInt(options, null, "SampleCount", 0);
    var loops = ReadLoopOptions(options, null, sampleCount, validateAgainstCount: sampleCount != 0);
    return new AkbWriteEntry(name, data, AkbCodec.OggVorbis, info.SampleRate, info.Channels,
      sampleCount, loops.Start, loops.End, loops.Start2, loops.End2);
  }

  private static AkbWriteEntry BuildRawInput(string name, byte[] data, AkbCodec codec, FormatCreateOptions options) {
    var sampleRate = options.GetOptionInt("SampleRate", 44_100);
    var channels = options.GetOptionInt("Channels", 2);
    var sampleCount = ReadUInt(options, null, "SampleCount", 0);
    var loops = ReadLoopOptions(options, null, sampleCount, validateAgainstCount: sampleCount != 0);
    return new AkbWriteEntry(name, data, codec, sampleRate, channels, sampleCount,
      loops.Start, loops.End, loops.Start2, loops.End2);
  }

  private static AkbWriteEntry BuildPcmInput(string name, byte[] data, FormatCreateOptions options) {
    var sampleRate = options.GetOptionInt("SampleRate", 44_100);
    var channels = options.GetOptionInt("Channels", 2);
    if (channels <= 0 || data.Length % checked(channels * 2) != 0)
      throw new InvalidDataException("Raw AKB2 PCM input must contain integral PCM16LE frames for the configured channel count.");
    var samples = checked((uint)(data.Length / (channels * 2)));
    var loops = ReadLoopOptions(options, null, samples, validateAgainstCount: true);
    return new AkbWriteEntry(name, data, AkbCodec.Pcm16Le, sampleRate, channels, samples,
      loops.Start, loops.End, loops.Start2, loops.End2);
  }

  private static AkbWriteEntry BuildMsAdpcmInput(string name, byte[] data, FormatCreateOptions options) {
    var sampleRate = options.GetOptionInt("SampleRate", 44_100);
    var channels = options.GetOptionInt("Channels", 2);
    var blockAlign = options.GetOptionInt("BlockAlign", 512);
    if (channels is not (1 or 2) || blockAlign < 7 * channels || blockAlign == 0 || data.Length % blockAlign != 0)
      throw new InvalidDataException("Raw AKB MS-ADPCM requires mono/stereo complete blocks and a valid BlockAlign.");
    var samplesPerBlock = 2 + (blockAlign - 7 * channels) * 2 / channels;
    var derived = checked((uint)(data.Length / blockAlign * samplesPerBlock));
    var samples = ReadUInt(options, null, "SampleCount", derived);
    var loops = ReadLoopOptions(options, null, samples, validateAgainstCount: samples != 0);
    return new AkbWriteEntry(name, data, AkbCodec.MsAdpcm, sampleRate, channels, samples,
      loops.Start, loops.End, loops.Start2, loops.End2, blockAlign);
  }

  private static byte[] EncodeVorbis(AudioPcmBuffer pcm, FormatCreateOptions options, LoopValues loops) {
    var quality = 0.5f;
    if (options.GetString("Quality") is { } text
        && !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out quality))
      throw new ArgumentException($"Invalid Vorbis Quality '{text}'.", nameof(options));
    Dictionary<string, string>? comments = null;
    void AddLoop(string key, uint value) {
      if (value == 0) return;
      comments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      comments[key] = value.ToString(CultureInfo.InvariantCulture);
    }
    AddLoop("LoopStart", loops.Start);
    AddLoop("LoopEnd", loops.End);
    AddLoop("LoopStart2", loops.Start2);
    AddLoop("LoopEnd2", loops.End2);
    return VorbisEncoder.Encode(ToShorts(pcm.InterleavedData),
      new VorbisEncoderOptions(pcm.Format.SampleRate, pcm.Format.Channels, quality, Comments: comments));
  }

  private static byte[] EncodeM4a(AudioPcmBuffer pcm, FormatCreateOptions options) {
    using var m4a = new MemoryStream();
    new Mp4FormatDescriptor().EncodePcm(m4a, pcm, "aac", options);
    return m4a.ToArray();
  }

  private static byte[] DecodeVorbis(byte[] payload) {
    using var source = new MemoryStream(payload, writable: false);
    using var pcm = new MemoryStream();
    VorbisCodec.Decompress(source, pcm);
    return pcm.ToArray();
  }

  private static byte[] DecodeMsAdpcm(byte[] payload, AkbEntry entry) {
    if (entry.BlockAlign <= 0)
      throw new InvalidDataException("AKB MS-ADPCM entry has no valid block align.");
    var planar = MsAdpcmCodec.Decode(payload, entry.BlockAlign, entry.Channels);
    var frames = planar.Length == 0 ? 0 : planar[0].Length;
    var result = new byte[checked(frames * entry.Channels * 2)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < entry.Channels; ++channel)
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan((frame * entry.Channels + channel) * 2, 2), planar[channel][frame]);
    return result;
  }

  private static byte[] DecodeM4aAac(byte[] payload, AkbEntry entry) {
    var tracks = new Mp4Demuxer().Demux(payload);
    var track = tracks.SingleOrDefault(static candidate => candidate.HandlerType == "soun" && candidate.CodecFourCc == "mp4a")
                ?? throw new NotSupportedException("Classic AKB codec 0x06 payload is not an AAC mp4a track supported by the managed decoder path.");
    var rateIndex = Array.IndexOf(AacAdtsReader.SampleRateTable, entry.SampleRate);
    if (rateIndex is < 0 or > 12 || entry.Channels is < 1 or > 7)
      throw new NotSupportedException("AKB M4A metadata cannot be represented by an ADTS header.");
    using var adts = new MemoryStream();
    foreach (var sample in track.Samples) {
      var frameLength = checked(AacAdtsReader.ShortHeaderLength + sample.Data.Length);
      adts.Write(AacAdtsReader.BuildHeader(1, rateIndex, entry.Channels, frameLength));
      adts.Write(sample.Data);
    }
    adts.Position = 0;
    using var pcm = new MemoryStream();
    AacCodec.Decompress(adts, pcm);
    return pcm.ToArray();
  }

  private static byte[] TrimPcm(byte[] pcm, uint sampleCount, int channels) {
    if (sampleCount == 0) return pcm;
    var wanted = checked((long)sampleCount * channels * 2);
    if (wanted >= pcm.LongLength) return pcm;
    return pcm.AsSpan(0, checked((int)wanted)).ToArray();
  }

  private static short[] ToShorts(byte[] data) {
    if ((data.Length & 1) != 0)
      throw new InvalidDataException("PCM16 byte count must be even.");
    var result = new short[data.Length / 2];
    for (var i = 0; i < result.Length; ++i)
      result[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2));
    return result;
  }

  private static byte[] ConcatenatePackets(IReadOnlyList<AudioPacket> packets) {
    var size = packets.Where(static packet => !packet.IsHeader).Sum(static packet => (long)packet.Data.Length);
    if (size > int.MaxValue)
      throw new NotSupportedException("Encoded AKB payload exceeds the managed array limit.");
    var result = new byte[(int)size];
    var offset = 0;
    foreach (var packet in packets) {
      if (packet.IsHeader) continue;
      packet.Data.CopyTo(result, offset);
      offset += packet.Data.Length;
    }
    return result;
  }

  private static uint ResolveSampleCount(AudioEncodedStream stream) {
    if (TryProperty(stream.Format.Properties, "akb.sample-count", out var raw)
        && uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored))
      return stored;
    var duration = stream.Packets.Where(static packet => !packet.IsHeader).Sum(static packet => packet.DurationSamples);
    return duration <= 0 ? 0u : checked((uint)duration);
  }

  private static Dictionary<string, string> BuildStreamProperties(AkbContainerKind kind, AkbEntry entry) => new(StringComparer.OrdinalIgnoreCase) {
    ["akb.variant"] = kind == AkbContainerKind.Classic ? "Classic" : "Akb2",
    ["akb.version"] = entry.Version.ToString(CultureInfo.InvariantCulture),
    ["akb.sample-count"] = entry.SampleCount.ToString(CultureInfo.InvariantCulture),
    ["akb.loop-start"] = entry.LoopStart.ToString(CultureInfo.InvariantCulture),
    ["akb.loop-end"] = entry.LoopEnd.ToString(CultureInfo.InvariantCulture),
    ["akb.loop-start2"] = entry.AlternateLoopStart.ToString(CultureInfo.InvariantCulture),
    ["akb.loop-end2"] = entry.AlternateLoopEnd.ToString(CultureInfo.InvariantCulture),
    ["akb.encrypted"] = entry.Encrypted ? "true" : "false",
    ["akb.block-align"] = entry.BlockAlign.ToString(CultureInfo.InvariantCulture),
  };

  private static Dictionary<string, string> BuildContainerProperties(AkbReader reader) {
    var first = reader.Entries[0];
    return BuildStreamProperties(reader.ContainerKind, first);
  }

  private static AkbContainerKind ResolveVariant(FormatCreateOptions options, string codecId, IReadOnlyDictionary<string, string>? properties) {
    var requested = options.HasOption("Variant") ? options.GetString("Variant") : null;
    if (string.IsNullOrWhiteSpace(requested) || requested.Equals("Auto", StringComparison.OrdinalIgnoreCase)) {
      if (TryProperty(properties, "akb.variant", out var stored))
        requested = stored;
      else if ((NormalizeCodec(codecId) is "aac" or "m4a-aac") || ResolveEncrypt(options, properties))
        return AkbContainerKind.Classic;
      else
        return AkbContainerKind.Akb2;
    }
    return requested.Equals("Classic", StringComparison.OrdinalIgnoreCase) ? AkbContainerKind.Classic
      : requested.Equals("Akb2", StringComparison.OrdinalIgnoreCase) ? AkbContainerKind.Akb2
      : throw new ArgumentException($"Unknown AKB Variant '{requested}'.", nameof(options));
  }

  private static byte ResolveClassicVersion(FormatCreateOptions options, string codecId, IReadOnlyDictionary<string, string>? properties) {
    var requested = options.HasOption("ClassicVersion") ? options.GetString("ClassicVersion") : null;
    if (string.IsNullOrWhiteSpace(requested) || requested.Equals("Auto", StringComparison.OrdinalIgnoreCase)) {
      if (TryProperty(properties, "akb.version", out var stored))
        requested = stored;
      else if (ResolveEncrypt(options, properties))
        return 3;
      else
        return NormalizeCodec(codecId) is "aac" or "m4a-aac" ? (byte)0 : (byte)2;
    }
    if (!byte.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
        || version is not (0 or 2 or 3))
      throw new ArgumentException($"ClassicVersion '{requested}' is not one of 0, 2 or 3.", nameof(options));
    return version;
  }

  private static bool ResolveEncrypt(FormatCreateOptions options, IReadOnlyDictionary<string, string>? properties) {
    if (options.HasOption("Encrypt"))
      return options.GetOptionBool("Encrypt", false);
    return TryProperty(properties, "akb.encrypted", out var value)
           && bool.TryParse(value, out var parsed) && parsed;
  }

  private static LoopValues ReadLoopOptions(FormatCreateOptions options, IReadOnlyDictionary<string, string>? properties,
                                             uint sampleCount, bool validateAgainstCount) {
    var result = new LoopValues(
      ReadUInt(options, properties, "LoopStart", PropertyUInt(properties, "akb.loop-start")),
      ReadUInt(options, properties, "LoopEnd", PropertyUInt(properties, "akb.loop-end")),
      ReadUInt(options, properties, "LoopStart2", PropertyUInt(properties, "akb.loop-start2")),
      ReadUInt(options, properties, "LoopEnd2", PropertyUInt(properties, "akb.loop-end2")));
    if (result.End != 0 && result.End <= result.Start)
      throw new ArgumentException("LoopEnd must be greater than LoopStart.", nameof(options));
    if (result.End2 != 0 && result.End2 <= result.Start2)
      throw new ArgumentException("LoopEnd2 must be greater than LoopStart2.", nameof(options));
    if (validateAgainstCount && sampleCount != 0) {
      if (result.Start > sampleCount || result.End > (sampleCount == uint.MaxValue ? uint.MaxValue : sampleCount + 1u) || result.Start2 > sampleCount || result.End2 > (sampleCount == uint.MaxValue ? uint.MaxValue : sampleCount + 1u))
        throw new ArgumentException("AKB loop points exceed the sample count.", nameof(options));
    }
    return result;
  }

  private static uint ReadUInt(FormatCreateOptions options, IReadOnlyDictionary<string, string>? properties, string key, uint fallback) {
    if (options.HasOption(key)) {
      var raw = options.GetString(key);
      if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
      throw new ArgumentException($"AKB option {key}='{raw}' is not an unsigned 32-bit integer.", nameof(options));
    }
    return fallback;
  }

  private static int ResolveInt(FormatCreateOptions options, IReadOnlyDictionary<string, string>? properties, string key, int fallback) {
    if (options.HasOption(key)) {
      if (options.TryGetInt(key, out var value)) return value;
      throw new ArgumentException($"AKB option {key} is not an integer.", nameof(options));
    }
    var propertyKey = key.Equals("BlockAlign", StringComparison.OrdinalIgnoreCase) ? "akb.block-align" : key;
    if (TryProperty(properties, propertyKey, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
      return parsed;
    return fallback;
  }

  private static uint PropertyUInt(IReadOnlyDictionary<string, string>? properties, string key) {
    if (TryProperty(properties, key, out var raw)
        && uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
      return value;
    return 0;
  }

  private static bool TryProperty(IReadOnlyDictionary<string, string>? properties, string key, out string value) {
    if (properties is not null)
      foreach (var pair in properties)
        if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) {
          value = pair.Value;
          return true;
        }
    value = "";
    return false;
  }

  private static string NormalizeCodec(string codecId) => codecId.ToLowerInvariant() switch {
    "pcm" or "pcm16" or "pcm16le" => "pcm16le",
    "ms-adpcm" or "msadpcm" => "ms-adpcm",
    "vorbis" or "ogg-vorbis" => codecId.Equals("vorbis", StringComparison.OrdinalIgnoreCase) ? "vorbis" : "ogg-vorbis",
    "aac" or "aac-lc" => "aac",
    "m4a-aac" or "m4a" => "m4a-aac",
    var value => value,
  };

  private static string CodecId(AkbCodec codec) => codec switch {
    AkbCodec.Pcm16Le => "pcm16le",
    AkbCodec.MsAdpcm => "ms-adpcm",
    AkbCodec.OggVorbis => "ogg-vorbis",
    AkbCodec.M4aAac => "m4a-aac",
    _ => throw new ArgumentOutOfRangeException(nameof(codec)),
  };

  private static string CodecLabel(AkbCodec codec) => codec switch {
    AkbCodec.Pcm16Le => "PCM16LE",
    AkbCodec.MsAdpcm => "MS-ADPCM",
    AkbCodec.OggVorbis => "Ogg Vorbis",
    AkbCodec.M4aAac => "M4A/AAC",
    _ => codec.ToString(),
  };

  private static byte[] BuildMetadata(AkbReader reader) {
    var sb = new StringBuilder();
    sb.AppendLine("[akb]");
    sb.Append("variant = ").AppendLine(reader.ContainerKind.ToString());
    sb.Append("version = ").AppendLine(reader.VersionByte.ToString(CultureInfo.InvariantCulture));
    sb.Append("entry_count = ").AppendLine(reader.Entries.Count.ToString(CultureInfo.InvariantCulture));
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var entry = reader.Entries[i];
      sb.AppendLine().Append("[stream.").Append(i.ToString(CultureInfo.InvariantCulture)).AppendLine("]");
      sb.Append("codec = ").AppendLine(CodecLabel(entry.Codec));
      sb.Append("sample_rate = ").AppendLine(entry.SampleRate.ToString(CultureInfo.InvariantCulture));
      sb.Append("channels = ").AppendLine(entry.Channels.ToString(CultureInfo.InvariantCulture));
      sb.Append("samples = ").AppendLine(entry.SampleCount.ToString(CultureInfo.InvariantCulture));
      sb.Append("loop_start = ").AppendLine(entry.LoopStart.ToString(CultureInfo.InvariantCulture));
      sb.Append("loop_end = ").AppendLine(entry.LoopEnd.ToString(CultureInfo.InvariantCulture));
      sb.Append("loop_start2 = ").AppendLine(entry.AlternateLoopStart.ToString(CultureInfo.InvariantCulture));
      sb.Append("loop_end2 = ").AppendLine(entry.AlternateLoopEnd.ToString(CultureInfo.InvariantCulture));
      sb.Append("block_align = ").AppendLine(entry.BlockAlign.ToString(CultureInfo.InvariantCulture));
      sb.Append("encrypted = ").AppendLine(entry.Encrypted ? "true" : "false");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private readonly record struct LoopValues(uint Start, uint End, uint Start2, uint End2);
}
