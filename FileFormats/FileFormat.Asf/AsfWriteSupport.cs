#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.ALaw;
using Codec.MuLaw;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Asf;

/// <summary>Creation/remux plumbing for <see cref="AsfFormatDescriptor"/>.</summary>
internal static class AsfWriteSupport {
  private sealed record InputFile(string Name, byte[] Data);

  internal static string AcceptedInputsDescription =>
    "ASF accepts FULL.asf; metadata.ini; metadata/tags.ini; streams/stream_NN.info.txt + stream_NN.bin; " +
    "or WAV audio at the root / streams/stream_NN/*.wav. Fresh encoding supports PCM, IEEE float pass-through, G.711 A-law and mu-law; " +
    "other WAVEFORMATEX codecs are remuxed byte-exact from stream_NN.bin. " +
    "Video and mixed multi-track containers use the codec-preserving artifacts instead: " +
    "streams/stream_NN.properties.bin + stream_NN.bin, optional stream_NN.objects.csv, and metadata/preserved-header.bin.";

  internal static bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) {
      reason = null;
      return true;
    }

    var name = Normalize(input.ArchiveName);
    if (IsFull(name) || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("metadata/tags.ini", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
        TryStreamNumber(name, ".bin", out _) || TryStreamNumber(name, ".info.txt", out _) ||
        IsContainerArtifact(name)) {
      reason = null;
      return true;
    }

    reason = $"not an ASF mux/remux input ({input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  internal static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(static input => !input.IsDirectory)
      .Select(static input => new InputFile(Normalize(input.ArchiveName), input.ReadContent()))
      .ToList();

    var full = files.FirstOrDefault(static file => IsFull(file.Name));
    if (full is not null && files.All(static file => IsFull(file.Name))) {
      output.Write(full.Data);
      return;
    }

    var metadataIni = files.FirstOrDefault(static file => file.Name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    var tagsIni = files.FirstOrDefault(static file => file.Name.Equals("metadata/tags.ini", StringComparison.OrdinalIgnoreCase));
    var metadata = ParseMetadata(metadataIni?.Data, tagsIni?.Data);
    var packetSize = ParsePacketSize(metadataIni?.Data);
    var creationFileTime = ParseCreationFileTime(metadataIni?.Data);

    var streams = BuildStreams(files, packetSize);
    if (streams.Count == 0)
      throw new InvalidOperationException("ASF creation requires at least one audio stream.");

    var blob = AsfTrackWriter.Write(streams, metadata, packetSize, creationFileTime);
    output.Write(blob);
  }

  internal static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var current = CanonicalInputs(ReadAll(archive));
    var byName = current.ToDictionary(static input => Normalize(input.ArchiveName), StringComparer.OrdinalIgnoreCase);
    var replacements = inputs.Where(static input => !input.IsDirectory)
      .Select(static input => new InputFile(Normalize(input.ArchiveName), input.ReadContent()))
      .ToList();
    var suppliedNames = replacements.Select(static input => input.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var input in replacements) {
      var name = input.Name;
      if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
        var streamNumber = TryWaveStreamNumber(name, out var number) ? number : 1;
        var infoName = $"streams/stream_{streamNumber:D2}.info.txt";
        byName.Remove($"streams/stream_{streamNumber:D2}.bin");
        if (!suppliedNames.Contains(infoName))
          byName.Remove(infoName);
      } else if (TryStreamNumber(name, ".bin", out var streamNumber) &&
                 byName.TryGetValue(name, out var previous) &&
                 previous.ReadContent().Length != input.Data.Length) {
        var infoName = $"streams/stream_{streamNumber:D2}.info.txt";
        if (!suppliedNames.Contains(infoName) && byName.TryGetValue(infoName, out var info))
          byName[infoName] = ArchiveInputInfo.InMemory(infoName, RemoveFlatField(info.ReadContent(), "object_sizes"));
      }
      byName[name] = ArchiveInputInfo.InMemory(name, input.Data);
    }

    ReplaceArchive(archive, byName.Values.ToList());
  }

  internal static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    var current = CanonicalInputs(ReadAll(archive));
    var byName = current.ToDictionary(static input => Normalize(input.ArchiveName), StringComparer.OrdinalIgnoreCase);

    foreach (var rawName in entryNames ?? []) {
      var name = Normalize(rawName);
      byName.Remove(name);
      if (TryStreamNumber(name, ".bin", out var streamNumber) || TryStreamNumber(name, ".info.txt", out streamNumber)) {
        byName.Remove($"streams/stream_{streamNumber:D2}.bin");
        byName.Remove($"streams/stream_{streamNumber:D2}.info.txt");
      }
    }

    if (!byName.Keys.Any(static name => TryStreamNumber(name, ".bin", out _) || TryWaveStreamNumber(name, out _)))
      throw new NotSupportedException("ASF requires at least one stream; the last stream cannot be removed.");

    ReplaceArchive(archive, byName.Values.ToList());
  }

  internal static string RenderStreamInfo(
    AsfReader.StreamInfo stream,
    IReadOnlyDictionary<int, AsfDepayloader.StreamData>? depayloaded = null) {
    var text = new StringBuilder(stream.Render());
    if (stream.ByteRate is { } byteRate)
      text.AppendLine($"byte_rate = {byteRate}");
    if (stream.BlockAlign is { } blockAlign)
      text.AppendLine($"block_align = {blockAlign}");
    if (stream.ExtraData is { Length: > 0 } extra)
      text.AppendLine($"extra_data_hex = {Convert.ToHexString(extra)}");
    if (depayloaded is not null && depayloaded.TryGetValue(stream.StreamNumber, out var data) && data.Objects.Count > 0)
      text.AppendLine($"object_sizes = {string.Join(',', data.Objects.Select(static item => item.Data.Length))}");
    return text.ToString();
  }

  internal static Dictionary<int, AsfDepayloader.StreamData> DepayloadWithBoundaries(AsfReader.Parsed parsed) {
    if (parsed.DataPayload is not { Length: > 0 } payload)
      return [];
    var packetSize = parsed.MinPacketSize.HasValue && parsed.MinPacketSize == parsed.MaxPacketSize
      ? checked((int)parsed.MinPacketSize.Value)
      : checked((int)(parsed.MaxPacketSize ?? 0));
    return AsfDepayloader.Depayload(payload, packetSize);
  }

  private static List<AsfTrackWriter.AudioStream> BuildStreams(List<InputFile> files, int packetSize) {
    var infos = new Dictionary<int, Dictionary<string, string>>();
    var raw = new Dictionary<int, InputFile>();
    var wavGroups = new Dictionary<int, List<InputFile>>();

    foreach (var file in files) {
      if (TryStreamNumber(file.Name, ".info.txt", out var infoNumber)) {
        infos[infoNumber] = ParseFlat(file.Data);
        continue;
      }
      if (TryStreamNumber(file.Name, ".bin", out var rawNumber)) {
        raw[rawNumber] = file;
        continue;
      }
      if (file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
        var number = TryWaveStreamNumber(file.Name, out var n) ? n : 1;
        if (!wavGroups.TryGetValue(number, out var list))
          wavGroups[number] = list = [];
        list.Add(file);
      }
    }

    var numbers = raw.Keys.Concat(wavGroups.Keys).Distinct().Order().ToList();
    if (numbers.Any(static number => number is < 1 or > 127))
      throw new InvalidDataException("ASF stream numbers must be in 1..127.");

    var result = new List<AsfTrackWriter.AudioStream>(numbers.Count);
    foreach (var number in numbers) {
      infos.TryGetValue(number, out var info);
      if (raw.TryGetValue(number, out var rawInput)) {
        if (info is null)
          throw new InvalidOperationException($"Raw ASF stream {number} requires streams/stream_{number:D2}.info.txt.");
        result.Add(BuildRawStream(number, rawInput.Data, info));
      } else {
        result.Add(BuildWaveStream(number, wavGroups[number], info, packetSize));
      }
    }
    return result;
  }

  private static AsfTrackWriter.AudioStream BuildRawStream(int number, byte[] payload, Dictionary<string, string> info) {
    if (Get(info, "type") is { Length: > 0 } kind && !kind.Equals("audio", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"ASF writer currently muxes audio stream properties; stream {number} is '{kind}'. FULL.asf pass-through still preserves it.");
    if (ParseBool(Get(info, "encrypted")))
      throw new NotSupportedException("ASF encrypted-stream remux requires preserving FULL.asf; the writer does not synthesize DRM/encryption objects.");

    var tag = ParseU16Required(info, "format_tag");
    var channels = ParseU16Required(info, "channels");
    var sampleRate = ParseU32Required(info, "sample_rate");
    var byteRate = TryParseU32(Get(info, "byte_rate"), out var br)
      ? br
      : TryParseU64(Get(info, "bitrate"), out var bitrate)
        ? checked((uint)Math.Min(uint.MaxValue, bitrate / 8))
        : 0;
    var blockAlign = TryParseU16(Get(info, "block_align"), out var ba) ? ba : (ushort)0;
    var bits = TryParseU16(Get(info, "bits_per_sample"), out var bps) ? bps : (ushort)0;
    var extra = ParseHex(Get(info, "extra_data_hex"));
    var objectSizes = ParseObjectSizes(Get(info, "object_sizes"), payload.Length);

    return new AsfTrackWriter.AudioStream(number, tag, channels, sampleRate, byteRate, blockAlign, bits, extra, payload, objectSizes);
  }

  private static AsfTrackWriter.AudioStream BuildWaveStream(
    int number,
    List<InputFile> inputs,
    Dictionary<string, string>? info,
    int packetSize) {
    if (inputs.Count == 0)
      throw new InvalidOperationException($"ASF stream {number} has no WAV inputs.");

    inputs.Sort(static (a, b) => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(a.Name))
      .CompareTo(ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(b.Name))));

    var requestedTag = info is null || !TryParseU16(Get(info, "format_tag"), out var parsedTag)
      ? (ushort)0
      : parsedTag;

    if (inputs.Count == 1) {
      var direct = new WavReader().Read(inputs[0].Data);
      var directTag = requestedTag == 0 ? checked((ushort)direct.FormatCode) : requestedTag;
      if (directTag is 0x0003 && direct.FormatCode == 0x0003)
        return BuildDirectWave(number, directTag, direct, packetSize);
      if (directTag is 0x0006 or 0x0007 && direct.FormatCode == directTag)
        return BuildDirectWave(number, directTag, direct, packetSize);
    }

    var channels = inputs.Select(static input => new WavReader().ReadCanonicalPcm(input.Data)).ToList();
    if (channels.Any(static channel => channel.FormatCode != 1))
      throw new NotSupportedException("ASF fresh encoding requires integer PCM source samples (or a single IEEE-float/G.711 WAV for direct pass-through).");

    byte[] interleaved;
    int channelCount;
    var first = channels[0];
    if (channels.Count == 1) {
      interleaved = first.InterleavedPcm;
      channelCount = first.NumChannels;
    } else {
      if (channels.Any(c => c.NumChannels != 1 || c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample ||
                            c.InterleavedPcm.Length != first.InterleavedPcm.Length))
        throw new InvalidOperationException("Per-channel ASF WAV inputs must be mono and share sample rate, bit depth and frame count.");
      interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), first.BitsPerSample);
      channelCount = channels.Count;
    }

    if (channelCount is < 1 or > ushort.MaxValue)
      throw new InvalidDataException("ASF WAVEFORMATEX channel count exceeds 16 bits.");
    if (first.BitsPerSample is not (8 or 16 or 24 or 32))
      throw new NotSupportedException($"ASF PCM/G.711 encoder supports 8/16/24/32-bit integer PCM input; got {first.BitsPerSample} bits.");

    var tag = requestedTag == 0 ? (ushort)0x0001 : requestedTag;
    byte[] payload;
    ushort bits;
    ushort blockAlign;
    uint byteRate;
    switch (tag) {
      case 0x0001:
        payload = interleaved;
        bits = checked((ushort)first.BitsPerSample);
        blockAlign = checked((ushort)(channelCount * (first.BitsPerSample / 8)));
        byteRate = checked((uint)(first.SampleRate * blockAlign));
        break;
      case 0x0006:
      case 0x0007: {
        var samples = ToPcm16(interleaved, first.BitsPerSample);
        payload = tag == 0x0006 ? ALawCodec.Encode(samples) : MuLawCodec.Encode(samples);
        bits = 8;
        blockAlign = checked((ushort)channelCount);
        byteRate = checked((uint)(first.SampleRate * channelCount));
        break;
      }
      case 0x0003:
        throw new NotSupportedException("IEEE-float ASF creation is pass-through only: provide one IEEE-float WAV rather than integer PCM.");
      default:
        throw new NotSupportedException(
          $"No managed encoder is available for WAVEFORMATEX tag 0x{tag:X4}. Keep stream_{number:D2}.bin for byte-exact remux, or choose PCM/A-law/mu-law.");
    }

    return new AsfTrackWriter.AudioStream(number, tag, checked((ushort)channelCount), checked((uint)first.SampleRate),
      byteRate, blockAlign, bits, [], payload, ChunkObjectSizes(payload.Length, blockAlign, packetSize));
  }

  private static AsfTrackWriter.AudioStream BuildDirectWave(int number, ushort tag, WavReader.ParsedWav wav, int packetSize) {
    if (wav.NumChannels is < 1 or > ushort.MaxValue || wav.SampleRate <= 0)
      throw new InvalidDataException("WAV parameters do not fit ASF WAVEFORMATEX.");
    var bytesPerSample = Math.Max(1, (wav.BitsPerSample + 7) / 8);
    var blockAlign = checked((ushort)(wav.NumChannels * (tag is 0x0006 or 0x0007 ? 1 : bytesPerSample)));
    var bits = checked((ushort)(tag is 0x0006 or 0x0007 ? 8 : wav.BitsPerSample));
    var byteRate = checked((uint)(wav.SampleRate * blockAlign));
    return new AsfTrackWriter.AudioStream(number, tag, checked((ushort)wav.NumChannels), checked((uint)wav.SampleRate),
      byteRate, blockAlign, bits, [], wav.InterleavedPcm,
      ChunkObjectSizes(wav.InterleavedPcm.Length, blockAlign, packetSize));
  }

  private static IReadOnlyList<int> ChunkObjectSizes(int length, int blockAlign, int packetSize) {
    if (length == 0)
      return [];
    var target = Math.Max(blockAlign, Math.Min(16 * 1024, packetSize * 4));
    if (blockAlign > 0)
      target -= target % blockAlign;
    if (target <= 0)
      target = Math.Max(1, blockAlign);
    var result = new List<int>((length + target - 1) / target);
    for (var offset = 0; offset < length; offset += target)
      result.Add(Math.Min(target, length - offset));
    return result;
  }

  private static short[] ToPcm16(ReadOnlySpan<byte> pcm, int bitsPerSample) {
    var bytesPerSample = bitsPerSample / 8;
    if (bytesPerSample <= 0 || pcm.Length % bytesPerSample != 0)
      throw new InvalidDataException("PCM byte count is not aligned to the declared sample width.");
    var result = new short[pcm.Length / bytesPerSample];
    for (var i = 0; i < result.Length; ++i) {
      var offset = i * bytesPerSample;
      result[i] = bitsPerSample switch {
        8 => (short)((pcm[offset] - 128) << 8),
        16 => BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]),
        24 => (short)(ReadInt24LittleEndian(pcm[offset..]) >> 8),
        32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(pcm[offset..]) >> 16),
        _ => throw new NotSupportedException($"PCM sample width {bitsPerSample} is not supported for G.711 encoding."),
      };
    }
    return result;
  }

  private static int ReadInt24LittleEndian(ReadOnlySpan<byte> data) {
    var value = data[0] | data[1] << 8 | data[2] << 16;
    return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
  }

  private static List<ArchiveInputInfo> CanonicalInputs(byte[] blob) {
    var parsed = AsfReader.Parse(blob);
    if (parsed.Streams.Any(static stream => stream.Kind != "audio"))
      throw new NotSupportedException(
        "Structured ASF remux/edit currently supports audio-only ASF. Containers carrying video or other stream types must be preserved through FULL.asf pass-through.");

    var depayloaded = DepayloadWithBoundaries(parsed);
    var result = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(parsed.RenderMetadataIni())),
    };
    if (parsed.ExtendedTags.Count > 0)
      result.Add(ArchiveInputInfo.InMemory("metadata/tags.ini", Encoding.UTF8.GetBytes(parsed.RenderTagsIni())));

    foreach (var stream in parsed.Streams.Where(static stream => stream.Kind == "audio")) {
      result.Add(ArchiveInputInfo.InMemory($"streams/stream_{stream.StreamNumber:D2}.info.txt",
        Encoding.UTF8.GetBytes(RenderStreamInfo(stream, depayloaded))));
      if (parsed.StreamPayloads.TryGetValue(stream.StreamNumber, out var payload))
        result.Add(ArchiveInputInfo.InMemory($"streams/stream_{stream.StreamNumber:D2}.bin", payload));
    }
    return result;
  }

  private static void ReplaceArchive(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    using var staged = new MemoryStream();
    Create(staged, inputs, new FormatCreateOptions());
    staged.Position = 0;
    if (!archive.CanSeek || !archive.CanWrite)
      throw new NotSupportedException("ASF remux editing requires a seekable writable stream.");
    archive.Position = 0;
    archive.SetLength(0);
    staged.CopyTo(archive);
    archive.Position = 0;
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek)
      stream.Position = 0;
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static AsfTrackWriter.Metadata ParseMetadata(byte[]? metadataIni, byte[]? tagsIni) {
    var metadata = ParseIni(metadataIni);
    var tags = ParseIni(tagsIni)
      .Where(static pair => pair.Key.Section.Equals("ExtendedContentDescription", StringComparison.OrdinalIgnoreCase))
      .Select(static pair => (pair.Key.Key, pair.Value))
      .ToList();
    return new AsfTrackWriter.Metadata(
      Get(metadata, "ContentDescription", "title"),
      Get(metadata, "ContentDescription", "author"),
      Get(metadata, "ContentDescription", "copyright"),
      Get(metadata, "ContentDescription", "description"),
      Get(metadata, "ContentDescription", "rating"),
      tags);
  }

  private static int ParsePacketSize(byte[]? metadataIni) {
    var metadata = ParseIni(metadataIni);
    if (int.TryParse(Get(metadata, "FileProperties", "max_packet_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) &&
        size is >= AsfTrackWriter.MinPacketSize and <= AsfTrackWriter.MaxPacketSize)
      return size;
    return AsfTrackWriter.DefaultPacketSize;
  }

  private static long? ParseCreationFileTime(byte[]? metadataIni) {
    var metadata = ParseIni(metadataIni);
    return long.TryParse(Get(metadata, "FileProperties", "creation_date_filetime"), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out var value) ? value : null;
  }

  private static Dictionary<(string Section, string Key), string> ParseIni(byte[]? data) {
    var result = new Dictionary<(string, string), string>();
    if (data is null)
      return result;
    var section = "";
    foreach (var raw in Encoding.UTF8.GetString(data).Split('\n')) {
      var line = raw.Trim().TrimEnd('\r');
      if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
        continue;
      if (line is ['[', ..] && line.EndsWith(']')) {
        section = line[1..^1].Trim();
        continue;
      }
      var separator = line.IndexOf('=');
      if (separator < 0)
        continue;
      result[(section, line[..separator].Trim())] = line[(separator + 1)..].Trim();
    }
    return result;
  }

  private static Dictionary<string, string> ParseFlat(byte[] data) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in Encoding.UTF8.GetString(data).Split('\n')) {
      var line = raw.Trim().TrimEnd('\r');
      var separator = line.IndexOf('=');
      if (separator < 0)
        continue;
      result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }
    return result;
  }

  private static byte[] RemoveFlatField(byte[] data, string key) {
    var lines = Encoding.UTF8.GetString(data).Split('\n');
    var filtered = lines.Where(line => {
      var trimmed = line.Trim().TrimEnd('\r');
      var separator = trimmed.IndexOf('=');
      return separator < 0 || !trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    });
    return Encoding.UTF8.GetBytes(string.Join('\n', filtered));
  }

  private static string Get(Dictionary<(string Section, string Key), string> ini, string section, string key) {
    foreach (var (entry, value) in ini)
      if (entry.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        return value;
    return "";
  }

  private static string? Get(Dictionary<string, string> values, string key)
    => values.TryGetValue(key, out var value) ? value : null;

  private static ushort ParseU16Required(Dictionary<string, string> values, string key) {
    if (!TryParseU16(Get(values, key), out var value))
      throw new InvalidDataException($"ASF stream info requires a valid {key} field.");
    return value;
  }

  private static uint ParseU32Required(Dictionary<string, string> values, string key) {
    if (!TryParseU32(Get(values, key), out var value))
      throw new InvalidDataException($"ASF stream info requires a valid {key} field.");
    return value;
  }

  private static bool TryParseU16(string? text, out ushort value) {
    if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
      return ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryParseU32(string? text, out uint value) {
    if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
      return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryParseU64(string? text, out ulong value) {
    if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
      return ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static bool ParseBool(string? text)
    => bool.TryParse(text, out var value) && value;

  private static byte[] ParseHex(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return [];
    try { return Convert.FromHexString(text); }
    catch (FormatException exception) { throw new InvalidDataException("ASF stream info extra_data_hex is not valid hexadecimal.", exception); }
  }

  private static IReadOnlyList<int>? ParseObjectSizes(string? text, int payloadLength) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    var values = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(static token => int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture))
      .ToList();
    if (values.Any(static value => value <= 0) || values.Sum(static value => (long)value) != payloadLength)
      throw new InvalidDataException("ASF object_sizes must be positive and sum exactly to stream_NN.bin length.");
    return values;
  }

  /// <summary>
  /// Reports whether a write input belongs to the codec-preserving container vocabulary rather
  /// than the audio-oriented one. These artifacts are what carry a video or mixed multi-track
  /// container through a rebuild, so the descriptor routes them to <see cref="AsfRemuxer"/>.
  /// </summary>
  internal static bool IsContainerArtifact(string name) {
    var normalized = Normalize(name);
    return normalized.Equals(AsfRemuxer.PreservedHeaderPath, StringComparison.OrdinalIgnoreCase)
      || TryStreamNumber(normalized, ".properties.bin", out _)
      || TryStreamNumber(normalized, ".objects.csv", out _);
  }

  private static bool IsFull(string name)
    => Path.GetFileName(name).Equals("FULL.asf", StringComparison.OrdinalIgnoreCase) ||
       Path.GetFileName(name).Equals("FULL.wma", StringComparison.OrdinalIgnoreCase) ||
       Path.GetFileName(name).Equals("FULL.wmv", StringComparison.OrdinalIgnoreCase);

  private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

  private static bool TryWaveStreamNumber(string name, out int streamNumber) {
    var normalized = Normalize(name);
    const string prefix = "streams/stream_";
    // The prefix test has to come first: it is what guarantees the string is long enough
    // for the separator search to start inside it.
    if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      streamNumber = 0;
      return false;
    }
    var slash = normalized.IndexOf('/', prefix.Length);
    if (slash < 0) {
      streamNumber = 0;
      return false;
    }
    return int.TryParse(normalized.AsSpan(prefix.Length, slash - prefix.Length), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out streamNumber);
  }

  private static bool TryStreamNumber(string name, string suffix, out int streamNumber) {
    var normalized = Normalize(name);
    const string prefix = "streams/stream_";
    if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
      streamNumber = 0;
      return false;
    }
    var length = normalized.Length - prefix.Length - suffix.Length;
    if (length <= 0) {
      streamNumber = 0;
      return false;
    }
    return int.TryParse(normalized.AsSpan(prefix.Length, length), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out streamNumber);
  }
}
