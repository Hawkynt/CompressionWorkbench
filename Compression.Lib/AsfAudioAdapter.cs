using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Lib;

/// <summary>
/// Bridges Microsoft ASF to the container-neutral audio conversion pipeline. Codec encoding is
/// delegated to the existing managed WAVE adapter, then the untouched WAVEFORMATEX payload is
/// muxed into ASF. Demux preserves the ASF media-object boundaries and WAVEFORMATEX private data.
/// </summary>
internal sealed class AsfAudioAdapter : IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {
  internal static readonly AsfAudioAdapter Instance = new();

  private static readonly WavAudioAdapter Wav = new();
  private static readonly string[] PacketCodecs = [
    "pcm", "float", "alaw", "mulaw", "ms-adpcm", "ima-adpcm",
    "oki-adpcm", "dialogic-oki-adpcm", "gsm610", "g721",
    "g726-16", "g726-24", "g726-32", "g726-40",
    "g726-apicom-16", "g726-apicom-24", "g726-apicom-32", "g726-apicom-40",
    "g722", "g722-apicom", "mp2", "mp3", "mpeg-layer3",
    "aac", "aac-lc", "aac-raw", "aac-adts",
    "wmavoice", "wmav1", "wmav2", "wmapro", "wmalossless", "ac3", "dts",
  ];

  public IReadOnlyList<string> SupportedEncodeCodecs => Wav.SupportedEncodeCodecs;
  public IReadOnlyList<string> SupportedMuxCodecs => PacketCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason)
    => Wav.CanEncode(format, codecId, options, out reason);

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    using var wave = new MemoryStream();
    Wav.EncodePcm(wave, pcm, codecId, options);
    var encoded = ParseWave(wave.ToArray());
    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["format_tag"] = $"0x{encoded.FormatTag:X4}",
      ["byte_rate"] = encoded.ByteRate.ToString(CultureInfo.InvariantCulture),
      ["block_align"] = encoded.BlockAlign.ToString(CultureInfo.InvariantCulture),
      ["bits_per_sample"] = encoded.BitsPerSample.ToString(CultureInfo.InvariantCulture),
    };
    this.Mux(output,
      new AudioEncodedStream(
        new AudioStreamFormat(CodecId(encoded.FormatTag, encoded.BitsPerSample), encoded.SampleRate,
          encoded.Channels, encoded.BitsPerSample, properties),
        [new AudioPacket(encoded.Payload, pcm.FrameCount)],
        encoded.ExtraData),
      options);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var bytes = Materialize(input);
    if (!this.TryDemux(new MemoryStream(bytes, writable: false), out var encoded) || encoded is null)
      throw new NotSupportedException("ASF PCM conversion requires exactly one demuxable audio stream.");

    try {
      using var wave = new MemoryStream(BuildWave(encoded));
      return Wav.DecodePcm(wave);
    } catch (NotSupportedException) {
      return DecodeViaDescriptorChannels(bytes);
    }
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;
    var bytes = Materialize(input);
    var descriptor = new AsfFormatDescriptor();
    var entries = descriptor.List(new MemoryStream(bytes, writable: false), null);
    var rawEntries = entries
      .Where(static entry => entry.Kind == "Stream" && entry.Name.StartsWith("streams/stream_", StringComparison.OrdinalIgnoreCase) &&
                             entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (rawEntries.Length != 1)
      return false;

    var raw = rawEntries[0];
    var number = ParseStreamNumber(raw.Name);
    if (number <= 0)
      return false;
    var infoName = $"streams/stream_{number:D2}.info.txt";
    if (!entries.Any(entry => entry.Name.Equals(infoName, StringComparison.OrdinalIgnoreCase)))
      return false;

    var info = ParseFlat(Extract(descriptor, bytes, infoName));
    if (!Get(info, "type").Equals("audio", StringComparison.OrdinalIgnoreCase) ||
        bool.TryParse(Get(info, "encrypted"), out var encrypted) && encrypted)
      return false;
    if (!TryParseU16(Get(info, "format_tag"), out var tag) ||
        !int.TryParse(Get(info, "channels"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channels) || channels <= 0 ||
        !int.TryParse(Get(info, "sample_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate) || sampleRate <= 0)
      return false;

    var bits = int.TryParse(Get(info, "bits_per_sample"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBits)
      ? parsedBits : 0;
    var payload = Extract(descriptor, bytes, raw.Name);
    var sizes = ParseObjectSizes(Get(info, "object_sizes"), payload.Length);
    var packets = new List<AudioPacket>();
    var offset = 0;
    foreach (var size in sizes) {
      packets.Add(new AudioPacket(payload.AsSpan(offset, size).ToArray()));
      offset += size;
    }

    var extra = ParseHex(Get(info, "extra_data_hex"));
    stream = new AudioEncodedStream(
      new AudioStreamFormat(CodecId(tag, bits), sampleRate, channels, bits, info),
      packets,
      extra.Length == 0 ? null : extra);
    return true;
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (stream.SampleRate <= 0 || stream.Channels <= 0 || stream.Channels > ushort.MaxValue) {
      reason = "ASF audio muxing requires a positive sample rate and a WAVEFORMATEX-sized channel count";
      return false;
    }
    if (TryFormatTag(stream, out _)) {
      reason = null;
      return true;
    }
    reason = $"ASF cannot map codec '{stream.CodecId}' to a WAVEFORMATEX tag; provide format_tag in stream properties for an exact remux";
    return false;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    if (!TryFormatTag(stream.Format, out var tag))
      throw new InvalidOperationException("ASF codec mapping disappeared after validation.");

    var dataPackets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (dataPackets.Length == 0)
      throw new ArgumentException("ASF audio muxing requires at least one non-header packet.", nameof(stream));
    var payloadLength = dataPackets.Sum(static packet => (long)packet.Data.Length);
    if (payloadLength > int.MaxValue)
      throw new NotSupportedException("ASF in-memory mux payload exceeds 2 GiB.");
    var payload = new byte[(int)payloadLength];
    var offset = 0;
    foreach (var packet in dataPackets) {
      packet.Data.CopyTo(payload, offset);
      offset += packet.Data.Length;
    }

    var properties = stream.Format.Properties ?? new Dictionary<string, string>();
    var bits = PropertyInt(properties, "bits_per_sample", stream.Format.BitsPerSample);
    var blockAlign = PropertyInt(properties, "block_align", DefaultBlockAlign(tag, stream.Format.Channels, bits));
    var byteRate = PropertyLong(properties, "byte_rate", DefaultByteRate(tag, stream.Format.SampleRate, blockAlign, payloadLength, dataPackets));
    var extra = stream.CodecPrivateData ?? [];
    var info = new StringBuilder();
    info.AppendLine("type = audio");
    info.AppendLine($"format_tag = 0x{tag:X4}");
    info.AppendLine($"channels = {stream.Format.Channels}");
    info.AppendLine($"sample_rate = {stream.Format.SampleRate}");
    info.AppendLine($"byte_rate = {Math.Clamp(byteRate, 0, uint.MaxValue)}");
    info.AppendLine($"block_align = {Math.Clamp(blockAlign, 0, ushort.MaxValue)}");
    info.AppendLine($"bits_per_sample = {Math.Clamp(bits, 0, ushort.MaxValue)}");
    info.AppendLine("encrypted = false");
    if (extra.Length > 0)
      info.AppendLine($"extra_data_hex = {Convert.ToHexString(extra)}");
    info.AppendLine($"object_sizes = {string.Join(',', dataPackets.Select(static packet => packet.Data.Length))}");

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("streams/stream_01.info.txt", Encoding.UTF8.GetBytes(info.ToString())),
      ArchiveInputInfo.InMemory("streams/stream_01.bin", payload),
    };
    if (options.TryGetInt("packet-size", out var packetSize))
      inputs.Add(ArchiveInputInfo.InMemory("metadata.ini",
        Encoding.UTF8.GetBytes($"[FileProperties]\nmax_packet_size = {packetSize}\n")));

    new AsfFormatDescriptor().Create(output, inputs, options);
  }

  private AudioPcmBuffer DecodeViaDescriptorChannels(byte[] bytes) {
    var descriptor = new AsfFormatDescriptor();
    var entries = descriptor.List(new MemoryStream(bytes, writable: false), null);
    var infos = entries.Where(static entry => entry.Name.EndsWith(".info.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (infos.Length != 1)
      throw new NotSupportedException("ASF PCM conversion cannot choose among multiple audio streams.");
    var streamNumber = ParseStreamNumber(infos[0].Name);
    var prefix = $"streams/stream_{streamNumber:D2}/";
    var channels = entries
      .Where(entry => entry.Kind == "Channel" && entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                      entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(entry => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(entry.Name)))
      .ToArray();
    if (channels.Length == 0)
      throw new NotSupportedException("The ASF audio codec is not decoded by the managed codec set.");

    var decoded = channels.Select(entry => {
      using var channel = new MemoryStream(Extract(descriptor, bytes, entry.Name), writable: false);
      return Wav.DecodePcm(channel);
    }).ToArray();
    var first = decoded[0];
    if (decoded.Any(item => item.Format.Channels != 1 || item.Format.SampleRate != first.Format.SampleRate ||
                            item.Format.BitsPerSample != first.Format.BitsPerSample || item.Format.Encoding != first.Format.Encoding ||
                            item.InterleavedData.Length != first.InterleavedData.Length))
      throw new InvalidDataException("ASF decoded channel WAVs do not share one PCM geometry.");
    if (decoded.Length == 1)
      return first;

    return new AudioPcmBuffer(
      first.Format with { Channels = decoded.Length, ChannelMask = null },
      PcmCodec.Interleave(decoded.Select(static item => item.InterleavedData).ToArray(), first.Format.BitsPerSample));
  }

  private static byte[] BuildWave(AudioEncodedStream stream) {
    if (!TryFormatTag(stream.Format, out var tag))
      throw new NotSupportedException($"Cannot reconstruct WAVEFORMATEX for '{stream.Format.CodecId}'.");
    var properties = stream.Format.Properties ?? new Dictionary<string, string>();
    var bits = PropertyInt(properties, "bits_per_sample", stream.Format.BitsPerSample);
    var blockAlign = PropertyInt(properties, "block_align", DefaultBlockAlign(tag, stream.Format.Channels, bits));
    var payloadLength = stream.Packets.Where(static packet => !packet.IsHeader).Sum(static packet => (long)packet.Data.Length);
    if (payloadLength > int.MaxValue) throw new NotSupportedException("WAVE reconstruction exceeds 2 GiB.");
    var byteRate = PropertyLong(properties, "byte_rate", DefaultByteRate(tag, stream.Format.SampleRate, blockAlign, payloadLength,
      stream.Packets.Where(static packet => !packet.IsHeader).ToArray()));
    var extra = stream.CodecPrivateData ?? [];
    var fmtSize = 18 + extra.Length;
    var dataSize = (int)payloadLength;
    var riffSize = checked(4 + 8 + fmtSize + (fmtSize & 1) + 8 + dataSize + (dataSize & 1));
    var result = new byte[8 + riffSize];
    "RIFF"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)riffSize));
    "WAVE"u8.CopyTo(result.AsSpan(8));
    var p = 12;
    "fmt "u8.CopyTo(result.AsSpan(p));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p + 4), checked((uint)fmtSize));
    p += 8;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(p), tag);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(p + 2), checked((ushort)stream.Format.Channels));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p + 4), checked((uint)stream.Format.SampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p + 8), checked((uint)Math.Clamp(byteRate, 0, uint.MaxValue)));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(p + 12), checked((ushort)Math.Clamp(blockAlign, 0, ushort.MaxValue)));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(p + 14), checked((ushort)Math.Clamp(bits, 0, ushort.MaxValue)));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(p + 16), checked((ushort)extra.Length));
    extra.CopyTo(result, p + 18);
    p += fmtSize + (fmtSize & 1);
    "data"u8.CopyTo(result.AsSpan(p));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p + 4), checked((uint)dataSize));
    p += 8;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader) continue;
      packet.Data.CopyTo(result, p);
      p += packet.Data.Length;
    }
    return result;
  }

  private static WaveEnvelope ParseWave(byte[] bytes) {
    if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
      throw new InvalidDataException("Managed WAVE encoder did not produce RIFF/WAVE.");
    ushort tag = 0, channels = 0, blockAlign = 0, bits = 0;
    uint sampleRate = 0, byteRate = 0;
    byte[] extra = [];
    byte[]? payload = null;
    var haveFmt = false;
    for (var p = 12; p + 8 <= bytes.Length;) {
      var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4)));
      var body = p + 8;
      if (body + (long)size > bytes.Length) throw new InvalidDataException("WAVE chunk is truncated.");
      if (bytes.AsSpan(p, 4).SequenceEqual("fmt "u8)) {
        if (size < 16) throw new InvalidDataException("WAVE fmt chunk is too short.");
        tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
        channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
        sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4));
        byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 8));
        blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 12));
        bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
        if (size >= 18) {
          var cbSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 16));
          if (18 + cbSize > size) throw new InvalidDataException("WAVE codec-private data is truncated.");
          extra = bytes.AsSpan(body + 18, cbSize).ToArray();
        }
        haveFmt = true;
      } else if (bytes.AsSpan(p, 4).SequenceEqual("data"u8)) {
        payload = bytes.AsSpan(body, size).ToArray();
      }
      p = checked(body + size + (size & 1));
    }
    if (!haveFmt || payload is null || channels == 0 || sampleRate == 0)
      throw new InvalidDataException("WAVE encoder output lacks usable fmt/data chunks.");
    return new WaveEnvelope(tag, channels, checked((int)sampleRate), byteRate, blockAlign, bits, extra, payload);
  }

  private static bool TryFormatTag(AudioStreamFormat format, out ushort tag) {
    if (format.Properties is not null && TryParseU16(Get(format.Properties, "format_tag"), out tag))
      return true;
    var codec = format.CodecId.ToLowerInvariant();
    tag = codec switch {
      "pcm" => 0x0001,
      "float" => 0x0003,
      "ms-adpcm" => 0x0002,
      "alaw" => 0x0006,
      "mulaw" => 0x0007,
      "wmavoice" => 0x000A,
      "oki-adpcm" => 0x0010,
      "ima-adpcm" => 0x0011,
      "dialogic-oki-adpcm" => 0x0017,
      "gsm610" => 0x0031,
      "g721" => 0x0040,
      "g726-16" or "g726-24" or "g726-32" or "g726-40" => 0x0045,
      "mp2" => 0x0050,
      "mp3" or "mpeg-layer3" => 0x0055,
      "g726-apicom-16" or "g726-apicom-24" or "g726-apicom-32" or "g726-apicom-40" => 0x0064,
      "g722-apicom" => 0x0065,
      "aac" or "aac-lc" or "aac-raw" => 0x00FF,
      "wmav1" => 0x0160,
      "wmav2" => 0x0161,
      "wmapro" => 0x0162,
      "wmalossless" => 0x0163,
      "g722" => 0x028F,
      "aac-adts" => 0x1600,
      "ac3" => 0x2000,
      "dts" => 0x2001,
      _ => 0,
    };
    if (tag != 0) return true;
    const string prefix = "wave-0x";
    return codec.StartsWith(prefix, StringComparison.Ordinal) &&
           ushort.TryParse(codec.AsSpan(prefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tag);
  }

  private static string CodecId(ushort tag, int bits) => tag switch {
    0x0001 => "pcm",
    0x0002 => "ms-adpcm",
    0x0003 => "float",
    0x0006 => "alaw",
    0x0007 => "mulaw",
    0x000A => "wmavoice",
    0x0010 => "oki-adpcm",
    0x0011 => "ima-adpcm",
    0x0017 => "dialogic-oki-adpcm",
    0x0031 => "gsm610",
    0x0040 => "g721",
    0x0045 => $"g726-{bits * 8}",
    0x0050 => "mp2",
    0x0055 => "mp3",
    0x0064 => $"g726-apicom-{bits * 8}",
    0x0065 => "g722-apicom",
    0x00FF => "aac",
    0x0160 => "wmav1",
    0x0161 => "wmav2",
    0x0162 => "wmapro",
    0x0163 => "wmalossless",
    0x028F => "g722",
    0x1600 => "aac-adts",
    0x0092 or 0x2000 => "ac3",
    0x2001 => "dts",
    _ => $"wave-0x{tag:X4}",
  };

  private static int DefaultBlockAlign(ushort tag, int channels, int bits) => tag switch {
    0x0001 or 0x0003 when bits > 0 => checked(channels * ((bits + 7) / 8)),
    0x0006 or 0x0007 => channels,
    _ => 1,
  };

  private static long DefaultByteRate(ushort tag, int sampleRate, int blockAlign, long payloadLength, IReadOnlyList<AudioPacket> packets) {
    if (tag is 0x0001 or 0x0003 or 0x0006 or 0x0007)
      return checked((long)sampleRate * Math.Max(1, blockAlign));
    var samples = packets.Sum(static packet => Math.Max(0, packet.DurationSamples));
    return samples > 0 ? Math.Max(1, payloadLength * sampleRate / samples) : 0;
  }

  private static int PropertyInt(IReadOnlyDictionary<string, string> properties, string key, int fallback)
    => int.TryParse(Get(properties, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

  private static long PropertyLong(IReadOnlyDictionary<string, string> properties, string key, long fallback)
    => long.TryParse(Get(properties, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

  private static IReadOnlyList<int> ParseObjectSizes(string text, int payloadLength) {
    if (string.IsNullOrWhiteSpace(text)) return payloadLength == 0 ? [] : [payloadLength];
    var sizes = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(static value => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
    if (sizes.Any(static size => size <= 0) || sizes.Sum(static size => (long)size) != payloadLength)
      throw new InvalidDataException("ASF media-object sizes are inconsistent with the demuxed payload.");
    return sizes;
  }

  private static Dictionary<string, string> ParseFlat(byte[] data) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var separator = line.IndexOf('=');
      if (separator <= 0) continue;
      result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }
    return result;
  }

  private static byte[] Extract(AsfFormatDescriptor descriptor, byte[] container, string name) {
    using var output = new MemoryStream();
    descriptor.ExtractEntry(new MemoryStream(container, writable: false), name, output, null);
    return output.ToArray();
  }

  private static int ParseStreamNumber(string name) {
    const string prefix = "streams/stream_";
    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
    var end = name.IndexOfAny(['.', '/'], prefix.Length);
    if (end < 0) end = name.Length;
    return int.TryParse(name.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value : 0;
  }

  private static string Get(IReadOnlyDictionary<string, string> values, string key)
    => values.TryGetValue(key, out var value) ? value : string.Empty;

  private static bool TryParseU16(string? text, out ushort value) {
    if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
      return ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static byte[] ParseHex(string text) {
    if (string.IsNullOrWhiteSpace(text)) return [];
    try { return Convert.FromHexString(text); }
    catch (FormatException exception) { throw new InvalidDataException("ASF codec-private data is invalid hexadecimal.", exception); }
  }

  private static byte[] Materialize(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }

  private sealed record WaveEnvelope(
    ushort FormatTag,
    ushort Channels,
    int SampleRate,
    uint ByteRate,
    ushort BlockAlign,
    ushort BitsPerSample,
    byte[] ExtraData,
    byte[] Payload);
}
