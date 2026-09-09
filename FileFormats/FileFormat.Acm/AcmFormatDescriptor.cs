#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.InterplayAcm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Acm;

/// <summary>
/// Interplay ACM audio (Fallout / Baldur's Gate era). The pseudo-archive exposes the
/// byte-exact <c>FULL.acm</c>, decoded mono channel WAVs and <c>metadata.ini</c>.
/// Creation accepts the original container for packet-preserving remux or PCM WAV input
/// for managed encoding. ACM geometry (<c>level</c>/<c>rows</c>) is configurable over
/// the complete encodable header range.
/// </summary>
public sealed class AcmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IFormatOptionsSchema,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] Codecs = ["interplay-acm", "acm"];

  public string Id => "Acm";
  public string DisplayName => "Interplay ACM (Fallout / Baldur's Gate audio)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".acm";
  public IReadOnlyList<string> Extensions => [".acm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x97, 0x28, 0x03, 0x01], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("interplay-acm", "Interplay ACM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Interplay ACM audio; managed PCM encode/decode plus packet-preserving remux.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Level", "Sub-band level", FormatOptionKind.Integer, "7",
      Enumerable.Range(0, 16).Select(static value => value.ToString(CultureInfo.InvariantCulture)).ToArray(),
      "ACM transform level (0..15). Higher levels use more sub-bands."),
    new("Rows", "Rows per block", FormatOptionKind.Integer, "16", null,
      "ACM rows per block (1..4095); very small values are invalid when mandatory block framing does not fit."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => AudioPseudoArchive.List(BuildEntries(stream));
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "ACM accepts FULL.acm, metadata.ini, one PCM WAV, or speaker-named mono PCM WAV channels.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.acm", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"not an ACM input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var metadataFile = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    var metadata = metadataFile.Data is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ParseMetadata(metadataFile.Data);

    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.acm", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      WriteRemuxedFull(output, full.Data, metadata, options);
      return;
    }

    var wavFiles = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .ToArray();
    if (wavFiles.Length == 0)
      throw new InvalidOperationException("ACM creation needs FULL.acm or PCM WAV input.");

    var (pcm, channels, sampleRate) = ReadWavInputs(wavFiles);
    var level = ResolveGeometry(options, metadata, "Level", "level", 7);
    var rows = ResolveGeometry(options, metadata, "Rows", "rows", 16);
    var samples = LePcmToShorts(pcm);
    InterplayAcmEncoder.Encode(samples, output, channels, sampleRate, level, rows);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not Interplay ACM";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "Interplay ACM encoder input must be signed PCM16";
      return false;
    }
    if (format.Channels is < 1 or > ushort.MaxValue) {
      reason = "Interplay ACM channel count must fit the unsigned 16-bit header field";
      return false;
    }
    if (format.SampleRate is < 1 or > ushort.MaxValue) {
      reason = "Interplay ACM sample rate must fit the unsigned 16-bit header field";
      return false;
    }
    var level = options.GetOptionInt("Level", 7);
    var rows = options.GetOptionInt("Rows", 16);
    if (!InterplayAcmEncoder.IsGeometryEncodable(level, rows)) {
      reason = $"ACM level={level}, rows={rows} cannot fit mandatory block framing";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    if (pcm.InterleavedData.Length % pcm.Format.BytesPerFrame != 0)
      throw new InvalidDataException("PCM payload is not frame-aligned.");

    var samples = LePcmToShorts(pcm.InterleavedData);
    var level = options.GetOptionInt("Level", 7);
    var rows = options.GetOptionInt("Rows", 16);
    InterplayAcmEncoder.Encode(samples, output, pcm.Format.Channels, pcm.Format.SampleRate, level, rows);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    var (samples, channels, sampleRate) = InterplayAcmCodec.Decode(blob);
    return new AudioPcmBuffer(new AudioPcmFormat(sampleRate, channels, 16), ShortsToLePcm(samples));
  }

  public IReadOnlyList<string> SupportedMuxCodecs => Codecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!Codecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"standalone ACM carries Interplay ACM, not codec '{stream.CodecId}'";
      return false;
    }
    if (stream.Channels is < 1 or > ushort.MaxValue || stream.SampleRate is < 1 or > ushort.MaxValue) {
      reason = "ACM channels/sample rate must fit unsigned 16-bit header fields";
      return false;
    }
    var level = PropertyInt(stream, "level") ?? options.GetOptionInt("Level", 7);
    var rows = PropertyInt(stream, "rows") ?? options.GetOptionInt("Rows", 16);
    if (!InterplayAcmEncoder.IsGeometryEncodable(level, rows)) {
      reason = $"ACM level={level}, rows={rows} cannot fit mandatory block framing";
      return false;
    }
    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    if (stream.CodecPrivateData is { Length: 14 } privateHeader) {
      InterplayAcmCodec.ParseHeader(privateHeader);
      output.Write(privateHeader);
    } else {
      var level = PropertyInt(stream.Format, "level") ?? options.GetOptionInt("Level", 7);
      var rows = PropertyInt(stream.Format, "rows") ?? options.GetOptionInt("Rows", 16);
      var totalSamples = PropertyUInt(stream.Format, "total-samples") ??
                         checked((uint)stream.Packets.Sum(static packet => Math.Max(0, packet.DurationSamples)));
      Span<byte> header = stackalloc byte[14];
      BinaryPrimitives.WriteUInt32LittleEndian(header, InterplayAcmCodec.Magic);
      BinaryPrimitives.WriteUInt32LittleEndian(header[4..], totalSamples);
      BinaryPrimitives.WriteUInt16LittleEndian(header[8..], checked((ushort)stream.Format.Channels));
      BinaryPrimitives.WriteUInt16LittleEndian(header[10..], checked((ushort)stream.Format.SampleRate));
      BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)((rows << 4) | level)));
      output.Write(header);
    }

    foreach (var packet in stream.Packets)
      if (!packet.IsHeader)
        output.Write(packet.Data);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    try {
      var header = InterplayAcmCodec.ParseHeader(blob);
      if (blob.Length < 14) {
        stream = null;
        return false;
      }
      var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["level"] = header.Level.ToString(CultureInfo.InvariantCulture),
        ["rows"] = header.Rows.ToString(CultureInfo.InvariantCulture),
        ["total-samples"] = header.TotalSamples.ToString(CultureInfo.InvariantCulture),
      };
      stream = new AudioEncodedStream(
        new AudioStreamFormat("interplay-acm", header.SampleRate <= 0 ? 22050 : header.SampleRate,
          Math.Max(1, header.Channels), 16, properties),
        [new AudioPacket(blob[14..], DurationSamples: header.TotalSamples)],
        blob[..14]);
      return true;
    } catch (Exception) {
      stream = null;
      return false;
    }
  }

  private static void WriteRemuxedFull(Stream output, byte[] source, IReadOnlyDictionary<string, string> metadata, FormatCreateOptions options) {
    var blob = (byte[])source.Clone();
    var header = InterplayAcmCodec.ParseHeader(blob);
    var requestedLevel = ResolveGeometry(options, metadata, "Level", "level", header.Level);
    var requestedRows = ResolveGeometry(options, metadata, "Rows", "rows", header.Rows);
    if (requestedLevel != header.Level || requestedRows != header.Rows)
      throw new InvalidOperationException("Changing ACM level/rows changes payload interpretation and requires PCM re-encoding; FULL.acm remux refuses that corruption.");

    if (TryMetadataUInt(metadata, "total_samples", out var totalSamples))
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), totalSamples);
    if (TryMetadataInt(metadata, "channels", out var channels)) {
      if (channels is < 1 or > ushort.MaxValue) throw new InvalidDataException("ACM channels must be 1..65535.");
      BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), (ushort)channels);
    }
    if (TryMetadataInt(metadata, "sample_rate", out var sampleRate)) {
      if (sampleRate is < 1 or > ushort.MaxValue) throw new InvalidDataException("ACM sample_rate must be 1..65535.");
      BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(10), (ushort)sampleRate);
    }
    output.Write(blob);
  }

  private static (byte[] Pcm, int Channels, int SampleRate) ReadWavInputs(IReadOnlyList<(string Name, byte[] Data)> files) {
    if (files.Count == 1) {
      var wav = new WavReader().ReadCanonicalPcm(files[0].Data);
      if (wav.FormatCode != 1 || wav.BitsPerSample is not (8 or 16 or 24 or 32))
        throw new InvalidOperationException("ACM creation accepts integer PCM WAV input.");
      var pcm = wav.BitsPerSample == 16 ? wav.InterleavedPcm : PcmCodec.Requantize(wav.InterleavedPcm, wav.BitsPerSample, 16);
      return (pcm, wav.NumChannels, wav.SampleRate);
    }

    var parsed = files.Select(static file => new WavReader().ReadCanonicalPcm(file.Data)).ToArray();
    if (parsed.Any(static wav => wav.NumChannels != 1 || wav.FormatCode != 1 || wav.BitsPerSample is not (8 or 16 or 24 or 32)))
      throw new InvalidOperationException("Multiple ACM WAV inputs must each be mono integer PCM.");
    var first = parsed[0];
    if (parsed.Any(wav => wav.SampleRate != first.SampleRate))
      throw new InvalidOperationException("All ACM channel WAVs must share the same sample rate.");

    var channels = parsed
      .Select(static wav => wav.BitsPerSample == 16 ? wav.InterleavedPcm : PcmCodec.Requantize(wav.InterleavedPcm, wav.BitsPerSample, 16))
      .ToArray();
    if (channels.Any(channel => channel.Length != channels[0].Length))
      throw new InvalidOperationException("All ACM channel WAVs must have the same frame count.");
    return (PcmCodec.Interleave(channels, 16), channels.Length, first.SampleRate);
  }

  private static int ResolveGeometry(FormatCreateOptions options, IReadOnlyDictionary<string, string> metadata,
      string optionKey, string metadataKey, int fallback) {
    if (options.TryGetInt(optionKey, out var optionValue))
      return optionValue;
    return TryMetadataInt(metadata, metadataKey, out var metadataValue) ? metadataValue : fallback;
  }

  private static Dictionary<string, string> ParseMetadata(byte[] bytes) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var rawLine in Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
        continue;
      var equals = line.IndexOf('=');
      if (equals > 0)
        result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }
    return result;
  }

  private static bool TryMetadataInt(IReadOnlyDictionary<string, string> metadata, string key, out int value) {
    value = 0;
    return metadata.TryGetValue(key, out var text) &&
           int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static bool TryMetadataUInt(IReadOnlyDictionary<string, string> metadata, string key, out uint value) {
    value = 0;
    return metadata.TryGetValue(key, out var text) &&
           uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static int? PropertyInt(AudioStreamFormat format, string key) =>
    format.Properties is { } properties && properties.TryGetValue(key, out var text) &&
    int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

  private static uint? PropertyUInt(AudioStreamFormat format, string key) =>
    format.Properties is { } properties && properties.TryGetValue(key, out var text) &&
    uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> { new("FULL.acm", "Container", blob, "interplay-acm") };
    AddDecodedChannels(blob, entries);
    entries.Add(new("metadata.ini", "Tag", BuildMetadata(blob), "stored"));
    return entries;
  }

  private static void AddDecodedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    try {
      var (samples, channels, sampleRate) = InterplayAcmCodec.Decode(blob);
      if (samples.Length == 0) return;
      var pcm = ShortsToLePcm(samples);
      if (channels <= 1)
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16), "interplay-acm"));
      else
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "interplay-acm"));
    } catch (Exception) {
      // Corrupt/unsupported input still exposes FULL.acm + metadata.ini.
    }
  }

  private static byte[] BuildMetadata(byte[] blob) {
    var sb = new StringBuilder("; Interplay ACM header\n");
    try {
      var h = InterplayAcmCodec.ParseHeader(blob);
      sb.Append("total_samples=").AppendLine(h.TotalSamples.ToString(CultureInfo.InvariantCulture));
      sb.Append("channels=").AppendLine(h.Channels.ToString(CultureInfo.InvariantCulture));
      sb.Append("sample_rate=").AppendLine(h.SampleRate.ToString(CultureInfo.InvariantCulture));
      sb.Append("level=").AppendLine(h.Level.ToString(CultureInfo.InvariantCulture));
      sb.Append("rows=").AppendLine(h.Rows.ToString(CultureInfo.InvariantCulture));
    } catch (Exception) {
      sb.AppendLine("; header could not be parsed");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static short[] LePcmToShorts(ReadOnlySpan<byte> pcm) {
    if ((pcm.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd byte length.");
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
    return samples;
  }
}
